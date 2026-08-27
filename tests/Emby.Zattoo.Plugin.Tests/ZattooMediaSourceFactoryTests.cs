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
