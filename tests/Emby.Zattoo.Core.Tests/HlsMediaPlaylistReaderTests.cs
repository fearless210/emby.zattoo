using Emby.Zattoo.Core.Tests.TestInfrastructure;
using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Zattoo;

namespace Emby.Zattoo.Core.Tests;

public sealed class HlsMediaPlaylistReaderTests
{
    private static readonly Uri MediaUri = new(
        "https://stream.invalid/live/video/720.m3u8?z32=fake");

    [Fact]
    public void Read_ResolvesSegmentsAgainstThePlaylist()
    {
        var playlist = HlsMediaPlaylistReader.Read(Fixture.Read("hls-media.m3u8"), MediaUri);

        Assert.Equal(3, playlist.SegmentUris.Count);
        Assert.Equal(
            "https://stream.invalid/live/video/segment-1234567.m4s?z32=fake",
            playlist.SegmentUris[0].AbsoluteUri);
        Assert.Equal(
            "https://stream.invalid/live/video/segment-1234569.m4s?z32=fake",
            playlist.LastSegmentUri?.AbsoluteUri);
        Assert.Equal(
            "https://stream.invalid/live/video/init.mp4?z32=fake",
            playlist.InitializationUri?.AbsoluteUri);
    }

    [Fact]
    public void Read_ReadsTheLiveWindowMarkers()
    {
        var playlist = HlsMediaPlaylistReader.Read(Fixture.Read("hls-media.m3u8"), MediaUri);

        Assert.Equal(1234567, playlist.MediaSequence);
        Assert.Equal(4d, playlist.TargetDurationSeconds);
        Assert.False(playlist.IsEndList);
    }

    [Fact]
    public void Read_DetectsACompletedPlaylist()
    {
        const string content =
            "#EXTM3U\n#EXTINF:4.000,\nsegment.m4s\n#EXT-X-ENDLIST\n";

        var playlist = HlsMediaPlaylistReader.Read(content, MediaUri);

        Assert.True(playlist.IsEndList);
        Assert.Single(playlist.SegmentUris);
    }

    [Fact]
    public void Read_ReturnsNoSegmentsForAnEmptyWindow()
    {
        const string content = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:12\n";

        var playlist = HlsMediaPlaylistReader.Read(content, MediaUri);

        Assert.Empty(playlist.SegmentUris);
        Assert.Null(playlist.LastSegmentUri);
        Assert.Null(playlist.InitializationUri);
    }

    [Fact]
    public void Read_KeepsUnknownTagsOutOfTheSegmentList()
    {
        const string content =
            "#EXTM3U\n#EXT-X-PROGRAM-DATE-TIME:2026-09-05T10:00:00Z\n"
            + "#EXT-X-SOMETHING-NEW:1\n\n#EXTINF:4.000,\nsegment.m4s\n";

        var playlist = HlsMediaPlaylistReader.Read(content, MediaUri);

        Assert.Single(playlist.SegmentUris);
    }

    [Fact]
    public void Read_RejectsAMasterPlaylist()
    {
        Assert.Throws<ZattooProtocolException>(
            () => HlsMediaPlaylistReader.Read(Fixture.Read("hls-master.m3u8"), MediaUri));
    }

    [Fact]
    public void Read_RejectsContentThatIsNotAPlaylist()
    {
        Assert.Throws<ZattooProtocolException>(
            () => HlsMediaPlaylistReader.Read("<html>error</html>", MediaUri));
    }

    [Fact]
    public void Read_RejectsAnInsecureSegment()
    {
        const string content = "#EXTM3U\n#EXTINF:4.000,\nhttp://stream.invalid/segment.m4s\n";

        Assert.Throws<ZattooProtocolException>(
            () => HlsMediaPlaylistReader.Read(content, MediaUri));
    }

    [Fact]
    public void Read_RejectsAnInsecurePlaylistUri()
    {
        Assert.Throws<ZattooProtocolException>(
            () => HlsMediaPlaylistReader.Read(
                "#EXTM3U\n",
                new Uri("http://stream.invalid/live/video/720.m3u8")));
    }
}
