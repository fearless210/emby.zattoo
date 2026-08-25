using Emby.Zattoo.Plugin.LiveTv;
using MediaBrowser.Model.MediaInfo;

namespace Emby.Zattoo.Plugin.Tests;

public sealed class ZattooMediaSourceFactoryTests
{
    [Fact]
    public void Create_DescribesServerSideMpegTsLiveStreamWithoutRemoteUrl()
    {
        var source = ZattooMediaSourceFactory.Create("tsr1", "RTS 1 HD");

        Assert.Equal("zattoo:tsr1:mpegts", source.Id);
        Assert.Equal("zattoo://tsr1", source.Path);
        Assert.Equal(MediaProtocol.File, source.Protocol);
        Assert.Equal("mpegts", source.Container);
        Assert.Contains("mpegts", source.Formats);
        Assert.False(source.IsRemote);
        Assert.True(source.IsInfiniteStream);
        Assert.True(source.RequiresOpening);
        Assert.True(source.RequiresClosing);
        Assert.False(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.DoesNotContain("http", source.Path, StringComparison.OrdinalIgnoreCase);
    }
}
