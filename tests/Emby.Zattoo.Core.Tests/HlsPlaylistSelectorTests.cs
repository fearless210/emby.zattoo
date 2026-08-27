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

    [Fact]
    public void Select_ReportsTheCharacteristicsOfTheSelectedRendition()
    {
        var selection = HlsPlaylistSelector.Select(
            Fixture.Read("hls-master.m3u8"),
            new Uri("https://cdn.example.invalid/live/master.m3u8"));

        Assert.Equal(1280, selection.Width);
        Assert.Equal(720, selection.Height);
        Assert.Equal(5400000, selection.Bandwidth);
        Assert.Equal("h264", selection.VideoCodec);
        Assert.Equal("aac", selection.AudioCodec);
    }

    [Fact]
    public void Select_ReportsTheCharacteristicsOfACappedRendition()
    {
        var selection = HlsPlaylistSelector.Select(
            Fixture.Read("hls-master.m3u8"),
            new Uri("https://cdn.example.invalid/live/master.m3u8"),
            maximumHeight: 432);

        Assert.Equal(768, selection.Width);
        Assert.Equal(432, selection.Height);
        Assert.Equal(3200000, selection.Bandwidth);
    }

    [Theory]
    [InlineData("avc1.64001f,mp4a.40.2", "h264")]
    [InlineData("hvc1.2.4.L120.B0", "hevc")]
    [InlineData("mp4a.40.2", null)]
    [InlineData("", null)]
    public void ReadVideoCodec_MapsKnownIdentifiersOnly(string codecs, string? expected)
    {
        Assert.Equal(expected, HlsPlaylistSelector.ReadVideoCodec(codecs));
    }

    [Theory]
    [InlineData("avc1.64001f,mp4a.40.2", "aac")]
    [InlineData("avc1.64001f,ac-3", "ac3")]
    [InlineData("avc1.64001f,ec-3", "eac3")]
    [InlineData("avc1.64001f", null)]
    public void ReadAudioCodec_MapsKnownIdentifiersOnly(string codecs, string? expected)
    {
        Assert.Equal(expected, HlsPlaylistSelector.ReadAudioCodec(codecs));
    }
}
