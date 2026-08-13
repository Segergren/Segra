using Segra.Backend.Core.Models;
using Segra.Backend.Detection;
using Segra.Backend.Games;
using Xunit;

namespace Segra.Tests;

// ML detection used to key its model lookup off SanitizeGameId(gameName) alone, so a null,
// localized or renamed display name ("Overwatch 2" -> "overwatch2") resolved to an id with no
// model behind it and detection silently never started — even when the IGDB id had matched.
// The enable/disable toggle was computed from a second, hardcoded name test, so the two could
// disagree. These tests pin the id-first resolution and the toggle sharing that resolution.
public class ModelIdResolutionTests
{
    private const int OverwatchIgdbId = 125174;
    private const int Dota2IgdbId = 2963;

    [Fact]
    public void RecognizedIgdbId_BeatsDriftedDisplayName()
    {
        Assert.Equal("Overwatch", GameIntegrationService.ResolveModelId(OverwatchIgdbId, "Overwatch 2"));
    }

    [Fact]
    public void RecognizedIgdbId_ResolvesWithoutAnyDisplayName()
    {
        Assert.Equal("Overwatch", GameIntegrationService.ResolveModelId(OverwatchIgdbId, null));
    }

    // The resolved id is used verbatim as a path segment under data/training, so it has to match
    // the shipped directory byte-for-byte. On a case-sensitive filesystem "overwatch" finds
    // nothing; this is the test that pins the casing.
    [Fact]
    public void ResolvedIdLocatesTheShippedModel()
    {
        var modelId = GameIntegrationService.ResolveModelId(OverwatchIgdbId, null);
        Assert.NotNull(modelId);
        Assert.True(ModelService.HasModelForGame(modelId!),
            $"No model found at {ModelService.GetModelPath(modelId!)} for resolved id '{modelId}'.");
    }

    [Fact]
    public void UnmappedIgdbId_FallsBackToSanitizedName()
    {
        Assert.Equal("dota2", GameIntegrationService.ResolveModelId(Dota2IgdbId, "Dota 2"));
    }

    [Fact]
    public void NoIdAndNoUsableName_ResolvesToNull()
    {
        Assert.Null(GameIntegrationService.ResolveModelId(null, null));
        Assert.Null(GameIntegrationService.ResolveModelId(null, "!!!"));
    }

    // Without canonicalisation a name-matched session and an id-matched one would key
    // ModelService's caches differently and load the ONNX session twice.
    [Fact]
    public void NameOnlyMatch_CanonicalisesToMappedSpelling()
    {
        Assert.Equal("Overwatch", GameIntegrationService.ResolveModelId(null, "overwatch"));
    }

    [Fact]
    public void DisabledToggle_AppliesToIdMatchedGameWithDriftedName()
    {
        var integrations = new GameIntegrations
        {
            Overwatch = new GameIntegrationSettings(false)
        };

        var modelId = GameIntegrationService.ResolveModelId(OverwatchIgdbId, "Overwatch 2");
        Assert.NotNull(modelId);
        Assert.False(GameIntegrationService.IsMlDetectionEnabled(modelId!, integrations));
    }

    [Fact]
    public void EnabledToggle_AllowsIdMatchedGame()
    {
        var integrations = new GameIntegrations();

        var modelId = GameIntegrationService.ResolveModelId(OverwatchIgdbId, "Overwatch 2");
        Assert.NotNull(modelId);
        Assert.True(GameIntegrationService.IsMlDetectionEnabled(modelId!, integrations));
    }

    [Fact]
    public void ModelsWithoutAToggle_AreAlwaysEnabled()
    {
        Assert.True(GameIntegrationService.IsMlDetectionEnabled("somegame", new GameIntegrations()));
    }
}
