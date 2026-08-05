#if ENABLE_TRAINING

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Segra.Backend.Detection;
using Serilog;

namespace Segra.Backend.Training;

public static class TrainingEventService
{
    private static readonly string BasePath =
        Path.Combine(AppContext.BaseDirectory, "data", "training");

    private static readonly ConcurrentDictionary<string, List<EventDefinition>> _definitions = new();
    private static readonly ConcurrentDictionary<string, List<TrainingSample>> _samples = new();
    private static readonly ConcurrentDictionary<string, InferenceSession?> _models = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static int _nextEventId = Environment.TickCount;

    // --- Event Definitions ---

    public static List<EventDefinition> LoadEventDefinitions(string gameId)
    {
        if (_definitions.TryGetValue(gameId, out var cached))
            return cached;

        var path = Path.Combine(GetGamePath(gameId), "events.json");
        if (!File.Exists(path))
        {
            _definitions[gameId] = new List<EventDefinition>();
            return _definitions[gameId];
        }

        var json = File.ReadAllText(path);
        var events = JsonSerializer.Deserialize<List<EventDefinition>>(json, _jsonOptions) ?? new List<EventDefinition>();
        _definitions[gameId] = events;
        return events;
    }

    public static void SaveEventDefinitions(string gameId, List<EventDefinition> events)
    {
        var dir = GetGamePath(gameId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "events.json");
        var json = JsonSerializer.Serialize(events, _jsonOptions);
        File.WriteAllText(path, json);
        _definitions[gameId] = events;
    }

    public static void AddEventDefinition(string gameId, EventDefinition evt)
    {
        var events = LoadEventDefinitions(gameId);
        var existing = events.FindIndex(e => e.Id == evt.Id);
        if (existing >= 0)
        {
            events[existing] = evt;
        }
        else
        {
            if (evt.Id <= 0)
                evt.Id = Interlocked.Increment(ref _nextEventId);
            events.Add(evt);
        }
        SaveEventDefinitions(gameId, events);
    }

    public static void RemoveEventDefinition(string gameId, int eventId)
    {
        var events = LoadEventDefinitions(gameId);
        events.RemoveAll(e => e.Id == eventId);
        SaveEventDefinitions(gameId, events);
    }

    // --- Samples ---

    public static List<TrainingSample> GetSamples(string gameId)
    {
        if (_samples.TryGetValue(gameId, out var cached))
            return cached;

        var samplesPath = GetSamplesPath(gameId);
        if (!Directory.Exists(samplesPath))
        {
            _samples[gameId] = new List<TrainingSample>();
            return _samples[gameId];
        }

        var samples = new List<TrainingSample>();
        foreach (var labelFile in Directory.GetFiles(samplesPath, "*.txt"))
        {
            var imageFile = Path.ChangeExtension(labelFile, ".png");
            if (!File.Exists(imageFile))
                continue;

            var name = Path.GetFileNameWithoutExtension(labelFile);
            var parts = name.Split('_', 2);
            int eventId = 0;
            if (parts.Length >= 2)
                int.TryParse(parts[0], out eventId);

            samples.Add(new TrainingSample
            {
                ImagePath = imageFile,
                LabelPath = labelFile,
                EventId = eventId,
                Timestamp = TimeSpan.Zero,
                RecordingFile = string.Empty
            });
        }

        _samples[gameId] = samples;
        return samples;
    }

    public static void AddSample(string gameId, TrainingSample sample, byte[] imageData)
    {
        var samplesPath = GetSamplesPath(gameId);
        Directory.CreateDirectory(samplesPath);

        var timestamp = DateTime.UtcNow.Ticks;
        var fileName = $"{sample.EventId}_{timestamp}";
        var imagePath = Path.Combine(samplesPath, $"{fileName}.png");
        var labelPath = Path.Combine(samplesPath, $"{fileName}.txt");

        File.WriteAllBytes(imagePath, imageData);

        sample.ImagePath = imagePath;
        sample.LabelPath = labelPath;
        sample.Timestamp = TimeSpan.FromTicks(timestamp);

        // Write YOLO label file with proper normalized coordinates
        var inv = CultureInfo.InvariantCulture;
        var labelContent = $"{sample.EventId} {sample.BoxX.ToString(inv)} {sample.BoxY.ToString(inv)} {sample.BoxW.ToString(inv)} {sample.BoxH.ToString(inv)}";
        File.WriteAllText(labelPath, labelContent);

        var samples = GetSamples(gameId);
        samples.Add(sample);
        _samples[gameId] = samples;
    }

    public static int GetSampleCount(string gameId)
    {
        return GetSamples(gameId).Count;
    }

    public static int GetSampleCountForEvent(string gameId, int eventId)
    {
        return GetSamples(gameId).Count(s => s.EventId == eventId);
    }

