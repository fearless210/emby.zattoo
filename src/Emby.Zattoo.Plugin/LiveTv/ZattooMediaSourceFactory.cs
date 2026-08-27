using System;
using System.Collections.Generic;
using Emby.Zattoo.Models;
using Emby.Zattoo.Zattoo;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
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

        /// <summary>
        /// Declares what the remux will actually contain. Without this, Emby has no
        /// source bitrate to constrain a transcode with and falls back to the ceiling
        /// advertised by the client, which can push an encoder past the H.264 level
        /// the device supports. It also stops Emby from assuming broadcast interlacing.
        /// </summary>
        internal static void DescribeStreams(
            MediaSourceInfo source,
            HlsPlaylistSelection selection,
            ZattooStream stream)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (selection == null)
            {
                throw new ArgumentNullException(nameof(selection));
            }

            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            var bitrate = selection.Bandwidth
                ?? (stream.BitrateKbps.HasValue
                    ? stream.BitrateKbps.Value * 1000
                    : (int?)null);
            var streams = new List<MediaStream>
            {
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = selection.VideoCodec ?? "h264",
                    Width = selection.Width ?? stream.Width,
                    Height = selection.Height ?? stream.Height,
                    BitRate = bitrate,

                    // The remux copies the provider rendition, which is progressive.
                    IsInterlaced = false,
                    IsDefault = true,
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = selection.AudioCodec ?? "aac",
                    IsDefault = true,
                },
            };

            source.MediaStreams = streams;
            if (bitrate > 0)
            {
                source.Bitrate = bitrate;
            }
        }

        /// <summary>
        /// Tells whether <see cref="UseLocalLiveStreamEndpoint"/> would accept this
        /// URL. Callers validate it before starting anything a failed switch to the
        /// local endpoint would leave behind.
        /// </summary>
        internal static bool IsSupportedLocalApiUrl(string localApiUrl)
        {
            return Uri.TryCreate(localApiUrl, UriKind.Absolute, out var baseUri)
                && (baseUri.Scheme == Uri.UriSchemeHttp
                    || baseUri.Scheme == Uri.UriSchemeHttps);
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

            if (!IsSupportedLocalApiUrl(localApiUrl))
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
