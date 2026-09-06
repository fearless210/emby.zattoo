using System;
using System.Collections.Generic;
using System.Globalization;
using Emby.Zattoo.Exceptions;

namespace Emby.Zattoo.Zattoo
{
    /// <summary>Reads what a signed media playlist currently advertises.</summary>
    /// <remarks>
    /// FFmpeg refreshes this playlist by itself while it plays. The plugin reads
    /// it only to observe a stream from the outside: which segments the signed
    /// URL still hands out, and whether the live window is still advancing. A
    /// master playlist is rejected rather than parsed, so that an observation
    /// never silently measures the wrong document.
    /// </remarks>
    public static class HlsMediaPlaylistReader
    {
        public static HlsMediaPlaylist Read(string content, Uri playlistUri)
        {
            if (playlistUri == null)
            {
                throw new ArgumentNullException(nameof(playlistUri));
            }

            if (string.IsNullOrWhiteSpace(content)
                || !content.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                throw new ZattooProtocolException("The HLS playlist is invalid.");
            }

            EnsureSecureUri(playlistUri);

            long? mediaSequence = null;
            double? targetDuration = null;
            Uri? initializationUri = null;
            var isEndList = false;
            var segments = new List<Uri>();

            foreach (var rawLine in content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line[0] != '#')
                {
                    segments.Add(ResolveSecureUri(playlistUri, line));
                    continue;
                }

                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    throw new ZattooProtocolException(
                        "A master playlist was given where a media playlist was expected.");
                }

                const string sequencePrefix = "#EXT-X-MEDIA-SEQUENCE:";
                if (line.StartsWith(sequencePrefix, StringComparison.Ordinal))
                {
                    if (long.TryParse(
                            line.Substring(sequencePrefix.Length).Trim(),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var parsedSequence))
                    {
                        mediaSequence = parsedSequence;
                    }

                    continue;
                }

                const string durationPrefix = "#EXT-X-TARGETDURATION:";
                if (line.StartsWith(durationPrefix, StringComparison.Ordinal))
                {
                    if (double.TryParse(
                            line.Substring(durationPrefix.Length).Trim(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var parsedDuration))
                    {
                        targetDuration = parsedDuration;
                    }

                    continue;
                }

                const string mapPrefix = "#EXT-X-MAP:";
                if (line.StartsWith(mapPrefix, StringComparison.Ordinal))
                {
                    var mapUri = ReadUriAttribute(line.Substring(mapPrefix.Length));
                    if (mapUri.Length > 0)
                    {
                        initializationUri = ResolveSecureUri(playlistUri, mapUri);
                    }

                    continue;
                }

                if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
                {
                    isEndList = true;
                }
            }

            return new HlsMediaPlaylist(
                segments,
                mediaSequence,
                targetDuration,
                initializationUri,
                isEndList);
        }

        /// <summary>
        /// Reads the URI attribute of a tag. Only this one attribute is needed, so
        /// the value is taken between the first pair of quotes rather than by
        /// parsing the whole attribute list.
        /// </summary>
        private static string ReadUriAttribute(string attributes)
        {
            const string name = "URI=\"";
            var start = attributes.IndexOf(name, StringComparison.Ordinal);
            if (start < 0)
            {
                return string.Empty;
            }

            start += name.Length;
            var end = attributes.IndexOf('"', start);
            return end < 0 ? string.Empty : attributes.Substring(start, end - start);
        }

        private static Uri ResolveSecureUri(Uri playlistUri, string value)
        {
            if (!Uri.TryCreate(playlistUri, value, out var result))
            {
                throw new ZattooProtocolException(
                    "The HLS media playlist contains an invalid URI.");
            }

            EnsureSecureUri(result);
            return result;
        }

        private static void EnsureSecureUri(Uri uri)
        {
            if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ZattooProtocolException(
                    "The HLS media playlist contains an invalid or insecure URI.");
            }
        }
    }

    /// <summary>One reading of a media playlist.</summary>
    public sealed class HlsMediaPlaylist
    {
        public HlsMediaPlaylist(
            IReadOnlyList<Uri> segmentUris,
            long? mediaSequence,
            double? targetDurationSeconds,
            Uri? initializationUri,
            bool isEndList)
        {
            SegmentUris = segmentUris ?? throw new ArgumentNullException(nameof(segmentUris));
            MediaSequence = mediaSequence;
            TargetDurationSeconds = targetDurationSeconds;
            InitializationUri = initializationUri;
            IsEndList = isEndList;
        }

        /// <summary>Gets the segments the playlist advertises, in playlist order.</summary>
        public IReadOnlyList<Uri> SegmentUris { get; }

        /// <summary>
        /// Gets the sequence number of the first segment. It is the only reliable
        /// way to tell a live window that advances from one that has stalled.
        /// </summary>
        public long? MediaSequence { get; }

        public double? TargetDurationSeconds { get; }

        /// <summary>Gets the initialization segment of a fragmented MP4 rendition.</summary>
        public Uri? InitializationUri { get; }

        /// <summary>Gets a value indicating whether the playlist is complete.</summary>
        public bool IsEndList { get; }

        /// <summary>Gets the most recent segment, which is the one a live player requests next.</summary>
        public Uri? LastSegmentUri =>
            SegmentUris.Count == 0 ? null : SegmentUris[SegmentUris.Count - 1];
    }
}
