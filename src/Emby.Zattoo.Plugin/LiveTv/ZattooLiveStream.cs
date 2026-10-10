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
    internal class ZattooLiveStream : ILiveStream
    {
        private static readonly TimeSpan ConsumerHandoverTimeout =
            TimeSpan.FromSeconds(5);

        /// <summary>
        /// How far back a standard error line may be read to explain an end of
        /// stream. FFmpeg reports the cause seconds before it gives up, and it
        /// recovers from older warnings on its own: blaming one of those would
        /// misname the failure in the only log that explains it.
        /// </summary>
        private static readonly TimeSpan RecentStandardErrorWindow =
            TimeSpan.FromSeconds(30);

        private static readonly TimeSpan ProcessExitGrace = TimeSpan.FromSeconds(2);

        private readonly string channelId;
        private readonly string channelName;
        private readonly IZattooClient client;
        private readonly ZattooStreamCapacity streamCapacity;
        private readonly ZattooPreferredQuality preferredQuality;
        private readonly string ffmpegPath;
        private readonly string localApiUrl;
        private readonly ILogger logger;
        private readonly SemaphoreSlim lifecycleLock = new SemaphoreSlim(1, 1);
        private readonly ZattooStreamConsumerGate consumerGate =
            new ZattooStreamConsumerGate();
        private readonly ZattooStandardErrorBuffer recentStandardError =
            new ZattooStandardErrorBuffer();
        private readonly ZattooStreamRestartBudget restartBudget =
            new ZattooStreamRestartBudget();
        private Process? process;
        private CancellationTokenSource? streamCancellation;
        private Task? stderrMonitor;
        private IDisposable? capacityLease;
        private int consumerCount;
        private int consumerNumber;
        private int duplicateMoovWarnings;
        private bool closing;

        public ZattooLiveStream(
            string tunerHostId,
            string channelId,
            string channelName,
            IZattooClient client,
            ZattooStreamCapacity streamCapacity,
            ZattooPreferredQuality preferredQuality,
            string ffmpegPath,
            string localApiUrl,
            ILogger logger)
        {
            TunerHostId = tunerHostId ?? string.Empty;
            this.channelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
            this.channelName = channelName ?? string.Empty;
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.streamCapacity = streamCapacity
                ?? throw new ArgumentNullException(nameof(streamCapacity));
            this.preferredQuality = preferredQuality;
            this.ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
            if (localApiUrl == null)
            {
                throw new ArgumentNullException(nameof(localApiUrl));
            }

            // Validated here so that Open cannot start FFmpeg and then fail while
            // switching the media source to the local endpoint.
            if (!ZattooMediaSourceFactory.IsSupportedLocalApiUrl(localApiUrl))
            {
                throw new ArgumentException(
                    "A valid local Emby API URL is required.",
                    nameof(localApiUrl));
            }

            this.localApiUrl = localApiUrl;
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            UniqueId = Guid.NewGuid().ToString("N");
            MediaSource = ZattooMediaSourceFactory.Create(channelId, this.channelName);
            MediaSource.LiveStreamId = UniqueId;
            OriginalStreamId = MediaSource.Id;
        }

        /// <summary>Gets or sets how many Emby consumers hold this stream.</summary>
        /// <remarks>
        /// Emby 4.9 counts its consumers itself through this setter; Emby 4.10
        /// removed the setter and calls <see cref="AddConsumer"/> and
        /// <see cref="RemoveConsumer"/> instead. These three members are virtual,
        /// and the class is therefore not sealed, so that the runtime binds them
        /// to whichever interface the hosting server declares: one DLL built
        /// against the 4.9 SDK then loads on both versions.
        /// </remarks>
        public virtual int ConsumerCount
        {
            get => Volatile.Read(ref consumerCount);
            set => Volatile.Write(ref consumerCount, Math.Max(0, value));
        }

        public string OriginalStreamId { get; set; }

        public string TunerHostId { get; }

        public bool EnableStreamSharing => false;

        public MediaSourceInfo MediaSource { get; set; }

        public string UniqueId { get; }

        public DateTimeOffset DateOpened { get; private set; }

        public bool SupportsCopyTo => true;

        /// <summary>Counts one more Emby consumer (Emby 4.10 and later).</summary>
        public virtual void AddConsumer(string consumerId)
        {
            Interlocked.Increment(ref consumerCount);
        }

        /// <summary>Counts one Emby consumer less (Emby 4.10 and later).</summary>
        /// <remarks>
        /// An unmatched call must not leave the count negative: it would then
        /// never read as zero again once the real consumers are gone.
        /// </remarks>
        public virtual void RemoveConsumer(string consumerId)
        {
            int current;
            do
            {
                current = Volatile.Read(ref consumerCount);
                if (current == 0)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref consumerCount, current - 1, current) != current);
        }

        public async Task Open(CancellationToken openCancellationToken)
        {
            await lifecycleLock.WaitAsync(openCancellationToken).ConfigureAwait(false);
            try
            {
                if (process != null)
                {
                    return;
                }

                if (closing)
                {
                    throw new InvalidOperationException(
                        "This Zattoo live stream is already closing.");
                }

                var acquiredLease = streamCapacity.TryAcquire();
                if (acquiredLease == null)
                {
                    throw new ZattooStreamUnavailableException(
                        "The Zattoo account concurrent stream capacity is already in use.");
                }

                Process? startedProcess = null;
                Task? startedStderrMonitor = null;
                try
                {
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
                    var newProcess = StartFfmpeg(selection);
                    startedProcess = newProcess;
                    startedStderrMonitor = MonitorStandardErrorAsync(newProcess);
                    if (newProcess.HasExited)
                    {
                        throw new ZattooStreamUnavailableException(
                            "FFmpeg exited while opening the Zattoo live stream.");
                    }

                    streamCancellation = new CancellationTokenSource();
                    process = newProcess;
                    stderrMonitor = startedStderrMonitor;
                    capacityLease = acquiredLease;
                    DateOpened = DateTimeOffset.UtcNow;
                    ZattooMediaSourceFactory.DescribeStreams(
                        MediaSource,
                        selection,
                        stream);
                    ZattooMediaSourceFactory.UseLocalLiveStreamEndpoint(
                        MediaSource,
                        localApiUrl,
                        UniqueId);
                }
                catch
                {
                    // Nothing must survive a failed open: Emby never calls Close on
                    // a live stream that did not open, so an already started FFmpeg
                    // would keep pulling the Zattoo stream forever.
                    process = null;
                    stderrMonitor = null;
                    capacityLease = null;
                    var abandonedCancellation = streamCancellation;
                    streamCancellation = null;
                    abandonedCancellation?.Dispose();
                    if (startedProcess != null)
                    {
                        logger.Warn(
                            "Stopping FFmpeg because the Zattoo live stream for {0} failed to open.",
                            channelName);
                        await StopProcessAsync(startedProcess).ConfigureAwait(false);
                        if (startedStderrMonitor != null)
                        {
                            await startedStderrMonitor.ConfigureAwait(false);
                        }

                        startedProcess.Dispose();
                    }

                    acquiredLease.Dispose();
                    throw;
                }
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
            IDisposable? leaseToRelease;

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
                leaseToRelease = capacityLease;
                capacityLease = null;
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
                leaseToRelease?.Dispose();
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
            // Emby attaches more than once to the same live stream, for instance
            // when media detection runs before a transcode. Consumers are served
            // one after another from the same pipe: a new one resumes at the
            // current position, which is what live television means anyway.
            var consumer = Interlocked.Increment(ref consumerNumber);
            if (!await consumerGate.TryEnterAsync(
                    ConsumerHandoverTimeout,
                    cancellationToken).ConfigureAwait(false))
            {
                var refused = new InvalidOperationException(
                    "This Zattoo live stream already has an active consumer.");
                logger.ErrorException(
                    "Zattoo live stream consumer {0} refused for channel {1}.",
                    refused,
                    consumer,
                    channelName);
                throw refused;
            }

            var attachedAt = DateTimeOffset.UtcNow;
            logger.Info(
                "Zattoo live stream consumer {0} attached for channel {1}.",
                consumer,
                channelName);
            try
            {
                // One consumer, several FFmpeg processes: when a stream dies under
                // an active consumer, the pipe is refilled from a freshly signed
                // URL instead of ending. Emby never sees the end of file, so a
                // recording keeps growing across the incident.
                while (true)
                {
                    var currentProcess = process
                        ?? throw new InvalidOperationException("The Zattoo live stream is not open.");
                    var currentCancellation = streamCancellation
                        ?? throw new InvalidOperationException("The Zattoo live stream is not open.");
                    if (closing)
                    {
                        throw new InvalidOperationException(
                            "This Zattoo live stream is already closing.");
                    }

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

                    if (closing)
                    {
                        return;
                    }

                    // Reaching this point means FFmpeg closed its output while the
                    // stream was still wanted. A clean exit code is not good news
                    // here: live television that ends by itself is a truncated
                    // recording, and Emby would otherwise record it as a normal end.
                    var failure = await DescribeUnexpectedEndAsync(currentProcess)
                        .ConfigureAwait(false);
                    var elapsed = (DateTimeOffset.UtcNow - attachedAt).TotalSeconds;
                    if (failure.Kind != ZattooStreamFailureKind.Recoverable)
                    {
                        logger.Error(
                            "Zattoo channel {0} ended after {1:F1}s: {2} (FFmpeg exit code {3}); a new stream request cannot repair it.",
                            channelName,
                            elapsed,
                            failure.Description,
                            failure.ExitCode);
                        throw new IOException(
                            "The Zattoo live stream ended unexpectedly: " + failure.Description + ".");
                    }

                    if (!restartBudget.TryAcquire(DateTimeOffset.UtcNow))
                    {
                        logger.Error(
                            "Zattoo channel {0} ended after {1:F1}s: {2} (FFmpeg exit code {3}); the reopen budget is spent.",
                            channelName,
                            elapsed,
                            failure.Description,
                            failure.ExitCode);
                        throw new IOException(
                            "The Zattoo live stream ended repeatedly: " + failure.Description + ".");
                    }

                    logger.Warn(
                        "Zattoo channel {0} ended after {1:F1}s: {2} (FFmpeg exit code {3}); reopening with a new stream URL.",
                        channelName,
                        elapsed,
                        failure.Description,
                        failure.ExitCode);
                    if (!await ReopenAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                // Emby only reports HTTP 500 to the client for this endpoint, so the
                // reason has to reach the server log from here.
                logger.ErrorException(
                    "Zattoo live stream consumer {0} failed for channel {1}.",
                    exception,
                    consumer,
                    channelName);
                throw;
            }
            finally
            {
                consumerGate.Exit();
                logger.Info(
                    "Zattoo live stream consumer {0} detached from channel {1} after {2:F1}s.",
                    consumer,
                    channelName,
                    (DateTimeOffset.UtcNow - attachedAt).TotalSeconds);
            }
        }

        private ProcessStartInfo CreateProcessStartInfo(HlsPlaylistSelection selection)
        {
            var arguments = new List<string>
            {
                "-hide_banner",
                "-loglevel", "warning",
            };
            AddInput(arguments, selection.VideoUri);
            if (selection.AudioUri != null)
            {
                AddInput(arguments, selection.AudioUri);
            }

            arguments.Add("-map");
            arguments.Add("0:v:0");
            arguments.Add("-map");
            arguments.Add(selection.AudioUri == null ? "0:a:0?" : "1:a:0");
            arguments.Add("-c");
            arguments.Add("copy");
            arguments.Add("-mpegts_flags");
            arguments.Add("+resend_headers");

            // Hand each packet to Emby as it is produced. Buffering here only
            // delays the first bytes a client waits for before it gives up.
            arguments.Add("-flush_packets");
            arguments.Add("1");
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

        /// <summary>
        /// Adds one input along with the HTTP reconnection options. Without them a
        /// single failed segment request ends the remux and the viewer loses the
        /// channel; with them FFmpeg retries by itself. Only options available in
        /// every FFmpeg the server is likely to ship are used, because an unknown
        /// option would stop it from starting at all.
        /// </summary>
        private static void AddInput(List<string> arguments, Uri input)
        {
            arguments.Add("-reconnect");
            arguments.Add("1");
            arguments.Add("-reconnect_streamed");
            arguments.Add("1");
            arguments.Add("-reconnect_delay_max");
            arguments.Add("5");
            arguments.Add("-i");
            arguments.Add(input.AbsoluteUri);
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
                    RememberStandardErrorLine(safeLine);
                    logger.Debug("Zattoo FFmpeg: {0}", safeLine);
                }
            }
        }

        private Process StartFfmpeg(HlsPlaylistSelection selection)
        {
            var newProcess = new Process
            {
                StartInfo = CreateProcessStartInfo(selection),
                EnableRaisingEvents = true,
            };

            try
            {
                if (!newProcess.Start())
                {
                    newProcess.Dispose();
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

            return newProcess;
        }

        /// <summary>
        /// Replaces the FFmpeg process behind an open stream with one fed by a
        /// freshly signed URL, without touching the capacity lease already held
        /// and without ending the pipe the consumer is reading.
        /// </summary>
        /// <returns>False when the stream was closed instead of reopened.</returns>
        private async Task<bool> ReopenAsync(CancellationToken cancellationToken)
        {
            Process? previousProcess;
            Task? previousMonitor;
            CancellationTokenSource? currentCancellation;

            await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (closing)
                {
                    return false;
                }

                previousProcess = process;
                previousMonitor = stderrMonitor;
                currentCancellation = streamCancellation;

                // The stream is briefly without a process on purpose: a Close
                // arriving now must find nothing to stop rather than a process
                // this method is already disposing. The lock is released before
                // the provider is called, so Close never waits on the network.
                process = null;
                stderrMonitor = null;
            }
            finally
            {
                lifecycleLock.Release();
            }

            if (previousProcess != null)
            {
                await StopProcessAsync(previousProcess).ConfigureAwait(false);
                if (previousMonitor != null)
                {
                    await previousMonitor.ConfigureAwait(false);
                }

                previousProcess.Dispose();
            }

            // The next classification must not read the lines that explained this
            // failure.
            recentStandardError.Clear();

            Process? startedProcess = null;
            Task? startedMonitor = null;
            try
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    currentCancellation?.Token ?? CancellationToken.None))
                {
                    var stream = await client.GetStreamAsync(
                            channelId,
                            preferredQuality,
                            ZattooStreamFormat.Hls,
                            linked.Token)
                        .ConfigureAwait(false);
                    if (!stream.IsSupported || string.IsNullOrWhiteSpace(stream.Url))
                    {
                        throw new ZattooStreamUnavailableException(
                            "Zattoo did not return a usable non-DRM HLS stream.");
                    }

                    var selection = await HlsManifestResolver.ResolveAsync(
                            stream.Url!,
                            stream.Height,
                            linked.Token)
                        .ConfigureAwait(false);
                    startedProcess = StartFfmpeg(selection);
                    startedMonitor = MonitorStandardErrorAsync(startedProcess);
                    if (startedProcess.HasExited)
                    {
                        throw new ZattooStreamUnavailableException(
                            "FFmpeg exited while reopening the Zattoo live stream.");
                    }
                }
            }
            catch (OperationCanceledException) when (closing)
            {
                await DiscardAsync(startedProcess, startedMonitor).ConfigureAwait(false);
                return false;
            }
            catch
            {
                await DiscardAsync(startedProcess, startedMonitor).ConfigureAwait(false);
                throw;
            }

            await lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (closing)
                {
                    await DiscardAsync(startedProcess, startedMonitor).ConfigureAwait(false);
                    return false;
                }

                process = startedProcess;
                stderrMonitor = startedMonitor;
            }
            finally
            {
                lifecycleLock.Release();
            }

            logger.Info(
                "Zattoo channel {0} reopened with a new stream URL; the pipe was not interrupted.",
                channelName);
            return true;
        }

        private static async Task DiscardAsync(Process? startedProcess, Task? startedMonitor)
        {
            if (startedProcess == null)
            {
                return;
            }

            await StopProcessAsync(startedProcess).ConfigureAwait(false);
            if (startedMonitor != null)
            {
                await startedMonitor.ConfigureAwait(false);
            }

            startedProcess.Dispose();
        }

        /// <summary>
        /// Keeps the last standard error lines, already sanitized, so that an end
        /// of stream can be explained without keeping the whole FFmpeg output.
        /// </summary>
        private void RememberStandardErrorLine(string line)
        {
            recentStandardError.Add(line, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Explains an FFmpeg process that ended while the stream was still
        /// wanted. The process is given a short grace period first, because
        /// standard output closes slightly before the exit code is available.
        /// </summary>
        private async Task<ZattooStreamFailure> DescribeUnexpectedEndAsync(
            Process currentProcess)
        {
            var exit = WaitForExitAsync(currentProcess);
            await Task.WhenAny(exit, Task.Delay(ProcessExitGrace)).ConfigureAwait(false);

            var exitCode = 0;
            if (currentProcess.HasExited)
            {
                exitCode = currentProcess.ExitCode;
            }

            return ZattooStreamFailureClassifier.Classify(
                exitCode,
                recentStandardError.ReadRecent(
                    DateTimeOffset.UtcNow,
                    RecentStandardErrorWindow));
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
