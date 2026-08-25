using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Infrastructure;
using Emby.Zattoo.Models;
using Emby.Zattoo.Zattoo;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;

namespace Emby.Zattoo.Plugin.LiveTv
{
    /// <summary>Owns one server-side FFmpeg remux from Zattoo HLS7 to MPEG-TS.</summary>
    internal sealed class ZattooLiveStream : ILiveStream
    {
        private readonly string channelId;
        private readonly string channelName;
        private readonly IZattooClient client;
        private readonly ZattooPreferredQuality preferredQuality;
        private readonly string ffmpegPath;
        private readonly ILogger logger;
        private readonly SemaphoreSlim lifecycleLock = new SemaphoreSlim(1, 1);
        private Process? process;
        private CancellationTokenSource? streamCancellation;
        private Task? stderrMonitor;
        private int copyStarted;
        private int duplicateMoovWarnings;
        private bool closing;

        public ZattooLiveStream(
            string tunerHostId,
            string channelId,
            string channelName,
            IZattooClient client,
            ZattooPreferredQuality preferredQuality,
            string ffmpegPath,
            ILogger logger)
        {
            TunerHostId = tunerHostId ?? string.Empty;
            this.channelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
            this.channelName = channelName ?? string.Empty;
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.preferredQuality = preferredQuality;
            this.ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            UniqueId = Guid.NewGuid().ToString("N");
            MediaSource = ZattooMediaSourceFactory.Create(channelId, this.channelName);
            MediaSource.LiveStreamId = UniqueId;
            OriginalStreamId = MediaSource.Id;
        }

        public int ConsumerCount { get; set; }

        public string OriginalStreamId { get; set; }

        public string TunerHostId { get; }

        public bool EnableStreamSharing => false;

        public MediaSourceInfo MediaSource { get; set; }

        public string UniqueId { get; }

        public DateTimeOffset DateOpened { get; private set; }

        public bool SupportsCopyTo => true;

        public async Task Open(CancellationToken openCancellationToken)
        {
            await lifecycleLock.WaitAsync(openCancellationToken).ConfigureAwait(false);
            try
            {
                if (process != null)
                {
                    return;
                }

                logger.Info("Opening Zattoo channel {0}.", channelName);
                var stream = await client.GetStreamAsync(
                        channelId,
                        preferredQuality,
                        ZattooStreamFormat.Hls,
                        openCancellationToken)
                    .ConfigureAwait(false);
                var streamUrl = stream.Url;
                if (!stream.IsSupported || string.IsNullOrWhiteSpace(streamUrl))
                {
                    throw new ZattooStreamUnavailableException(
                        "Zattoo did not return a usable non-DRM HLS stream.");
                }

                var selection = await HlsManifestResolver.ResolveAsync(
                        streamUrl!,
                        stream.Height,
                        openCancellationToken)
                    .ConfigureAwait(false);
                var startInfo = CreateProcessStartInfo(selection);
                var newProcess = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true,
                };

                try
                {
                    if (!newProcess.Start())
                    {
                        throw new ZattooStreamUnavailableException(
                            "FFmpeg could not be started for the Zattoo live stream.");
                    }
                }
                catch (Win32Exception exception)
                {
                    newProcess.Dispose();
                    throw new ZattooStreamUnavailableException(
                        "FFmpeg could not be started. Configure its executable path in the Zattoo plugin settings.",
                        exception);
                }

                var newStderrMonitor = MonitorStandardErrorAsync(newProcess);
                if (newProcess.HasExited)
                {
                    await newStderrMonitor.ConfigureAwait(false);
                    newProcess.Dispose();
                    throw new ZattooStreamUnavailableException(
                        "FFmpeg exited while opening the Zattoo live stream.");
                }

                streamCancellation = new CancellationTokenSource();
                process = newProcess;
                stderrMonitor = newStderrMonitor;
                DateOpened = DateTimeOffset.UtcNow;
            }
            finally
            {
                lifecycleLock.Release();
            }
        }

        public async Task CopyToAsync(
            PipeWriter writer,
            CancellationToken cancellationToken)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            using (var stream = writer.AsStream(leaveOpen: true))
            {
                await CopyToCoreAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task CopyToAsync(
            Stream writer,
            DateTimeOffset? wallClockStartTime,
            Action<SegmentedStreamSegmentInfo> onSegmentWritten,
            CancellationToken cancellationToken)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            return CopyToCoreAsync(writer, cancellationToken);
        }

        public async Task Close()
        {
            Process? processToClose;
            CancellationTokenSource? cancellationToDispose;
            Task? monitorToAwait;

            await lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (closing)
                {
                    return;
                }

                closing = true;
                processToClose = process;
                cancellationToDispose = streamCancellation;
                monitorToAwait = stderrMonitor;
                cancellationToDispose?.Cancel();
            }
            finally
            {
                lifecycleLock.Release();
            }

