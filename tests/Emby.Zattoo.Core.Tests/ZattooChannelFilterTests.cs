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

    [Fact]
    public void ApplyGroups_KeepsEverythingWithoutASelection()
    {
        var channels = CreateGroupedChannels();

        var result = ZattooChannelFilter.ApplyGroups(channels, Array.Empty<string>());

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ApplyGroups_KeepsOnlyTheSelectedGroups()
    {
        var channels = CreateGroupedChannels();

        var result = ZattooChannelFilter.ApplyGroups(channels, new[] { "Suisse" });

        Assert.Equal(new[] { "ch-a", "ch-c" }, result.Select(channel => channel.Id));
    }

    [Fact]
    public void ApplyGroups_IgnoresCaseAndSurroundingSpace()
    {
        var channels = CreateGroupedChannels();

        var result = ZattooChannelFilter.ApplyGroups(channels, new[] { "  suisse " });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ApplyGroups_MatchesNothingForAnUnknownGroup()
    {
        var channels = CreateGroupedChannels();

        // A typo must not silently import the whole catalogue.
        Assert.Empty(ZattooChannelFilter.ApplyGroups(channels, new[] { "Nowhere" }));
    }

    [Fact]
    public void ListGroups_ReportsEachGroupOnceInCatalogueOrder()
    {
        var groups = ZattooChannelFilter.ListGroups(CreateGroupedChannels());

        Assert.Equal(new[] { "Suisse", "France" }, groups);
    }

    [Fact]
    public void ApplyFavorites_KeepsOnlyTheAccountFavorites()
    {
        var result = ZattooChannelFilter.ApplyFavorites(CreateMixedFavorites());

        Assert.Equal(new[] { "ch-a", "ch-c" }, result.Select(item => item.Id));
    }

    [Fact]
    public void ApplyFavorites_ReturnsNothingWhenTheAccountHasNoFavorite()
    {
        var channels = new[]
        {
            new ZattooChannel { Id = "ch-a" },
            new ZattooChannel { Id = "ch-b" },
        };

        // The caller decides what an empty selection means; the filter only
        // reports it.
        Assert.Empty(ZattooChannelFilter.ApplyFavorites(channels));
    }

    [Fact]
    public void ApplyFavorites_PreservesTheCatalogueOrder()
    {
        var result = ZattooChannelFilter.ApplyFavorites(CreateMixedFavorites());

        Assert.Equal("ch-a", result[0].Id);
        Assert.Equal("ch-c", result[1].Id);
    }

    [Fact]
    public void ApplyFavorites_RejectsAMissingCatalogue()
    {
        Assert.Throws<ArgumentNullException>(
            () => ZattooChannelFilter.ApplyFavorites(null!));
    }

    private static IReadOnlyList<ZattooChannel> CreateMixedFavorites()
    {
        return new[]
        {
            new ZattooChannel { Id = "ch-a", IsFavorite = true },
            new ZattooChannel { Id = "ch-b", IsFavorite = false },
            new ZattooChannel { Id = "ch-c", IsFavorite = true },
        };
    }

    private static IReadOnlyList<ZattooChannel> CreateGroupedChannels()
    {
        return new[]
        {
            new ZattooChannel { Id = "ch-a", GroupName = "Suisse" },
            new ZattooChannel { Id = "ch-b", GroupName = "France" },
            new ZattooChannel { Id = "ch-c", GroupName = "Suisse" },
        };
    }
}
