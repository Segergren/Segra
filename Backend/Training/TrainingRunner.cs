#if ENABLE_TRAINING_EVENTS

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Segra.Backend.App;
using Serilog;

namespace Segra.Backend.Training;

public static class TrainingRunner
{
    private static string? _venvPython;

    public static string? FindPython()
    {
        if (_venvPython != null) return _venvPython;

        // Check project .venv first
        var venvPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".venv", "Scripts", "python.exe");
        if (File.Exists(venvPath))
        {
            _venvPython = venvPath;
            return _venvPython;
        }

        foreach (var candidate in new[] { "python", "python3", "py" })
        {
            try
            {
                var psi = new ProcessStartInfo(candidate, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p != null && p.WaitForExit(2000) && p.ExitCode == 0)
                {
                    _venvPython = candidate;
                    return _venvPython;
                }
            }
            catch { }
        }
        return null;
    }

    public static async Task RunTraining(string gameId, string datasetDir)
    {
        var python = FindPython();
        if (python == null)
        {
            await MessageService.SendFrontendMessage("TrainingProgress", new
            {
                gameId,
                status = "error",
                message = "Python not found. Install Python with PyTorch and run: python export_and_train.py " + datasetDir
            });
            return;
        }

        var gamePath = TrainingEventService.GetGamePath(gameId);

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "export_and_train.py");
        if (!File.Exists(scriptPath))
        {
            scriptPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "scripts", "export_and_train.py");
        }

        if (!File.Exists(scriptPath))
        {
            await MessageService.SendFrontendMessage("TrainingProgress", new
            {
                gameId,
                status = "error",
                message = $"Training script not found. Expected at: {scriptPath}"
            });
            return;
        }

        var psi = new ProcessStartInfo(python, $"\"{scriptPath}\" \"{gamePath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var progressLineCount = 0;
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            progressLineCount++;
            var isProgress = e.Data.Contains("epoch", StringComparison.OrdinalIgnoreCase)
                || e.Data.Contains("train", StringComparison.OrdinalIgnoreCase)
                || e.Data.Contains("Training", StringComparison.OrdinalIgnoreCase);
            _ = MessageService.SendFrontendMessage("TrainingProgress", new
            {
                gameId,
                status = isProgress ? "training" : "info",
                message = e.Data,
                line = progressLineCount
            });
        };
        var errorLines = new List<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            errorLines.Add(e.Data);
            _ = MessageService.SendFrontendMessage("TrainingProgress", new
            {
                gameId,
                status = "error",
                message = e.Data,
                line = progressLineCount
            });
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        if (process.ExitCode == 0)
        {
            var trainedModel = Path.Combine(gamePath, "model.onnx");
            if (!File.Exists(trainedModel))
            {
                // export_and_train.py places model.onnx in the game directory
                trainedModel = Path.Combine(gamePath, "dataset", "model.onnx");
            }

            var gameModel = TrainingEventService.GetModelPath(gameId);
            if (File.Exists(trainedModel))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(gameModel)!);
                File.Copy(trainedModel, gameModel, true);

                // Secure backup to project root (outside build output, survives clean)
                try
                {
                    var projectDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
                    var backupPath = Path.Combine(projectDir, "data", "training", gameId, "model.onnx");
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(trainedModel, backupPath, true);
                    Log.Information("TrainingRunner: backed up model to {Path}", backupPath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "TrainingRunner: model backup failed (non-fatal)");
                }
            }

            TrainingEventService.UnloadModel(gameId);
            var modelLoaded = TrainingEventService.LoadModel(gameId) != null;
            await MessageService.SendFrontendMessage("TrainingProgress", new
            {
                gameId,
                status = "completed",
                message = modelLoaded
                    ? "Training complete! Model loaded and ready."
                    : "Training complete but model could not be loaded. Try 'Load Model'.",
                success = true
            });
        }
        else
        {
            var errDetail = errorLines.Count > 0 ? string.Join("\n", errorLines.TakeLast(5)) : "No error output captured.";
            await MessageService.SendFrontendMessage("TrainingProgress", new
            {
                gameId,
                status = "error",
                message = $"Training failed with exit code {process.ExitCode}.\n{errDetail}",
                success = false
            });
        }
    }
}
#endif
