using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Models;
using Emby.Zattoo.Zattoo;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

namespace Emby.Zattoo.Plugin.LiveTv
{
    /// <summary>Live TV tuner backed by one Zattoo account.</summary>
    public sealed class ZattooTunerHost : BaseTunerHost, IDisposable
    {
        private static readonly TimeSpan RetiredClientGracePeriod =
            TimeSpan.FromMinutes(5);

        private readonly object clientSync = new object();
        private readonly ZattooRetiredClientQueue retiredClients =
            new ZattooRetiredClientQueue(RetiredClientGracePeriod);
        private readonly ZattooStreamCapacity streamCapacity =
            new ZattooStreamCapacity();
        private readonly SemaphoreSlim catalogueLock = new SemaphoreSlim(1, 1);
        private readonly IFfmpegManager? ffmpegManager;
        private IZattooClient? client;
        private IZattooClient? catalogueLoadedFor;
        private ZattooRuntimeSettings? cachedSettings;
        private long clientConfigurationRevision = long.MinValue;
        private bool disposed;

        public ZattooTunerHost(IServerApplicationHost applicationHost)
            : base(applicationHost)
        {
            ffmpegManager = applicationHost.Resolve<IFfmpegManager>();
            Logger.Info("Zattoo tuner initialized; account settings are managed by the plugin page.");
        }

        public override string Name => "Zattoo";

        public override string Type => "zattoo";

        protected override bool UseTunerHostIdAsPrefix => true;

        public override bool SupportsGuideData(TunerHostInfo tuner)
        {
            return true;
        }

        public override bool SupportsRemappingGuideData(TunerHostInfo tuner)
        {
            return false;
        }

        public override TunerHostInfo GetDefaultConfiguration()
        {
            return new TunerHostInfo
            {
                Type = Type,
                FriendlyName = Name,
                TunerCount = 1,
                ImportGuideData = true,
                AllowHWTranscoding = false,
            };
        }

