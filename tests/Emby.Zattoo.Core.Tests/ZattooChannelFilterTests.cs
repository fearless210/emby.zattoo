using Emby.Zattoo.Models;
using Emby.Zattoo.Zattoo;

namespace Emby.Zattoo.Core.Tests;

public sealed class ZattooChannelFilterTests
{
    private static readonly ZattooChannel Playable = Channel(
        "playable",
        Quality(available: true, drm: false));
    private static readonly ZattooChannel Mixed = Channel(
        "mixed",
        Quality(available: true, drm: true),
        Quality(available: true, drm: false));
    private static readonly ZattooChannel DrmOnly = Channel(
        "drm",
        Quality(available: true, drm: true));
    private static readonly ZattooChannel Unavailable = Channel(
        "unavailable",
        Quality(available: false, drm: false));

    [Fact]
    public void Apply_PlayableOnlyKeepsOpenAndMixedChannels()
    {
        var result = ZattooChannelFilter.Apply(
            AllChannels(),
            ZattooChannelImportMode.PlayableOnly);

        Assert.Equal(new[] { "playable", "mixed" }, result.Select(item => item.Id));
    }

    [Fact]
    public void Apply_ExcludeDrmOnlyKeepsTemporarilyUnavailableChannels()
    {
        var result = ZattooChannelFilter.Apply(
            AllChannels(),
            ZattooChannelImportMode.ExcludeDrmOnly);

        Assert.Equal(
            new[] { "playable", "mixed", "unavailable" },
            result.Select(item => item.Id));
    }

    [Fact]
    public void Apply_AllChannelsPreservesTheCatalogue()
    {
        var result = ZattooChannelFilter.Apply(
            AllChannels(),
            ZattooChannelImportMode.AllChannels);

        Assert.Equal(4, result.Count);
    }

    private static IReadOnlyList<ZattooChannel> AllChannels()
    {
        return new[] { Playable, Mixed, DrmOnly, Unavailable };
    }

    private static ZattooChannel Channel(
        string id,
        params ZattooQuality[] qualities)
    {
        return new ZattooChannel { Id = id, Qualities = qualities };
    }

    private static ZattooQuality Quality(bool available, bool drm)
    {
        return new ZattooQuality
        {
            IsAvailable = available,
            DrmRequired = drm,
        };
    }
}
