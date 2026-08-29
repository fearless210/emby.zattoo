using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Emby.Zattoo.Infrastructure;

internal static class MediaToolRunner
{
    public static Task<MediaToolResult> RunFfprobeAsync(
        string executable,
        string streamUrl,
        CancellationToken cancellationToken)
    {
        return RunAsync(
            executable,
            new[]
            {
                "-v", "error",
                "-show_entries",
                "stream=index,codec_name,codec_type,width,height,avg_frame_rate,sample_rate,channels:format=format_name,duration,bit_rate",
                "-of", "json",
                "-i", streamUrl,
            },
            TimeSpan.FromSeconds(45),
            cancellationToken);
    }

    public static Task<MediaToolResult> RunFfmpegCopyTestAsync(
        string executable,
        IReadOnlyList<string> streamUrls,
        TimeSpan duration,
        bool discardAlternateStreams,
        CancellationToken cancellationToken)
    {
        if (streamUrls == null || streamUrls.Count == 0 || streamUrls.Count > 2)
        {
            throw new ArgumentException(
                "One video input and at most one separate audio input are required.",
                nameof(streamUrls));
        }

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-y",
            "-stats_period", "1",
            "-progress", "pipe:1",
            "-nostats",
        };
        if (discardAlternateStreams)
        {
            arguments.Add("-discard:v");
            arguments.Add("all");
            arguments.Add("-discard:v:0");
            arguments.Add("none");
            arguments.Add("-discard:a");
            arguments.Add("all");
            arguments.Add("-discard:a:0");
            arguments.Add("none");
            arguments.Add("-discard:s");
            arguments.Add("all");
        }

        arguments.Add("-i");
        arguments.Add(streamUrls[0]);
        if (streamUrls.Count == 2)
        {
            arguments.Add("-i");
            arguments.Add(streamUrls[1]);
        }

        arguments.Add("-map");
        arguments.Add("0:v:0");
        arguments.Add("-map");
        arguments.Add(streamUrls.Count == 2 ? "1:a:0" : "0:a:0?");
        arguments.Add("-c");
        arguments.Add("copy");
        arguments.Add("-mpegts_flags");
        arguments.Add("+resend_headers");
        arguments.Add("-f");
        arguments.Add("mpegts");
        arguments.Add(Path.DirectorySeparatorChar == '\\' ? "NUL" : "/dev/null");