            try
            {
                if (processToClose != null)
                {
                    await StopProcessAsync(processToClose).ConfigureAwait(false);
                    if (monitorToAwait != null)
                    {
                        await monitorToAwait.ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                processToClose?.Dispose();
                cancellationToDispose?.Dispose();
                logger.Info(
                    "Closed Zattoo channel {0}; duplicated MOOV warnings: {1}.",
                    channelName,
                    duplicateMoovWarnings);
            }
        }

        private async Task CopyToCoreAsync(
            Stream writer,
            CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref copyStarted, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "This Zattoo live stream already has an active consumer.");
            }

            var currentProcess = process
                ?? throw new InvalidOperationException("The Zattoo live stream is not open.");
            var currentCancellation = streamCancellation
                ?? throw new InvalidOperationException("The Zattoo live stream is not open.");
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                currentCancellation.Token))
            {
                try
                {
                    await currentProcess.StandardOutput.BaseStream.CopyToAsync(
                            writer,
                            81920,
                            linked.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    currentCancellation.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }

            if (!closing && currentProcess.HasExited && currentProcess.ExitCode != 0)
            {
                throw new IOException("FFmpeg stopped unexpectedly while remuxing Zattoo Live TV.");
            }
        }

        private ProcessStartInfo CreateProcessStartInfo(HlsPlaylistSelection selection)
        {
            var arguments = new List<string>
            {
                "-hide_banner",
                "-loglevel", "warning",
                "-i", selection.VideoUri.AbsoluteUri,
            };
            if (selection.AudioUri != null)
            {
                arguments.Add("-i");
                arguments.Add(selection.AudioUri.AbsoluteUri);
            }

            arguments.Add("-map");
            arguments.Add("0:v:0");
            arguments.Add("-map");
            arguments.Add(selection.AudioUri == null ? "0:a:0?" : "1:a:0");
            arguments.Add("-c");
            arguments.Add("copy");
            arguments.Add("-mpegts_flags");
            arguments.Add("+resend_headers");
            arguments.Add("-f");
            arguments.Add("mpegts");
            arguments.Add("pipe:1");

            return new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = JoinArguments(arguments),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
        }

        private async Task MonitorStandardErrorAsync(Process currentProcess)
        {
            var previousLineWasDuplicateMoov = false;
            while (await currentProcess.StandardError.ReadLineAsync().ConfigureAwait(false)
                is string line)
            {
                if (line.IndexOf(
                        "Found duplicated MOOV Atom. Skipped it",
                        StringComparison.Ordinal) >= 0)
                {
                    Interlocked.Increment(ref duplicateMoovWarnings);
                    previousLineWasDuplicateMoov = true;
                    continue;
                }

                if (previousLineWasDuplicateMoov
                    && line.IndexOf("Last message repeated", StringComparison.Ordinal) >= 0)
                {
                    continue;
                }

                previousLineWasDuplicateMoov = false;
                var safeLine = SensitiveDataSanitizer.SanitizeText(line);
                if (!string.IsNullOrWhiteSpace(safeLine))
                {
                    logger.Debug("Zattoo FFmpeg: {0}", safeLine);
                }
            }
        }

        private static async Task StopProcessAsync(Process currentProcess)
        {
            if (currentProcess.HasExited)
            {
                return;
            }

            try
            {
                await currentProcess.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                await currentProcess.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Standard input can already be closed while FFmpeg is still stopping.
            }
            catch (IOException)
            {
                // Continue with the bounded wait and forced termination fallback.
            }

            var exit = WaitForExitAsync(currentProcess);
            if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(5)))
                    .ConfigureAwait(false)
                != exit)
            {
                try
                {
                    currentProcess.Kill();
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }

            await WaitForExitAsync(currentProcess).ConfigureAwait(false);
        }

        private static Task WaitForExitAsync(Process currentProcess)
        {
            if (currentProcess.HasExited)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            currentProcess.Exited += (_, __) => completion.TrySetResult(true);
            currentProcess.EnableRaisingEvents = true;
            if (currentProcess.HasExited)
            {
                completion.TrySetResult(true);
            }

            return completion.Task;
        }

        private static string JoinArguments(IEnumerable<string> arguments)
        {
            var result = new StringBuilder();
            foreach (var argument in arguments)
            {
                if (result.Length > 0)
                {
                    result.Append(' ');
                }

                result.Append(QuoteArgument(argument));
            }

            return result.ToString();
        }

        private static string QuoteArgument(string value)
        {
            if (value.Length > 0
                && value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return value;
            }

            var result = new StringBuilder();
            result.Append('"');
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    result.Append('\\', (backslashes * 2) + 1);
                    result.Append(character);
                    backslashes = 0;
                    continue;
                }

                result.Append('\\', backslashes);
                backslashes = 0;
                result.Append(character);
            }

            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }
    }
}
