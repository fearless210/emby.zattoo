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
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

namespace Emby.Zattoo.Plugin.LiveTv
{
    /// <summary>Live TV tuner backed by one Zattoo account.</summary>
    public sealed class ZattooTunerHost : BaseTunerHost, IDisposable
    {
        private readonly object clientSync = new object();
        private readonly List<IZattooClient> retiredClients =
            new List<IZattooClient>();
        private readonly ZattooStreamCapacity streamCapacity =
            new ZattooStreamCapacity();
        private IZattooClient? client;
        private long clientConfigurationRevision = long.MinValue;
        private bool disposed;

        public ZattooTunerHost(IServerApplicationHost applicationHost)
            : base(applicationHost)
        {
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
            tuner.TunerCount = concurrentStreams;
            var channelIdPrefix = GetChannelIdPrefix(tuner);
            var result = selected
                .Select(channel => ZattooChannelMapper.Map(
                    channel,
                    channelIdPrefix))
                .ToList();
            Logger.Info(
                "Loaded {0} of {1} Zattoo channels using {2} import mode.",
                result.Count,
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

            return result;
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

        protected override Task<ILiveStream> GetChannelStream(
            TunerHostInfo tuner,
            BaseItem dbChannnel,
            ChannelInfo tunerChannel,
            string mediaSourceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = RequireClient();
            var channelId = tunerChannel.TunerChannelId ?? tunerChannel.Id;
            context.Client.PrioritizeGuideDetails(channelId);
            ILiveStream stream = new ZattooLiveStream(
                tuner.Id,
                channelId,
                tunerChannel.Name,
                context.Client,
                streamCapacity,
                context.Settings.PreferredQuality,
                context.Settings.FfmpegPath,
                AppHost.GetLocalApiUrl("127.0.0.1"),
                Logger);
            return Task.FromResult(stream);
        }

        public void Dispose()
        {
            List<IZattooClient> clientsToDispose;
            lock (clientSync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                clientsToDispose = new List<IZattooClient>(retiredClients);
                if (client != null)
                {
                    clientsToDispose.Add(client);
                    client = null;
                }

                retiredClients.Clear();
            }

            foreach (var clientToDispose in clientsToDispose)
            {
                clientToDispose.Dispose();
            }
        }

        private ClientContext RequireClient()
        {
            lock (clientSync)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(ZattooTunerHost));
                }

                var plugin = Plugin.Instance
                    ?? throw new InvalidOperationException(
                        "The Zattoo plugin has not finished loading.");
                var settings = plugin.GetRuntimeSettings(out var revision);
                if (client == null || clientConfigurationRevision != revision)
                {
                    settings.ClientOptions.GuideDetailsProgress =
                        LogGuideDetailsProgress;
                    var replacement = new ZattooClient(settings.ClientOptions);
                    streamCapacity.UpdateLimit(1);
                    if (client != null)
                    {
                        client.StopGuideEnrichment();
                        retiredClients.Add(client);
                    }

                    client = replacement;
                    clientConfigurationRevision = revision;
                    Logger.Info("Zattoo client configuration activated.");
                }

                return new ClientContext(client, settings);
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