        protected override async Task<List<ProgramInfo>> GetProgramsInternal(
            TunerHostInfo tuner,
            string tunerChannelId,
            DateTimeOffset startDateUtc,
            DateTimeOffset endDateUtc,
            CancellationToken cancellationToken)
        {
            var context = RequireClient();
            await EnsureCatalogueAsync(context, tuner, cancellationToken)
                .ConfigureAwait(false);
            var programs = await context.Client.GetProgramsAsync(
                    new[] { tunerChannelId },
                    startDateUtc,
                    endDateUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            return programs.Select(ZattooProgramMapper.Map).ToList();
        }

        protected override async Task<List<ChannelInfo>> GetChannelsInternal(
            TunerHostInfo tuner,
            CancellationToken cancellationToken)
        {
            var context = RequireClient();
            var selected = await LoadCatalogueAsync(context, tuner, cancellationToken)
                .ConfigureAwait(false);
            var channelIdPrefix = GetChannelIdPrefix(tuner);
            return selected
                .Select(channel => ZattooChannelMapper.Map(channel, channelIdPrefix))
                .ToList();
        }

        /// <summary>
        /// Loads the catalogue and applies everything derived from it: the guide
        /// channel filter, the favorites used to prioritise enrichment and the
        /// concurrent stream capacity.
        /// </summary>
        private async Task<IReadOnlyList<ZattooChannel>> LoadCatalogueAsync(
            ClientContext context,
            TunerHostInfo tuner,
            CancellationToken cancellationToken)
        {
            var channels = await context.Client.GetChannelsAsync(cancellationToken)
                .ConfigureAwait(false);
            var session = context.Client.SessionInfo;
            if (tuner.ImportFavoritesOnly && session?.FavoritesAvailable == false)
            {
                // Importing an empty list would drop every channel Emby already
                // knows. Fail the refresh instead and keep the existing lineup.
                throw new ZattooApiException(
                    "Zattoo did not return the channel favorites. The channel list "
                    + "was kept unchanged because this tuner imports favorites only.");
            }

            if (session?.FavoritesAvailable == false)
            {
                Logger.Warn(
                    "Zattoo did not return the channel favorites; every channel is imported as non-favorite.");
            }

            var importable = ZattooChannelFilter.Apply(
                channels,
                context.Settings.ChannelImportMode);
            var selected = (tuner.ImportFavoritesOnly
                    ? importable.Where(channel => channel.IsFavorite)
                    : importable)
                .ToArray();
            context.Client.SetImportedGuideChannels(
                selected.Select(channel => channel.Id).ToArray());
            var concurrentStreams = Math.Max(
                1,
                session?.MaximumConcurrentStreams ?? 1);
            streamCapacity.UpdateLimit(concurrentStreams);

            // Emby compares its own TunerCount with the open streams before it ever
            // calls this plugin, so the detected capacity has to reach the tuner
            // Emby is holding, not only the internal lock.
            tuner.TunerCount = concurrentStreams;
            lock (clientSync)
            {
                catalogueLoadedFor = context.Client;
            }

            Logger.Info(
                "Loaded {0} of {1} Zattoo channels using {2} import mode.",
                selected.Length,
                channels.Count,
                context.Settings.ChannelImportMode);
            if (session != null)
            {
                Logger.Info(
                    "Zattoo account capabilities: {0} playable channel(s), {1} DRM-only, {2} unavailable, maximum non-DRM height {3}, replay {4}, cloud recording limit {5}, concurrent stream capacity {6} ({7}).",
                    session.PlayableChannelCount,
                    session.DrmOnlyChannelCount,
                    session.UnavailableChannelCount,
                    session.MaximumPlayableHeight?.ToString() ?? "unknown",
                    session.ReplayAvailable ? "available" : "unavailable",
                    session.RecordingNumberLimit,
                    concurrentStreams,
                    session.ConcurrentStreamLimitIsInferred
                        ? "inferred from numerical account limits"
                        : "reported by the provider");
            }

            return selected;
        }

        /// <summary>
        /// Guarantees the catalogue was loaded for the current client. Emby serves
        /// guide requests and stream openings from its own channel cache, which
        /// survives a restart, so GetChannelsInternal may never have run in this
        /// process.
        /// </summary>
        private async Task EnsureCatalogueAsync(
            ClientContext context,
            TunerHostInfo tuner,
            CancellationToken cancellationToken)
        {
            if (IsCatalogueLoaded(context.Client))
            {
                return;
            }

            await catalogueLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsCatalogueLoaded(context.Client))
                {
                    return;
                }

                await LoadCatalogueAsync(context, tuner, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (!(exception is OperationCanceledException))
            {
                // Degraded but usable: the guide still loads, without the channel
                // filter and without favorite priority for enrichment.
                Logger.ErrorException(
                    "The Zattoo catalogue could not be loaded; this pass runs without the imported channel filter.",
                    exception);
            }
            finally
            {
                catalogueLock.Release();
            }
        }

        /// <summary>
        /// Returns the configured FFmpeg, or the one Emby runs itself. Emby always
        /// knows where its own binary is, including inside a container, which is
        /// more reliable than a path typed by hand or a PATH lookup.
        /// </summary>
        private string ResolveFfmpegPath(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return configuredPath;
            }

            string? encoderPath = null;
            try
            {
                encoderPath = ffmpegManager?.FfmpegConfiguration?.EncoderPath;
            }
            catch (Exception exception)
            {
                Logger.ErrorException(
                    "The FFmpeg path used by Emby could not be read; falling back to the system path.",
                    exception);
            }

            if (string.IsNullOrWhiteSpace(encoderPath))
            {
                Logger.Warn(
                    "Emby did not report an FFmpeg path; falling back to 'ffmpeg' from the system path.");
                return "ffmpeg";
            }

            return encoderPath!;
        }

        private bool IsCatalogueLoaded(IZattooClient current)
        {
            lock (clientSync)
            {
                return ReferenceEquals(catalogueLoadedFor, current);
            }
        }

        protected override Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(
            TunerHostInfo tuner,
            BaseItem dbChannnel,
            ChannelInfo tunerChannel,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireClient();
            var source = ZattooMediaSourceFactory.Create(
                tunerChannel.TunerChannelId ?? tunerChannel.Id,
                tunerChannel.Name);
            return Task.FromResult(new List<MediaSourceInfo> { source });
        }

        protected override async Task<ILiveStream> GetChannelStream(
            TunerHostInfo tuner,
            BaseItem dbChannnel,
            ChannelInfo tunerChannel,
            string mediaSourceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = RequireClient();

            // The stream capacity comes from the catalogue, so it has to be known
            // before the first stream of a freshly started server is opened.
            await EnsureCatalogueAsync(context, tuner, cancellationToken)
                .ConfigureAwait(false);
            var channelId = tunerChannel.TunerChannelId ?? tunerChannel.Id;
            context.Client.PrioritizeGuideDetails(channelId);
            ILiveStream stream = new ZattooLiveStream(
                tuner.Id,
                channelId,
                tunerChannel.Name,
                context.Client,
                streamCapacity,
                context.Settings.PreferredQuality,
                ResolveFfmpegPath(context.Settings.FfmpegPath),
                AppHost.GetLocalApiUrl("127.0.0.1"),
                Logger);
            return stream;
        }

        public void Dispose()
        {
            var clientsToDispose = new List<IZattooClient>();
            lock (clientSync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                if (client != null)
                {
                    clientsToDispose.Add(client);
                    client = null;
                }
            }

            clientsToDispose.AddRange(retiredClients.TakeAll());
            DisposeClients(clientsToDispose);
        }

