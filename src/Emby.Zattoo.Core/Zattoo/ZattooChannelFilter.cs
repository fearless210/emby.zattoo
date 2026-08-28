using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Zattoo.Models;

namespace Emby.Zattoo.Zattoo
{
    public static class ZattooChannelFilter
    {
        public static IReadOnlyList<ZattooChannel> Apply(
            IEnumerable<ZattooChannel> channels,
            ZattooChannelImportMode mode)
        {
            if (channels == null)
            {
                throw new ArgumentNullException(nameof(channels));
            }

            if (!Enum.IsDefined(typeof(ZattooChannelImportMode), mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            return channels.Where(channel => ShouldImport(channel, mode)).ToArray();
        }

        /// <summary>
        /// Keeps only the channels of the named groups. An empty selection keeps
        /// everything, and a name the catalogue does not publish simply matches
        /// nothing rather than failing the import.
        /// </summary>
        public static IReadOnlyList<ZattooChannel> ApplyGroups(
            IEnumerable<ZattooChannel> channels,
            IReadOnlyCollection<string> groupNames)
        {
            if (channels == null)
            {
                throw new ArgumentNullException(nameof(channels));
            }

            if (groupNames == null)
            {
                throw new ArgumentNullException(nameof(groupNames));
            }

            var selected = new HashSet<string>(
                groupNames.Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim()),
                StringComparer.OrdinalIgnoreCase);
            if (selected.Count == 0)
            {
                return channels.ToArray();
            }

            return channels
                .Where(channel => selected.Contains(channel.GroupName))
                .ToArray();
        }

        /// <summary>Lists the groups the catalogue publishes, in catalogue order.</summary>
        public static IReadOnlyList<string> ListGroups(
            IEnumerable<ZattooChannel> channels)
        {
            if (channels == null)
            {
                throw new ArgumentNullException(nameof(channels));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (var channel in channels)
            {
                if (!string.IsNullOrWhiteSpace(channel.GroupName)
                    && seen.Add(channel.GroupName))
                {
                    names.Add(channel.GroupName);
                }
            }

            return names;
        }

        public static bool IsPlayable(ZattooChannel channel)
        {
            if (channel == null)
            {
                throw new ArgumentNullException(nameof(channel));
            }

            return channel.Qualities.Any(
                quality => quality.IsAvailable && !quality.DrmRequired);
        }

        private static bool ShouldImport(
            ZattooChannel channel,
            ZattooChannelImportMode mode)
        {
            switch (mode)
            {
                case ZattooChannelImportMode.PlayableOnly:
                    return IsPlayable(channel);
                case ZattooChannelImportMode.ExcludeDrmOnly:
                    var available = channel.Qualities
                        .Where(quality => quality.IsAvailable)
                        .ToArray();
                    return available.Length == 0
                        || available.Any(quality => !quality.DrmRequired);
                case ZattooChannelImportMode.AllChannels:
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }
    }
}
