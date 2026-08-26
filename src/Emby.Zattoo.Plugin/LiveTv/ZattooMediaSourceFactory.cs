using System;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Emby.Zattoo.Plugin.LiveTv
{
    public static class ZattooMediaSourceFactory
    {
        public static MediaSourceInfo Create(string channelId, string channelName)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                throw new ArgumentException(
                    "A tuner channel identifier is required.",
                    nameof(channelId));
            }

            var sourceId = "zattoo:" + channelId + ":mpegts";
            return new MediaSourceInfo
            {
                Id = sourceId,
                Name = string.IsNullOrWhiteSpace(channelName) ? "Zattoo Live TV" : channelName,
                Path = "zattoo://" + Uri.EscapeDataString(channelId),
                Protocol = MediaProtocol.File,
                Container = "mpegts",
                Formats = new[] { "mpegts" },
                IsRemote = false,
                IsInfiniteStream = true,
                RequiresOpening = true,
                RequiresClosing = true,
                SupportsDirectPlay = false,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                Timestamp = TransportStreamTimestamp.Valid,
            };
        }

        internal static void UseLocalLiveStreamEndpoint(
            MediaSourceInfo source,
            string localApiUrl,
            string liveStreamUniqueId)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (!Uri.TryCreate(localApiUrl, UriKind.Absolute, out var baseUri)
                || (baseUri.Scheme != Uri.UriSchemeHttp
                    && baseUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException(
                    "A valid local Emby API URL is required.",
                    nameof(localApiUrl));
            }

            if (string.IsNullOrWhiteSpace(liveStreamUniqueId))
            {
                throw new ArgumentException(
                    "A live stream unique identifier is required.",
                    nameof(liveStreamUniqueId));
            }

            source.Path = localApiUrl.TrimEnd('/')
                + "/LiveTv/LiveStreamFiles/"
                + Uri.EscapeDataString(liveStreamUniqueId)
                + "/stream.ts";
            source.Protocol = MediaProtocol.Http;
            source.IsRemote = false;
            source.RequiresLooping = false;
            source.SupportsDirectPlay = false;
            source.SupportsDirectStream = true;
            source.SupportsTranscoding = true;
        }
    }
}