        private ClientContext RequireClient()
        {
            ClientContext context;
            IZattooClient? clientToRetire = null;
            var applyGuideDays = 0;
            lock (clientSync)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(ZattooTunerHost));
                }

                var plugin = Plugin.Instance
                    ?? throw new InvalidOperationException(
                        "The Zattoo plugin has not finished loading.");
                if (client == null
                    || cachedSettings == null
                    || clientConfigurationRevision != plugin.ConfigurationRevision)
                {
                    var settings = plugin.GetRuntimeSettings(out var revision);
                    settings.ClientOptions.GuideDetailsProgress =
                        LogGuideDetailsProgress;
                    var replacement = new ZattooClient(settings.ClientOptions);

                    // The stream capacity keeps the limit detected for the previous
                    // session until the next channel refresh publishes a new one.
                    // Resetting it here would refuse a legitimate second stream
                    // while a recording is running.
                    clientToRetire = client;
                    client = replacement;
                    catalogueLoadedFor = null;
                    cachedSettings = settings;
                    clientConfigurationRevision = revision;
                    Logger.Info("Zattoo client configuration activated.");
                    applyGuideDays = settings.GuideDays;
                }

                context = new ClientContext(client, cachedSettings);
            }

            if (applyGuideDays > 0)
            {
                ApplyGuideDays(applyGuideDays);
            }

            if (clientToRetire != null)
            {
                // Stopping and disposing a client drains its enrichment worker, so
                // neither happens while the client lock is held.
                clientToRetire.StopGuideEnrichment();
                retiredClients.Retire(clientToRetire, DateTimeOffset.UtcNow);
            }

            DisposeClients(retiredClients.TakeExpired(DateTimeOffset.UtcNow));
            return context;
        }

        /// <summary>
        /// Writes the guide depth into the Emby Live TV settings. Emby decides the
        /// range it asks the provider for, so a plugin setting can only take
        /// effect by changing that value. Nothing is written while the setting is
        /// left at zero, and nothing is written when the value already matches.
        /// </summary>
        private void ApplyGuideDays(int guideDays)
        {
            try
            {
                var options = GetConfiguration();
                if (options.GuideDays == guideDays)
                {
                    return;
                }

                options.GuideDays = guideDays;
                Config.SaveConfiguration("livetv", options);
                Logger.Info(
                    "Emby guide depth set to {0} day(s) from the Zattoo plugin settings.",
                    guideDays);
            }
            catch (Exception exception)
            {
                Logger.ErrorException(
                    "The Emby guide depth could not be updated from the Zattoo plugin settings.",
                    exception);
            }
        }

        private void DisposeClients(IReadOnlyList<IZattooClient> clientsToDispose)
        {
            foreach (var clientToDispose in clientsToDispose)
            {
                try
                {
                    clientToDispose.Dispose();
                }
                catch (Exception exception)
                {
                    // A retired client must never fail the request that collected it.
                    Logger.ErrorException(
                        "Failed to dispose a retired Zattoo client.",
                        exception);
                }
            }
        }

        private void LogGuideDetailsProgress(ZattooGuideDetailsProgress progress)
        {
            switch (progress.Kind)
            {
                case ZattooGuideDetailsProgressKind.Started:
                    Logger.Info(
                        "Zattoo guide detail enrichment started; {0} pending, {1} cached.",
                        progress.PendingPrograms,
                        progress.CachedPrograms);
                    break;
                case ZattooGuideDetailsProgressKind.Progress:
                    Logger.Info(
                        "Zattoo guide detail enrichment progress; {0} processed, {1} pending, {2} cached, {3} removed.",
                        progress.ProcessedPrograms,
                        progress.PendingPrograms,
                        progress.CachedPrograms,
                        progress.RemovedPrograms);
                    break;
                case ZattooGuideDetailsProgressKind.Retrying:
                    Logger.Warn(
                        "Zattoo guide detail enrichment will retry; {0} failed batch(es), {1} pending.",
                        progress.FailedBatches,
                        progress.PendingPrograms);
                    break;
                case ZattooGuideDetailsProgressKind.Completed:
                    Logger.Info(
                        "Zattoo guide detail enrichment completed; {0} processed, {1} cached, {2} removed.",
                        progress.ProcessedPrograms,
                        progress.CachedPrograms,
                        progress.RemovedPrograms);
                    break;
                case ZattooGuideDetailsProgressKind.Stopped:
                    Logger.Info(
                        "Zattoo guide detail enrichment stopped; {0} pending, {1} cached.",
                        progress.PendingPrograms,
                        progress.CachedPrograms);
                    break;
            }
        }

        private sealed class ClientContext
        {
            public ClientContext(
                IZattooClient client,
                ZattooRuntimeSettings settings)
            {
                Client = client;
                Settings = settings;
            }

            public IZattooClient Client { get; }

            public ZattooRuntimeSettings Settings { get; }
        }
    }
}
