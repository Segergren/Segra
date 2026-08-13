using System.IO;
using Segra.Backend.Detection;
using Segra.Backend.Games;
using Xunit;

namespace Segra.Tests;

// ModelService used to build model paths with a bare Path.Combine(BasePath, gameId) and probe them
// with File.Exists, trusting whatever casing the caller passed. The shipped folder is
// data/training/Overwatch while GameIntegrationService.SanitizeGameId lowercases display names, so
// on a case-sensitive filesystem — ext4, the Flatpak runtime — the lookup missed and the entire ML
// feature no-opped with nothing logged. It only ever worked by accident of Windows' case-insensitive
// filesystem. These tests pin that a model id resolves to the directory on disk no matter how it is
// spelled, and that an id with no directory still falls back to a literal creatable path.
public class ModelPathCasingTests
{
    [Fact]
    public void LowercasedGameId_FindsShippedModel()
    {
        Assert.True(ModelService.HasModelForGame("overwatch"),
            $"Lowercased id 'overwatch' found no model under {ModelService.BasePath}.");
    }

    [Fact]
    public void MixedCaseGameId_ResolvesToOnDiskCasing()
    {
        Assert.Equal("Overwatch", Path.GetFileName(ModelService.GetGamePath("OVERWATCH")));
    }

    // Fail loudly rather than skip, matching InputTensorReuseTests: a guard that quietly disables
    // itself where the model is absent is worse than no guard at all.
    [Fact]
    public void ResolvedModelPath_ExistsRegardlessOfRequestedCasing()
    {
        var modelPath = ModelService.GetModelPath("oVeRwAtCh");
        Assert.True(File.Exists(modelPath),
            $"Case-insensitive resolution produced {modelPath}, which does not exist.");
    }

    // The fallback is load-bearing: SaveEventDefinitions creates the directory for a game that has
    // none yet, so an unmatched id must still yield the literal path rather than null or a throw.
    [Fact]
    public void UnknownGameId_FallsBackToLiteralPath()
    {
        Assert.Equal(Path.Combine(ModelService.BasePath, "not-a-real-game"),
            ModelService.GetGamePath("not-a-real-game"));
        Assert.False(ModelService.HasModelForGame("not-a-real-game"));
    }

    // End-to-end across the id resolution and the path resolution: the name-only path that a
    // catalog lookup without an IGDB id takes has to reach the shipped model on Linux.
    [Fact]
    public void SanitizedDisplayName_ReachesTheShippedModel()
    {
        var modelId = GameIntegrationService.ResolveModelId(null, "Overwatch");
        Assert.NotNull(modelId);
        Assert.True(ModelService.HasModelForGame(modelId!),
            $"Resolved id '{modelId}' found no model under {ModelService.BasePath}.");
    }
}
