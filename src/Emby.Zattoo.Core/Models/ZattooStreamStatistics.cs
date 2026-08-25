using System;
using System.Collections.Generic;
using System.Linq;

namespace Emby.Zattoo.Models
{
    /// <summary>Catalogue-level stream availability statistics; no playback URLs are opened.</summary>
    public sealed class ZattooStreamStatistics
    {
        public int TotalChannels { get; set; }

        public int ChannelsWithAvailableStreams { get; set; }

        public int ChannelsWithNonDrmStreams { get; set; }

        public int DrmOnlyChannels { get; set; }

        public int ChannelsWithoutAvailableStreams { get; set; }

        public static ZattooStreamStatistics Calculate(
            IReadOnlyCollection<ZattooChannel> channels)
        {
            if (channels == null)
            {
                throw new ArgumentNullException(nameof(channels));
            }

            var available = channels.Count(
                channel => channel.Qualities.Any(quality => quality.IsAvailable));
            var nonDrm = channels.Count(
                channel => channel.Qualities.Any(
                    quality => quality.IsAvailable && !quality.DrmRequired));
            var drmOnly = channels.Count(
                channel => channel.Qualities.Any(quality => quality.IsAvailable)
                    && channel.Qualities
                        .Where(quality => quality.IsAvailable)
                        .All(quality => quality.DrmRequired));

            return new ZattooStreamStatistics
            {
                TotalChannels = channels.Count,
                ChannelsWithAvailableStreams = available,
                ChannelsWithNonDrmStreams = nonDrm,
                DrmOnlyChannels = drmOnly,
                ChannelsWithoutAvailableStreams = channels.Count - available,
            };
        }
    }
}
