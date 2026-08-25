using Emby.Zattoo.Models;

namespace Emby.Zattoo.Core.Tests;

public sealed class ZattooStreamStatisticsTests
{
    [Fact]
    public void Calculate_CountsChannelsByCatalogueAvailabilityAndDrm()
    {
        var channels = new[]
        {
            Channel("open", Quality(available: true, drm: false)),
            Channel("mixed", Quality(available: true, drm: true), Quality(available: true, drm: false)),
            Channel("drm", Quality(available: true, drm: true)),
            Channel("offline", Quality(available: false, drm: false)),
        };

        var statistics = ZattooStreamStatistics.Calculate(channels);

        Assert.Equal(4, statistics.TotalChannels);
        Assert.Equal(3, statistics.ChannelsWithAvailableStreams);
        Assert.Equal(2, statistics.ChannelsWithNonDrmStreams);
        Assert.Equal(1, statistics.DrmOnlyChannels);
        Assert.Equal(1, statistics.ChannelsWithoutAvailableStreams);
    }

    private static ZattooChannel Channel(string id, params ZattooQuality[] qualities)
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
