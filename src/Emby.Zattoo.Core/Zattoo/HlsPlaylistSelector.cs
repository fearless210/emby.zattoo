using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Emby.Zattoo.Exceptions;

namespace Emby.Zattoo.Zattoo
{
    /// <summary>Selects one video rendition and its default audio rendition from an HLS master.</summary>
    public static class HlsPlaylistSelector
    {
        public static HlsPlaylistSelection Select(
            string content,
            Uri playlistUri,
            int? maximumHeight = null)
        {
            if (string.IsNullOrWhiteSpace(content)
                || !content.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                throw new ZattooProtocolException("The HLS playlist is invalid.");
            }

            EnsureSecureUri(playlistUri);
            var variants = new List<HlsVariant>();
            var audioRenditions = new List<HlsAudioRendition>();
            Dictionary<string, string>? pendingVariant = null;

            foreach (var rawLine in content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (pendingVariant != null && line[0] != '#')
                {
                    variants.Add(CreateVariant(pendingVariant, playlistUri, line));
                    pendingVariant = null;
                    continue;
                }

                const string variantPrefix = "#EXT-X-STREAM-INF:";
                if (line.StartsWith(variantPrefix, StringComparison.Ordinal))
                {
                    if (pendingVariant != null)
                    {
                        throw new ZattooProtocolException(
                            "The HLS master contains a variant without a playlist URI.");
                    }

                    pendingVariant = ParseAttributes(line.Substring(variantPrefix.Length));
                    continue;
                }

                const string mediaPrefix = "#EXT-X-MEDIA:";
                if (line.StartsWith(mediaPrefix, StringComparison.Ordinal))
                {
                    var attributes = ParseAttributes(line.Substring(mediaPrefix.Length));
                    if (string.Equals(
                            ReadAttribute(attributes, "TYPE"),
                            "AUDIO",
                            StringComparison.OrdinalIgnoreCase)
                        && attributes.TryGetValue("URI", out var audioUri)
                        && !string.IsNullOrWhiteSpace(audioUri))
                    {
                        audioRenditions.Add(new HlsAudioRendition
                        {
                            GroupId = ReadAttribute(attributes, "GROUP-ID"),
                            Uri = ResolveSecureUri(playlistUri, audioUri),
                            IsDefault = string.Equals(
                                ReadAttribute(attributes, "DEFAULT"),
                                "YES",
                                StringComparison.OrdinalIgnoreCase),
                            IsAutoSelect = string.Equals(
                                ReadAttribute(attributes, "AUTOSELECT"),
                                "YES",
                                StringComparison.OrdinalIgnoreCase),
                        });
                    }
                }
            }

            if (pendingVariant != null)
            {
                throw new ZattooProtocolException(
                    "The HLS master contains a variant without a playlist URI.");
            }

            if (variants.Count == 0)
            {
                return new HlsPlaylistSelection(playlistUri, null, isMasterPlaylist: false);
            }

            var selectedVariant = SelectVariant(variants, maximumHeight);
            var audio = audioRenditions
                .Where(item => string.Equals(
                    item.GroupId,
                    selectedVariant.AudioGroupId,
                    StringComparison.Ordinal))
                .OrderByDescending(item => item.IsDefault)
                .ThenByDescending(item => item.IsAutoSelect)
                .FirstOrDefault();

            return new HlsPlaylistSelection(
                selectedVariant.Uri,
                audio?.Uri,
                isMasterPlaylist: true)
            {
                Width = selectedVariant.Width,
                Height = selectedVariant.Height,
                Bandwidth = selectedVariant.Bandwidth,
                VideoCodec = ReadVideoCodec(selectedVariant.Codecs),
                AudioCodec = ReadAudioCodec(selectedVariant.Codecs),
            };
        }

        private static HlsVariant SelectVariant(
            IReadOnlyList<HlsVariant> variants,
            int? maximumHeight)
        {
            var candidates = maximumHeight.HasValue
                ? variants.Where(item => item.Height.HasValue
                        && item.Height.Value <= maximumHeight.Value)
                    .ToArray()
                : variants.ToArray();

            if (candidates.Length == 0)
            {
                candidates = variants.ToArray();
            }

            return candidates
                .OrderByDescending(item => item.Height ?? 0)
                .ThenByDescending(item => item.Bandwidth ?? 0)
                .First();
        }

        private static HlsVariant CreateVariant(
            IReadOnlyDictionary<string, string> attributes,
            Uri playlistUri,
            string relativeUri)
        {
            int? width = null;
            int? height = null;
            var resolution = ReadAttribute(attributes, "RESOLUTION");
            var separator = resolution.IndexOfAny(new[] { 'x', 'X' });
            if (separator >= 0
                && int.TryParse(
                    resolution.Substring(separator + 1),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedHeight))
            {
                height = parsedHeight;
            }

            if (separator > 0
                && int.TryParse(
                    resolution.Substring(0, separator),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedWidth))
            {
                width = parsedWidth;
            }

            int? bandwidth = null;
            if (int.TryParse(
                ReadAttribute(attributes, "BANDWIDTH"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedBandwidth))
            {
                bandwidth = parsedBandwidth;
            }

            return new HlsVariant
            {
                Uri = ResolveSecureUri(playlistUri, relativeUri),
                Width = width,
                Height = height,
                Bandwidth = bandwidth,
                Codecs = ReadAttribute(attributes, "CODECS"),
                AudioGroupId = ReadAttribute(attributes, "AUDIO"),
            };
        }

        /// <summary>
        /// Maps the RFC 6381 identifiers of a CODECS attribute to the names Emby
        /// uses. Unknown identifiers return null: the plugin must not claim a
        /// codec it did not recognise.
        /// </summary>
        internal static string? ReadVideoCodec(string codecs)
        {
            foreach (var codec in SplitCodecs(codecs))
            {
                if (codec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase)
                    || codec.StartsWith("avc3", StringComparison.OrdinalIgnoreCase))
                {
                    return "h264";
                }

                if (codec.StartsWith("hvc1", StringComparison.OrdinalIgnoreCase)
                    || codec.StartsWith("hev1", StringComparison.OrdinalIgnoreCase))
                {
                    return "hevc";
                }
            }

            return null;
        }

