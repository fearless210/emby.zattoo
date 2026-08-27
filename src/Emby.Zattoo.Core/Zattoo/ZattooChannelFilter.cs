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
