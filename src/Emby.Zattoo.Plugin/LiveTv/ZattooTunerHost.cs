using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
            return false;
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
                ImportGuideData = false,
                AllowHWTranscoding = false,
            };
        }

        protected override async Task<List<ChannelInfo>> GetChannelsInternal(
            TunerHostInfo tuner,
            CancellationToken cancellationToken)
        {
            var context = RequireClient();
            var channels = await context.Client.GetChannelsAsync(cancellationToken)
                .ConfigureAwait(false);
            var selected = tuner.ImportFavoritesOnly
                ? channels.Where(channel => channel.IsFavorite)
                : channels;
            var channelIdPrefix = GetChannelIdPrefix(tuner);
            var result = selected
                .Select(channel => ZattooChannelMapper.Map(
                    channel,
                    channelIdPrefix))
                .ToList();
            Logger.Info("Loaded {0} Zattoo channels.", result.Count);
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
            ILiveStream stream = new ZattooLiveStream(
                tuner.Id,
                tunerChannel.TunerChannelId ?? tunerChannel.Id,
                tunerChannel.Name,
                context.Client,
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
                    var replacement = new ZattooClient(settings.ClientOptions);
                    if (client != null)
                    {
                        retiredClients.Add(client);
                    }

                    client = replacement;
                    clientConfigurationRevision = revision;
                    Logger.Info("Zattoo client configuration activated.");
                }

                return new ClientContext(client, settings);
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
