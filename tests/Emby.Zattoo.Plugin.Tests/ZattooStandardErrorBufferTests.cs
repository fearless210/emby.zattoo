using Emby.Zattoo.Plugin.LiveTv;

namespace Emby.Zattoo.Plugin.Tests;

public sealed class ZattooStandardErrorBufferTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 6, 19, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    [Fact]
    public void ReadRecent_ReturnsTheLinesOldestFirst()
    {
        var buffer = new ZattooStandardErrorBuffer();
        buffer.Add("first", Start);
        buffer.Add("second", Start.AddSeconds(1));

        Assert.Equal(new[] { "first", "second" }, buffer.ReadRecent(Start.AddSeconds(2), Window));
    }

    [Fact]
    public void ReadRecent_HidesAWarningFfmpegAlreadyRecoveredFrom()
    {
        // The case seen in production: a transient HTTP warning survived by the
        // reconnect options, minutes before an unrelated end of stream.
        var buffer = new ZattooStandardErrorBuffer();
        buffer.Add("[https @ 0x1] HTTP error 403 Forbidden", Start);
        buffer.Add("frame= 4200 fps= 25", Start.AddMinutes(3));

        var recent = buffer.ReadRecent(Start.AddMinutes(3).AddSeconds(1), Window);

        Assert.Equal(new[] { "frame= 4200 fps= 25" }, recent);
    }

    [Fact]
    public void ReadRecent_KeepsALineExactlyOnTheWindowEdge()
    {
        var buffer = new ZattooStandardErrorBuffer();
        buffer.Add("edge", Start);

        Assert.Single(buffer.ReadRecent(Start.Add(Window), Window));
        Assert.Empty(buffer.ReadRecent(Start.Add(Window).AddTicks(1), Window));
    }

    [Fact]
    public void ReadRecent_ReturnsNothingWhenEveryLineIsTooOld()
    {
        // An empty answer is the honest one: FFmpeg said nothing recent, so the
        // classifier must not name a cause.
        var buffer = new ZattooStandardErrorBuffer();
        buffer.Add("[tcp @ 0x1] Connection reset by peer", Start);

        Assert.Empty(buffer.ReadRecent(Start.AddMinutes(10), Window));
    }

    [Fact]
    public void Add_KeepsOnlyTheMostRecentLinesWithinTheCapacity()
    {
        var buffer = new ZattooStandardErrorBuffer(2);
        buffer.Add("first", Start);
        buffer.Add("second", Start.AddSeconds(1));
        buffer.Add("third", Start.AddSeconds(2));

        Assert.Equal(
            new[] { "second", "third" },
            buffer.ReadRecent(Start.AddSeconds(3), Window));
    }

    [Fact]
    public void Add_IgnoresBlankLines()
    {
        var buffer = new ZattooStandardErrorBuffer();
        buffer.Add(string.Empty, Start);
        buffer.Add("   ", Start);

        Assert.Empty(buffer.ReadRecent(Start, Window));
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var buffer = new ZattooStandardErrorBuffer();
        buffer.Add("line", Start);
        buffer.Clear();

        Assert.Empty(buffer.ReadRecent(Start, Window));
    }

    [Fact]
    public void Constructor_RejectsAnEmptyBuffer()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZattooStandardErrorBuffer(0));
    }

    [Fact]
    public void ReadRecent_RejectsAnEmptyWindow()
    {
        var buffer = new ZattooStandardErrorBuffer();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => buffer.ReadRecent(Start, TimeSpan.Zero));
    }
}
