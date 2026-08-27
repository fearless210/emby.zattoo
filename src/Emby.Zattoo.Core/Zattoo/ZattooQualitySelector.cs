using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Zattoo.Models;

namespace Emby.Zattoo.Zattoo
{
    public static class ZattooQualitySelector
    {
        public static ZattooQuality? SelectBest(
            IEnumerable<ZattooQuality> qualities,
            ZattooPreferredQuality preference)
        {
            if (qualities == null)
            {
                throw new ArgumentNullException(nameof(qualities));
            }

            var candidates = qualities
                .Where(quality => quality.IsAvailable && !quality.DrmRequired)
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            if (preference == ZattooPreferredQuality.Auto)
            {
                return candidates
                    .OrderByDescending(quality => quality.Height ?? -1)
                    .First();
            }

            var maximumHeight = (int)preference;
            var knownMatch = candidates
                .Where(quality => quality.Height.HasValue && quality.Height.Value <= maximumHeight)
                .OrderByDescending(quality => quality.Height)
                .FirstOrDefault();
            if (knownMatch != null)
            {
                return knownMatch;
            }

            // Unknown provider levels cannot be compared safely with a numeric cap.
            // They are used only when no known level can satisfy the preference.
            var unknownLevel = candidates.FirstOrDefault(
                quality => !quality.Height.HasValue);
            if (unknownLevel != null)
            {
                return unknownLevel;
            }

            // Every remaining level is taller than the configured preference. The
            // preference is a ceiling for bandwidth, not a playback requirement:
            // keep the channel playable with its lowest available quality instead
            // of reporting it as unavailable.
            return candidates
                .OrderBy(quality => quality.Height)
                .First();
        }

        internal static int? InferHeight(string level)
        {
            if (string.Equals(level, "uhd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(level, "4k", StringComparison.OrdinalIgnoreCase))
            {
                return 2160;
            }

            if (string.Equals(level, "fhd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(level, "fullhd", StringComparison.OrdinalIgnoreCase))
            {
                return 1080;
            }

            if (string.Equals(level, "hd", StringComparison.OrdinalIgnoreCase))
            {
                return 720;
            }

            if (string.Equals(level, "sd", StringComparison.OrdinalIgnoreCase))
            {
                return 540;
            }

            return null;
        }

        internal static string CreateLabel(string level, int? height)
        {
            if (height.HasValue)
            {
                return height.Value + "p";
            }

            return string.IsNullOrWhiteSpace(level) ? "unknown" : level;
        }
    }
}
