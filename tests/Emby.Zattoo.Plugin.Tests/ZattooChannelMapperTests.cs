using Emby.Zattoo.Models;
using Emby.Zattoo.Plugin.LiveTv;
using MediaBrowser.Model.LiveTv;

namespace Emby.Zattoo.Plugin.Tests;

public sealed class ZattooChannelMapperTests
{
    [Fact]
    public void Map_UsesStableCidAndMapsDisplayMetadata()
    {
        var channel = new ZattooChannel
        {
            Id = "tsr1",
            Name = "RTS 1 HD",
            Number = 1,
            LogoUrl = "https://logos.invalid/tsr1.png",
            IsFavorite = true,
            Qualities = new[]
            {
                new ZattooQuality
                {
                    Height = 720,
                    IsAvailable = true,
                    DrmRequired = false,
                },
            },
        };

        var result = ZattooChannelMapper.Map(channel, "tuner-host_");

        Assert.Equal("tuner-host_tsr1", result.Id);
        Assert.Equal("tsr1", result.TunerChannelId);
        Assert.Equal("RTS 1 HD", result.Name);
        Assert.Equal("1", result.Number);
        Assert.Equal(1, result.SortIndexNumber);
        Assert.Equal(channel.LogoUrl, result.ImageUrl);
        Assert.True(result.IsFavorite);
        Assert.True(result.IsHD);
        Assert.Equal(ChannelType.TV, result.ChannelType);
    }

    [Fact]
    public void Map_DoesNotAdvertiseHdFromUnavailableQuality()
    {
        var channel = new ZattooChannel
        {
            Id = "example",
            Name = "Example",
            Qualities = new[]
            {
                new ZattooQuality { Height = 1080, IsAvailable = false },
                new ZattooQuality { Height = 540, IsAvailable = true },
            },
        };

        var result = ZattooChannelMapper.Map(channel, "tuner-host_");

        Assert.False(result.IsHD);
    }

    [Fact]
    public void Map_DoesNotAdvertiseHdFromDrmQualityWhenFallbackIsSd()
    {
        var channel = new ZattooChannel
        {
            Id = "mixed",
            Name = "Mixed fixture",
            Qualities = new[]
            {
                new ZattooQuality
                {
                    Height = 1080,
                    IsAvailable = true,
                    DrmRequired = true,
                },
                new ZattooQuality
                {
                    Height = 540,
                    IsAvailable = true,
                    DrmRequired = false,
                },
            },
        };

        var result = ZattooChannelMapper.Map(channel, "tuner-host_");

        Assert.False(result.IsHD);
    }

    [Fact]
    public void Map_RejectsMissingEmbyChannelIdPrefix()
    {
        var channel = new ZattooChannel
        {
            Id = "tsr1",
            Name = "RTS 1 HD",
        };

        Assert.Throws<ArgumentException>(() =>
            ZattooChannelMapper.Map(channel, string.Empty));
    }

    [Fact]
    public void Map_MarksARadioChannelAsRadio()
    {
        var info = ZattooChannelMapper.Map(
            new ZattooChannel { Id = "ch-a", Name = "Radio A", IsRadio = true },
            "tuner_");

        Assert.Equal(ChannelType.Radio, info.ChannelType);
    }

    [Fact]
    public void Map_KeepsTelevisionAsTheDefault()
    {
        var info = ZattooChannelMapper.Map(
            new ZattooChannel { Id = "ch-a", Name = "Channel A" },
            "tuner_");

        Assert.Equal(ChannelType.TV, info.ChannelType);
    }
}
