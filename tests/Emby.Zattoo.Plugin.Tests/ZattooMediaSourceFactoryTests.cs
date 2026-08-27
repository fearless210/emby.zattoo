using Emby.Zattoo.Models;
using Emby.Zattoo.Plugin.LiveTv;
using Emby.Zattoo.Zattoo;
using MediaBrowser.Model.Entities;
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

    [Fact]
    public void DescribeStreams_PublishesTheRenditionSoEmbyCanCapATranscode()
    {
        var source = ZattooMediaSourceFactory.Create("tsr1", "RTS 1 HD");
        var selection = HlsPlaylistSelector.Select(
            "#EXTM3U\n"
                + "#EXT-X-STREAM-INF:BANDWIDTH=5400000,RESOLUTION=1280x720,"
                + "CODECS=\"avc1.64001f,mp4a.40.2\"\n"
                + "video/720.m3u8\n",
            new Uri("https://cdn.example.invalid/live/master.m3u8"));

        ZattooMediaSourceFactory.DescribeStreams(
            source,
            selection,
            new ZattooStream { Height = 720 });

        Assert.Equal(5400000, source.Bitrate);
        var video = Assert.Single(
            source.MediaStreams,
            stream => stream.Type == MediaStreamType.Video);
        Assert.Equal("h264", video.Codec);
        Assert.Equal(1280, video.Width);
        Assert.Equal(720, video.Height);
        Assert.Equal(5400000, video.BitRate);
        Assert.False(video.IsInterlaced);
        var audio = Assert.Single(
            source.MediaStreams,
            stream => stream.Type == MediaStreamType.Audio);
        Assert.Equal("aac", audio.Codec);
    }

    [Fact]
    public void DescribeStreams_FallsBackToTheCatalogueQualityWithoutAMaster()
    {
        var source = ZattooMediaSourceFactory.Create("tsr1", "RTS 1 HD");
        var selection = new HlsPlaylistSelection(
            new Uri("https://cdn.example.invalid/live/media.m3u8"),
            audioUri: null,
            isMasterPlaylist: false);

        ZattooMediaSourceFactory.DescribeStreams(
            source,
            selection,
            new ZattooStream { Width = 1280, Height = 720, BitrateKbps = 4200 });

        Assert.Equal(4200000, source.Bitrate);
        var video = Assert.Single(
            source.MediaStreams,
            stream => stream.Type == MediaStreamType.Video);
        Assert.Equal(720, video.Height);
        Assert.Equal(4200000, video.BitRate);
        Assert.Equal("h264", video.Codec);
    }

    [Fact]
    public void UseLocalLiveStreamEndpoint_ExposesCopyToThroughEmby()
    {
        var source = ZattooMediaSourceFactory.Create("tsr1", "RTS 1 HD");

        ZattooMediaSourceFactory.UseLocalLiveStreamEndpoint(
            source,
            "http://127.0.0.1:8096/",
            "stream-id");

        Assert.Equal(
            "http://127.0.0.1:8096/LiveTv/LiveStreamFiles/stream-id/stream.ts",
            source.Path);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.False(source.IsRemote);
        Assert.False(source.RequiresLooping);
        Assert.False(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.DoesNotContain("zattoo://", source.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8096/")]
    [InlineData("https://127.0.0.1:8920/")]
    public void IsSupportedLocalApiUrl_AcceptsUrlsUseLocalLiveStreamEndpointAccepts(
        string localApiUrl)
    {
        var source = ZattooMediaSourceFactory.Create("tsr1", "RTS 1 HD");

        Assert.True(ZattooMediaSourceFactory.IsSupportedLocalApiUrl(localApiUrl));
        ZattooMediaSourceFactory.UseLocalLiveStreamEndpoint(
            source,
            localApiUrl,
            "stream-id");
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:8096")]
    [InlineData("file:///var/lib/emby")]
    public void IsSupportedLocalApiUrl_RejectsUrlsUseLocalLiveStreamEndpointRejects(
        string localApiUrl)
    {
        var source = ZattooMediaSourceFactory.Create("tsr1", "RTS 1 HD");

        Assert.False(ZattooMediaSourceFactory.IsSupportedLocalApiUrl(localApiUrl));
        Assert.Throws<ArgumentException>(() =>
            ZattooMediaSourceFactory.UseLocalLiveStreamEndpoint(
                source,
                localApiUrl,
                "stream-id"));
    }

    [Fact]
    public void UseLocalLiveStreamEndpoint_RejectsNonHttpApiUrl()
    {
        var source = ZattooMediaSourceFactory.Create("tsr1", "RTS 1 HD");

        Assert.Throws<ArgumentException>(() =>
            ZattooMediaSourceFactory.UseLocalLiveStreamEndpoint(
                source,
                "file:///var/lib/emby",
                "stream-id"));
    }
}
