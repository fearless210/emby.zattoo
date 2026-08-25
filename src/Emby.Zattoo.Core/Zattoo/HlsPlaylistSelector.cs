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
                isMasterPlaylist: true);
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
                Height = height,
                Bandwidth = bandwidth,
                AudioGroupId = ReadAttribute(attributes, "AUDIO"),
            };
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

            public int? Height { get; set; }

            public int? Bandwidth { get; set; }

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
    }
}
