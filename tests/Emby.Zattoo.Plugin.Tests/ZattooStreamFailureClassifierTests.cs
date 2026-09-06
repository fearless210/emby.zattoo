using Emby.Zattoo.Plugin.LiveTv;

namespace Emby.Zattoo.Plugin.Tests;

public sealed class ZattooStreamFailureClassifierTests
{
    [Fact]
    public void Classify_TreatsACleanExitAsAProviderThatStoppedSending()
    {
        var failure = ZattooStreamFailureClassifier.Classify(0, Array.Empty<string>());

        Assert.Equal(ZattooStreamFailureReason.NoDataFromProvider, failure.Reason);
        Assert.Equal(ZattooStreamFailureKind.Recoverable, failure.Kind);
    }

    [Theory]
    [InlineData("[https @ 0x1] HTTP error 403 Forbidden")]
    [InlineData("[hls @ 0x1] Failed to open segment 12 of playlist 0")]
    [InlineData("[hls @ 0x1] Failed to reload playlist 0")]
    [InlineData("Server returned 404 Not Found")]
    [InlineData("Server returned 503 Service Unavailable")]
    public void Classify_RecognisesARefusedMediaRequest(string line)
    {
        var failure = ZattooStreamFailureClassifier.Classify(1, new[] { line });

        Assert.Equal(ZattooStreamFailureReason.MediaRequestFailed, failure.Reason);
        Assert.Equal(ZattooStreamFailureKind.Recoverable, failure.Kind);
    }

    [Theory]
    [InlineData("[tls @ 0x1] Connection reset by peer")]
    [InlineData("[tcp @ 0x1] Connection timed out")]
    [InlineData("[https @ 0x1] Error in the pull function")]
    [InlineData("Input/output error")]
    [InlineData("[tcp @ 0x1] Temporary failure in name resolution")]
    public void Classify_RecognisesALostConnection(string line)
    {
        var failure = ZattooStreamFailureClassifier.Classify(1, new[] { line });

        Assert.Equal(ZattooStreamFailureReason.ConnectionLost, failure.Reason);
        Assert.Equal(ZattooStreamFailureKind.Recoverable, failure.Kind);
    }

    [Theory]
    [InlineData("Unrecognized option 'reconnect_streamed'.")]
    [InlineData("Error splitting the argument list: Option not found")]
    [InlineData("Protocol not found")]
    [InlineData("/usr/lib/emby/ffmpeg: No such file or directory")]
    public void Classify_RefusesToReopenALocalFfmpegFailure(string line)
    {
        var failure = ZattooStreamFailureClassifier.Classify(1, new[] { line });

        Assert.Equal(ZattooStreamFailureReason.LocalFfmpegFailure, failure.Reason);
        Assert.Equal(ZattooStreamFailureKind.Permanent, failure.Kind);
    }

    [Fact]
    public void Classify_PrefersALocalFailureOverEveryOtherLine()
    {
        var failure = ZattooStreamFailureClassifier.Classify(
            1,
            new[]
            {
                "[https @ 0x1] HTTP error 403 Forbidden",
                "[tcp @ 0x1] Connection reset by peer",
                "Unrecognized option 'nope'.",
            });

        Assert.Equal(ZattooStreamFailureKind.Permanent, failure.Kind);
    }

    [Fact]
    public void Classify_PrefersARefusedRequestOverALostConnection()
    {
        // A refused request is the cause; the reset that follows is its effect.
        var failure = ZattooStreamFailureClassifier.Classify(
            1,
            new[]
            {
                "[tcp @ 0x1] Connection reset by peer",
                "[https @ 0x1] HTTP error 403 Forbidden",
            });

        Assert.Equal(ZattooStreamFailureReason.MediaRequestFailed, failure.Reason);
    }

    [Fact]
    public void Classify_KeepsAnUnexplainedFailureRecoverable()
    {
        var failure = ZattooStreamFailureClassifier.Classify(
            1,
            new[] { "frame= 1234 fps= 25 q=-1.0 size=  102400kB" });

        Assert.Equal(ZattooStreamFailureReason.Unknown, failure.Reason);
        Assert.Equal(ZattooStreamFailureKind.Recoverable, failure.Kind);
    }

    [Fact]
    public void Classify_IgnoresBlankLines()
    {
        var failure = ZattooStreamFailureClassifier.Classify(
            0,
            new[] { string.Empty, "   ", "\t" });

        Assert.Equal(ZattooStreamFailureReason.NoDataFromProvider, failure.Reason);
    }

    [Fact]
    public void Classify_ReportsTheExitCodeAndADescriptionForEveryReason()
    {
        var failure = ZattooStreamFailureClassifier.Classify(
            137,
            new[] { "[https @ 0x1] HTTP error 403 Forbidden" });

        Assert.Equal(137, failure.ExitCode);
        Assert.Equal("a playlist or segment request was refused", failure.Description);
    }

    [Fact]
    public void Classify_RejectsAMissingStandardError()
    {
        Assert.Throws<ArgumentNullException>(
            () => ZattooStreamFailureClassifier.Classify(1, null!));
    }
}
