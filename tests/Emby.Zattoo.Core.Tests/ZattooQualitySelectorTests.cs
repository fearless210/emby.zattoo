using Emby.Zattoo.Models;
using Emby.Zattoo.Zattoo;

namespace Emby.Zattoo.Core.Tests;

public sealed class ZattooQualitySelectorTests
{
    [Fact]
    public void SelectBest_AutoFiltersDrmAndChoosesHighestKnownQuality()
    {
        var qualities = CreateQualities();

        var selected = ZattooQualitySelector.SelectBest(
            qualities,
            ZattooPreferredQuality.Auto);

        Assert.NotNull(selected);
        Assert.Equal("hd", selected.Level);
        Assert.Equal(720, selected.Height);
    }

    [Fact]
    public void SelectBest_RespectsMaximumPreferredHeight()
    {
        var selected = ZattooQualitySelector.SelectBest(
            CreateQualities(),
            ZattooPreferredQuality.P540);

        Assert.NotNull(selected);
        Assert.Equal("sd", selected.Level);
    }

    [Fact]
    public void SelectBest_FallsBackToLowestQualityWhenEveryLevelExceedsPreference()
    {
        var qualities = new[]
        {
            new ZattooQuality
            {
                Level = "fhd",
                Height = 1080,
                IsAvailable = true,
            },
            new ZattooQuality
            {
                Level = "hd",
                Height = 720,
                IsAvailable = true,
            },
        };

        var selected = ZattooQualitySelector.SelectBest(
            qualities,
            ZattooPreferredQuality.P540);

        Assert.NotNull(selected);
        Assert.Equal("hd", selected.Level);
    }

    [Fact]
    public void SelectBest_PrefersUnknownLevelOverQualityAbovePreference()
    {
        var qualities = new[]
        {
            new ZattooQuality
            {
                Level = "fhd",
                Height = 1080,
                IsAvailable = true,
            },
            new ZattooQuality
            {
                Level = "provider-specific",
                IsAvailable = true,
            },
        };

        var selected = ZattooQualitySelector.SelectBest(
            qualities,
            ZattooPreferredQuality.P540);

        Assert.NotNull(selected);
        Assert.Equal("provider-specific", selected.Level);
    }

    [Fact]
    public void SelectBest_ReturnsNullWhenEveryAvailableQualityRequiresDrm()
    {
        var qualities = new[]
        {
            new ZattooQuality
            {
                Level = "fhd",
                Height = 1080,
                IsAvailable = true,
                DrmRequired = true,
            },
        };

        Assert.Null(
            ZattooQualitySelector.SelectBest(qualities, ZattooPreferredQuality.Auto));
    }

    private static IReadOnlyList<ZattooQuality> CreateQualities()
    {
        return new[]
        {
            new ZattooQuality
            {
                Level = "fhd",
                Height = 1080,
                IsAvailable = true,
                DrmRequired = true,
            },
            new ZattooQuality
            {
                Level = "hd",
                Height = 720,
                IsAvailable = true,
            },
            new ZattooQuality
            {
                Level = "sd",
                Height = 540,
                IsAvailable = true,
            },
        };
    }
}