        internal static string? ReadAudioCodec(string codecs)
        {
            foreach (var codec in SplitCodecs(codecs))
            {
                if (codec.StartsWith("mp4a.40", StringComparison.OrdinalIgnoreCase))
                {
                    return "aac";
                }

                if (codec.StartsWith("ac-3", StringComparison.OrdinalIgnoreCase))
                {
                    return "ac3";
                }

                if (codec.StartsWith("ec-3", StringComparison.OrdinalIgnoreCase))
                {
                    return "eac3";
                }

                if (codec.StartsWith("mp3", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(codec, "mp4a.69", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(codec, "mp4a.6B", StringComparison.OrdinalIgnoreCase))
                {
                    return "mp3";
                }
            }

            return null;
        }

        private static IEnumerable<string> SplitCodecs(string codecs)
        {
            if (string.IsNullOrWhiteSpace(codecs))
            {
                yield break;
            }

            foreach (var codec in codecs.Split(','))
            {
                var normalized = codec.Trim();
                if (normalized.Length > 0)
                {
                    yield return normalized;
                }
            }
        }

        private static Dictionary<string, string> ParseAttributes(string value)
        {
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            var offset = 0;
            while (offset < value.Length)
            {
                while (offset < value.Length && (value[offset] == ',' || char.IsWhiteSpace(value[offset])))
                {
                    offset++;
                }

                var equals = value.IndexOf('=', offset);
                if (equals < 0)
                {
                    break;
                }

                var key = value.Substring(offset, equals - offset).Trim();
                offset = equals + 1;
                string attributeValue;
                if (offset < value.Length && value[offset] == '"')
                {
                    offset++;
                    var closingQuote = value.IndexOf('"', offset);
                    if (closingQuote < 0)
                    {
                        throw new ZattooProtocolException(
                            "The HLS master contains an invalid quoted attribute.");
                    }

                    attributeValue = value.Substring(offset, closingQuote - offset);
                    offset = closingQuote + 1;
                }
                else
                {
                    var comma = value.IndexOf(',', offset);
                    if (comma < 0)
                    {
                        attributeValue = value.Substring(offset).Trim();
                        offset = value.Length;
                    }
                    else
                    {
                        attributeValue = value.Substring(offset, comma - offset).Trim();
                        offset = comma + 1;
                    }
                }

                if (key.Length > 0)
                {
                    attributes[key] = attributeValue;
                }
            }

            return attributes;
        }

        private static string ReadAttribute(
            IReadOnlyDictionary<string, string> attributes,
            string name)
        {
            return attributes.TryGetValue(name, out var value) ? value : string.Empty;
        }

        private static Uri ResolveSecureUri(Uri playlistUri, string value)
        {
            if (!Uri.TryCreate(playlistUri, value, out var result))
            {
                throw new ZattooProtocolException(
                    "The HLS master contains an invalid playlist URI.");
            }

            EnsureSecureUri(result);
            return result;
        }

        private static void EnsureSecureUri(Uri uri)
        {
            if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ZattooProtocolException(
                    "The HLS playlist contains an invalid or insecure URI.");
            }
        }

        private sealed class HlsVariant
        {
            public Uri Uri { get; set; } = null!;

            public int? Width { get; set; }

            public int? Height { get; set; }

            public int? Bandwidth { get; set; }

            public string Codecs { get; set; } = string.Empty;

            public string AudioGroupId { get; set; } = string.Empty;
        }

        private sealed class HlsAudioRendition
        {
            public Uri Uri { get; set; } = null!;

            public string GroupId { get; set; } = string.Empty;

            public bool IsDefault { get; set; }

            public bool IsAutoSelect { get; set; }
        }
    }

    public sealed class HlsPlaylistSelection
    {
        public HlsPlaylistSelection(Uri videoUri, Uri? audioUri, bool isMasterPlaylist)
        {
            VideoUri = videoUri;
            AudioUri = audioUri;
            IsMasterPlaylist = isMasterPlaylist;
        }

        public Uri VideoUri { get; }

        public Uri? AudioUri { get; }

        public bool IsMasterPlaylist { get; }

        /// <summary>Gets the width advertised for the selected rendition.</summary>
        public int? Width { get; set; }

        /// <summary>Gets the height advertised for the selected rendition.</summary>
        public int? Height { get; set; }

        /// <summary>Gets the peak bandwidth in bits per second, when advertised.</summary>
        public int? Bandwidth { get; set; }

        /// <summary>Gets the video codec name, or null when it was not advertised.</summary>
        public string? VideoCodec { get; set; }

        /// <summary>Gets the audio codec name, or null when it was not advertised.</summary>
        public string? AudioCodec { get; set; }
    }
}