    public static void DeleteSample(string gameId, string imagePath)
    {
        var labelPath = Path.ChangeExtension(imagePath, ".txt");
        try { if (File.Exists(imagePath)) File.Delete(imagePath); } catch { }
        try { if (File.Exists(labelPath)) File.Delete(labelPath); } catch { }

        if (_samples.TryGetValue(gameId, out var cached))
            cached.RemoveAll(s => s.ImagePath == imagePath);
    }

    // --- Dataset Export ---

    public static void ExportDataset(string gameId)
    {
        var gamePath = GetGamePath(gameId);
        var datasetPath = Path.Combine(gamePath, "dataset");
        var imagesTrainPath = Path.Combine(datasetPath, "images", "train");
        var labelsTrainPath = Path.Combine(datasetPath, "labels", "train");
        var imagesValPath = Path.Combine(datasetPath, "images", "val");
        var labelsValPath = Path.Combine(datasetPath, "labels", "val");
        Directory.CreateDirectory(imagesTrainPath);
        Directory.CreateDirectory(labelsTrainPath);
        Directory.CreateDirectory(imagesValPath);
        Directory.CreateDirectory(labelsValPath);

        var definitions = LoadEventDefinitions(gameId);
        var sorted = definitions.OrderBy(e => e.Id).ToList();
        var classMap = sorted.Select((e, i) => (e.Id, Index: i))
            .ToDictionary(x => x.Id, x => x.Index);

        var samples = GetSamples(gameId);
        var rng = new Random(42);
        var shuffled = samples.OrderBy(_ => rng.Next()).ToList();
        var valCount = Math.Max(1, shuffled.Count / 5);
        var valSet = shuffled.Take(valCount).ToHashSet();

        var classNames = sorted
            .Select(e => $"'{e.Name}'")
            .ToList();

        void CopySample(TrainingSample sample, string imagesDir, string labelsDir, int idx)
        {
            if (!File.Exists(sample.ImagePath) || !File.Exists(sample.LabelPath))
                return;

            var destImage = Path.Combine(imagesDir, $"{idx:D6}.png");
            var destLabel = Path.Combine(labelsDir, $"{idx:D6}.txt");

            var lines = File.ReadAllLines(sample.LabelPath)
                .Select(l => l.Trim().Replace(',', '.'))
                .Where(l => l.Length > 0)
                .ToList();

            if (lines.Count == 0) return;

            // Remap class IDs
            var remapped = new List<string>();
            foreach (var line in lines)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (!int.TryParse(parts[0], out var rawEventId)) continue;
                if (!classMap.TryGetValue(rawEventId, out var classIdx)) continue;
                parts[0] = classIdx.ToString();
                remapped.Add(string.Join(" ", parts));
            }
            if (remapped.Count == 0) return;

            File.Copy(sample.ImagePath, destImage, true);
            File.WriteAllLines(destLabel, remapped);
        }

        int trainIdx = 0, valIdx = 0;
        int exportedCount = 0, skipCount = 0;
        foreach (var sample in shuffled)
        {
            try
            {
                if (valSet.Contains(sample))
                    CopySample(sample, imagesValPath, labelsValPath, valIdx++);
                else
                    CopySample(sample, imagesTrainPath, labelsTrainPath, trainIdx++);
                exportedCount++;
            }
            catch (Exception ex)
            {
                skipCount++;
                Log.Warning(ex, "ExportDataset: skipped sample {Path} ({Reason})", sample.ImagePath, ex.Message);
            }
        }

        var yamlLines = new List<string>
        {
            "train: ./images/train",
            "val: ./images/val",
            $"nc: {classNames.Count}",
            $"names: [{string.Join(", ", classNames)}]"
        };
        var yamlPath = Path.Combine(datasetPath, "dataset.yaml");
        File.WriteAllText(yamlPath, string.Join("\n", yamlLines));
    }

    // --- Model ---

    public static bool HasModelForGame(string gameId)
    {
        return File.Exists(GetModelPath(gameId));
    }

    public static InferenceSession? LoadModel(string gameId)
    {
        if (_models.TryGetValue(gameId, out var existing))
            return existing;

        var modelPath = GetModelPath(gameId);
        if (!File.Exists(modelPath))
        {
            _models[gameId] = null;
            return null;
        }

        var opts = new SessionOptions();
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        var session = new InferenceSession(modelPath, opts);
        _models[gameId] = session;
        return session;
    }

    public static void UnloadModel(string gameId)
    {
        if (_models.TryRemove(gameId, out var session))
            session?.Dispose();
    }

    // --- Path helpers ---

    public static string GetGamePath(string gameId)
    {
        return Path.Combine(BasePath, gameId);
    }

    public static string GetSamplesPath(string gameId)
    {
        return Path.Combine(GetGamePath(gameId), "samples");
    }

    public static string GetModelPath(string gameId)
    {
        return Path.Combine(GetGamePath(gameId), "model.onnx");
    }
}
#endif
