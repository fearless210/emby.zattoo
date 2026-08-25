using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Core.Tests.TestInfrastructure;
using Emby.Zattoo.Zattoo;

namespace Emby.Zattoo.Core.Tests;

public sealed class HlsPlaylistSelectorTests
{
    private static readonly Uri MasterUri = new(
        "https://stream.invalid/live/master.m3u8?signature=fake");

    [Fact]
    public void Select_ChoosesHighestAllowedVideoAndDefaultAudio()
    {
        var selection = HlsPlaylistSelector.Select(
            Fixture.Read("hls-master.m3u8"),
            MasterUri,
            maximumHeight: 720);

        Assert.True(selection.IsMasterPlaylist);
        Assert.Equal(
            "https://stream.invalid/live/video/720.m3u8?signature=fake",
            selection.VideoUri.AbsoluteUri);
        Assert.Equal(
            "https://stream.invalid/live/audio/fr.m3u8?signature=fake",
            selection.AudioUri?.AbsoluteUri);
    }

    [Fact]
    public void Select_RespectsMaximumHeight()
    {
        var selection = HlsPlaylistSelector.Select(
            Fixture.Read("hls-master.m3u8"),
            MasterUri,
            maximumHeight: 540);

        Assert.Equal(
            "https://stream.invalid/live/video/432.m3u8?signature=fake",
            selection.VideoUri.AbsoluteUri);
    }

    [Fact]
    public void Select_UsesMediaPlaylistDirectly()
    {
        const string mediaPlaylist = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\nsegment.m4s";

        var selection = HlsPlaylistSelector.Select(mediaPlaylist, MasterUri, 720);

        Assert.False(selection.IsMasterPlaylist);
        Assert.Equal(MasterUri, selection.VideoUri);
        Assert.Null(selection.AudioUri);
    }

    [Fact]
    public void Select_RejectsInsecureChildPlaylist()
    {
        const string master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000,RESOLUTION=1280x720\nhttp://stream.invalid/video.m3u8";

        Assert.Throws<ZattooProtocolException>(
            () => HlsPlaylistSelector.Select(master, MasterUri, 720));
    }
}
