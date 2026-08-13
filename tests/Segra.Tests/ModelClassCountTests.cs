using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

// The class count used to come from events.json's entry count, while ParseYoloOutput strides the
// output tensor by 4 + numClasses. Adding or removing a single events.json entry without
// retraining therefore shifted every read and decoded every box to garbage — no exception, no log.
// These tests pin the replacement: the count comes from the exported graph's output shape, and
// events.json is checked against the model's own class map at load.
public class ModelClassCountTests
{
    private const string GameId = "Overwatch";

    // The literal Ultralytics writes into metadata_props["names"]: a Python dict, not JSON.
    private const string UltralyticsNames =
        "{0: 'Elimination', 1: 'Death Spectating', 2: 'Turret', 3: 'Kill Cam', 4: 'Assist', " +
        "5: 'POTG', 6: 'Steel Trap'}";

    private static EventDefinition Def(int classId, string name) =>
        new() { Id = classId, ClassId = classId, Name = name, Type = EventType.Trigger };

    private static List<EventDefinition> ShippedDefinitions() =>
    [
        Def(0, "Elimination"),
        Def(1, "Death Spectating"),
        Def(2, "Turret"),
        Def(3, "Kill Cam"),
        Def(4, "Assist"),
        Def(5, "POTG"),
        Def(6, "Steel Trap"),
    ];

    // [batch, 4 + numClasses, numAnchors].
    [Theory]
    [InlineData(true, 7, new[] { 1, 11, 8400 })]
    [InlineData(true, 1, new[] { 1, 5, 8400 })]
    // A dynamic axis exports as -1, or 0 on older exporters. Deriving from either produces a
    // stride that looks plausible, so the shape has to be reported as saying nothing.
    [InlineData(false, 0, new[] { 1, -1, 8400 })]
    [InlineData(false, 0, new[] { 1, 0, 8400 })]
    // Box rows only, or fewer: no class rows to count.
    [InlineData(false, 0, new[] { 1, 4, 8400 })]
    // Not the layout the parser walks at all.
    [InlineData(false, 0, new[] { 1, 11 })]
    [InlineData(false, 0, new[] { 1, 11, 8400, 1 })]
    public void TryDeriveClassCount_ReadsTheClassRowsOrRefusesToGuess(
        bool expected, int expectedClasses, int[] dimensions)
    {
        Assert.Equal(expected, VisualEventDetector.TryDeriveClassCount(dimensions, out var numClasses));
        Assert.Equal(expectedClasses, numClasses);
    }

    [Fact]
    public void TryDeriveClassCount_WithoutDimensions_Refuses()
    {
        Assert.False(VisualEventDetector.TryDeriveClassCount(null, out var numClasses));
        Assert.Equal(0, numClasses);
    }

    [Fact]
    public void ParseClassNames_ReadsTheUltralyticsDictLiteral()
    {
        var names = VisualEventDetector.ParseClassNames(UltralyticsNames);

        Assert.NotNull(names);
        Assert.Equal(7, names!.Count);
        Assert.Equal("Elimination", names[0]);
        Assert.Equal("Death Spectating", names[1]);
        Assert.Equal("Steel Trap", names[6]);
    }

    // Another exporter may quote differently or omit the key entirely; a map that cannot be read
    // must disable the name check rather than invent entries for it to fail on.
    [Fact]
    public void ParseClassNames_AcceptsDoubleQuotes_AndYieldsNullWhenUnreadable()
    {
        var doubleQuoted = VisualEventDetector.ParseClassNames("{0: \"Elimination\", 1: \"Assist\"}");
        Assert.NotNull(doubleQuoted);
        Assert.Equal("Elimination", doubleQuoted![0]);
        Assert.Equal("Assist", doubleQuoted[1]);

        Assert.Null(VisualEventDetector.ParseClassNames(null));
        Assert.Null(VisualEventDetector.ParseClassNames("   "));
        Assert.Null(VisualEventDetector.ParseClassNames("detect"));
    }

    [Fact]
    public void FindClassMapMismatch_AcceptsDefinitionsThatMatchTheModel()
    {
        var names = VisualEventDetector.ParseClassNames(UltralyticsNames);

        Assert.Null(VisualEventDetector.FindClassMapMismatch(ShippedDefinitions(), 7, names));
    }

    // The exact edit the old code could not survive: one more entry than the model has classes.
    [Fact]
    public void FindClassMapMismatch_RejectsAClassIdBeyondTheModelsClasses()
    {
        var definitions = ShippedDefinitions();
        definitions.Add(Def(7, "Ultimate"));

        var mismatch = VisualEventDetector.FindClassMapMismatch(
            definitions, 7, VisualEventDetector.ParseClassNames(UltralyticsNames));

        Assert.NotNull(mismatch);
        Assert.Contains("7", mismatch);
        Assert.Contains("Ultimate", mismatch);
    }

    // The shape check cannot see this one — the count still matches, only the meaning moved.
    [Fact]
    public void FindClassMapMismatch_RejectsAReorderedOrRenamedClass()
    {
        var definitions = ShippedDefinitions();
        definitions[2].Name = "Healing";

        var mismatch = VisualEventDetector.FindClassMapMismatch(
            definitions, 7, VisualEventDetector.ParseClassNames(UltralyticsNames));

        Assert.NotNull(mismatch);
        Assert.Contains("Healing", mismatch);
        Assert.Contains("Turret", mismatch);
    }

    // Without a class map the names cannot be checked, but the count still can.
    [Fact]
    public void FindClassMapMismatch_WithoutAModelClassMap_StillBoundsChecksClassIds()
    {
        var definitions = ShippedDefinitions();
        Assert.Null(VisualEventDetector.FindClassMapMismatch(definitions, 7, null));

        definitions.Add(Def(9, "Ultimate"));
        Assert.NotNull(VisualEventDetector.FindClassMapMismatch(definitions, 7, null));
    }

    // Fail loudly rather than skip, matching InputTensorReuseTests: this is the assertion that ties
    // the pure helpers above to the model and events.json that actually ship, and a guard that
    // quietly disables itself where the model is absent is worse than no guard at all.
    //
    // Loads its own session rather than ModelService's cached one: test classes run in parallel and
    // ModelService.UnloadModel disposes the shared session out from under whoever else holds it.
    [Fact]
    public void ShippedModel_DeclaresSevenClasses_AndAgreesWithEventsJson()
    {
        var modelPath = ModelService.GetModelPath(GameId);
        Assert.True(File.Exists(modelPath),
            $"ONNX model not found at {modelPath}. This test verifies events.json against the " +
            "real model and cannot be checked without it. It must fail, not skip.");

        using var session = new InferenceSession(modelPath);

        var outputName = session.OutputMetadata.Keys.First();
        var dimensions = session.OutputMetadata[outputName].Dimensions;

        Assert.True(VisualEventDetector.TryDeriveClassCount(dimensions, out var numClasses),
            $"Output {outputName} has shape [{string.Join(',', dimensions)}], which carries no " +
            "static class dimension — the detector would be falling back to events.json.");
        Assert.Equal(7, numClasses);

        var names = VisualEventDetector.ParseClassNames(
            session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var raw) ? raw : null);
        Assert.NotNull(names);
        Assert.Equal(numClasses, names!.Count);

        var definitions = ModelService.LoadEventDefinitions(GameId);
        Assert.Equal(numClasses, definitions.Count);

        var mismatch = VisualEventDetector.FindClassMapMismatch(definitions, numClasses, names);
        Assert.True(mismatch == null,
            $"data/training/{GameId}/events.json disagrees with model.onnx: {mismatch}");
    }
}