        return RunAsync(
            executable,
            arguments,
            duration + TimeSpan.FromSeconds(45),
            cancellationToken,
            duration,
            summarizeFfmpegProgress: true);
    }

    private static async Task<MediaToolResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        TimeSpan? gracefulStopAfter = null,
        bool summarizeFfmpegProgress = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = gracefulStopAfter.HasValue,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
            {
                return MediaToolResult.NotAvailable(stopwatch.Elapsed);
            }
        }
        catch (Win32Exception)
        {
            return MediaToolResult.NotAvailable(stopwatch.Elapsed);
        }

        var firstMediaProgress = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!summarizeFfmpegProgress)
        {
            firstMediaProgress.TrySetResult(true);
        }

        var standardOutput = ReadStandardOutputAsync(
            process.StandardOutput,
            summarizeFfmpegProgress,
            firstMediaProgress);
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeout);

        var timedOut = false;
        var completedRequestedDuration = !gracefulStopAfter.HasValue;
        var sampledCpuTime = TimeSpan.Zero;
        try
        {
            var processExit = process.WaitForExitAsync(timeoutCancellation.Token);
            if (gracefulStopAfter.HasValue)
            {
                var startedOrExited = await Task.WhenAny(
                    processExit,
                    firstMediaProgress.Task);
                if (startedOrExited == firstMediaProgress.Task)
                {
                    await firstMediaProgress.Task;
                    var stopDelay = Task.Delay(
                        gracefulStopAfter.Value,
                        timeoutCancellation.Token);
                    var completed = await Task.WhenAny(processExit, stopDelay);
                    if (completed == stopDelay)
                    {
                        await stopDelay;
                        completedRequestedDuration = true;

                        // Sampled while the process is still alive: Linux drops
                        // the accounting as soon as it exits.
                        sampledCpuTime = TryReadCpuTime(process) ?? sampledCpuTime;
                        await RequestGracefulStopAsync(process);
                    }
                }
            }

            await processExit;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            KillProcess(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        stopwatch.Stop();
        var progress = summarizeFfmpegProgress
            ? ParseFfmpegProgress(output)
            : new FfmpegProgressSummary();
        var fragmentFailures = CountOccurrences(
            error,
            "Failed to open fragment of playlist");
        var corruptionWarnings = CountOccurrences(error, "Packet corrupt")
            + CountOccurrences(error, "Invalid NAL unit size")
            + CountOccurrences(error, "missing picture in access unit");
        var duplicateMoovWarnings = CountOccurrences(
            error,
            "Found duplicated MOOV Atom");
        var exitCode = timedOut ? 124 : process.ExitCode;
        if (!completedRequestedDuration && exitCode == 0)
        {
            exitCode = 125;
            error += Environment.NewLine
                + "ffmpeg exited before the requested wall-clock duration.";
        }

        return new MediaToolResult
        {
            ToolAvailable = true,
            ExitCode = exitCode,
            TimedOut = timedOut,
            Elapsed = stopwatch.Elapsed,
            CpuTime = TryReadCpuTime(process) ?? sampledCpuTime,
            MediaProcessed = progress.MediaProcessed,
            ProgressReportCount = progress.ReportCount,
            LastReportedSpeed = progress.LastSpeed,
            FragmentFailureCount = fragmentFailures,
            CorruptionWarningCount = corruptionWarnings,
            DuplicateMoovWarningCount = duplicateMoovWarnings,
            StandardOutput = summarizeFfmpegProgress
                ? string.Empty
                : SensitiveDataSanitizer.SanitizeText(output),
            StandardError = SensitiveDataSanitizer.SanitizeText(
                RemoveSummarizedFfmpegWarnings(error)),
        };
    }

    private static FfmpegProgressSummary ParseFfmpegProgress(string output)
    {
        long? firstPosition = null;
        long? lastPosition = null;
        var reportCount = 0;
        var lastSpeed = string.Empty;

        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(
                    line.Substring("out_time_us=".Length),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var position)
                && position >= 0)
            {
                firstPosition ??= position;
                lastPosition = position;
            }
            else if (line.StartsWith("speed=", StringComparison.Ordinal))
            {
                lastSpeed = line.Substring("speed=".Length);
            }
            else if (line.StartsWith("progress=", StringComparison.Ordinal))
            {
                reportCount++;
            }
        }

        TimeSpan? mediaProcessed = null;
        if (firstPosition.HasValue && lastPosition.HasValue)
        {
            mediaProcessed = TimeSpan.FromTicks(
                Math.Max(0, lastPosition.Value - firstPosition.Value) * 10);
        }

        return new FfmpegProgressSummary
        {
            MediaProcessed = mediaProcessed,
            ReportCount = reportCount,
            LastSpeed = lastSpeed,
        };
    }

    private static async Task<string> ReadStandardOutputAsync(
        StreamReader reader,
        bool detectMediaProgress,
        TaskCompletionSource<bool> firstMediaProgress)
    {
        if (!detectMediaProgress)
        {
            return await reader.ReadToEndAsync();
        }

        var output = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            output.AppendLine(line);
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(
                    line.Substring("out_time_us=".Length),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var position)
                && position >= 0)
            {
                firstMediaProgress.TrySetResult(true);
            }
        }

        return output.ToString();
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private static string RemoveSummarizedFfmpegWarnings(string value)
    {
        var result = new StringBuilder();
        var previousLineWasDuplicateMoov = false;
        using var reader = new StringReader(value);
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains("Found duplicated MOOV Atom. Skipped it", StringComparison.Ordinal))
            {
                previousLineWasDuplicateMoov = true;
                continue;
            }

            if (previousLineWasDuplicateMoov
                && line.Contains("Last message repeated", StringComparison.Ordinal))
            {
                continue;
            }

            previousLineWasDuplicateMoov = false;
            result.AppendLine(line);
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Reads the processor time, or returns null when the platform no longer
    /// exposes it. Linux discards the accounting of an exited process, and the
    /// failure must not mask the tool result being reported.
    /// </summary>
    private static TimeSpan? TryReadCpuTime(Process process)
    {
        try
        {
            return process.TotalProcessorTime;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static async Task RequestGracefulStopAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("q");
                await process.StandardInput.FlushAsync();
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited while the graceful-stop request was being sent.
        }
        catch (IOException)
        {
            // The process closed stdin while the stop request was being sent.
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
    }
}

internal sealed class FfmpegProgressSummary
{
    public TimeSpan? MediaProcessed { get; set; }

    public int ReportCount { get; set; }

    public string LastSpeed { get; set; } = string.Empty;
}

internal sealed class MediaToolResult
{
    public bool ToolAvailable { get; set; }

    public int ExitCode { get; set; }

    public bool TimedOut { get; set; }

    public TimeSpan Elapsed { get; set; }

    public TimeSpan CpuTime { get; set; }

    public TimeSpan? MediaProcessed { get; set; }

    public int ProgressReportCount { get; set; }

    public string LastReportedSpeed { get; set; } = string.Empty;

    public int FragmentFailureCount { get; set; }

    public int CorruptionWarningCount { get; set; }

    public int DuplicateMoovWarningCount { get; set; }

    public string StandardOutput { get; set; } = string.Empty;

    public string StandardError { get; set; } = string.Empty;

    public static MediaToolResult NotAvailable(TimeSpan elapsed)
    {
        return new MediaToolResult
        {
            ToolAvailable = false,
            ExitCode = 127,
            Elapsed = elapsed,
        };
    }
}
