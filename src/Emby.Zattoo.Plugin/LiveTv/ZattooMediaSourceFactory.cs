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
    }
}
