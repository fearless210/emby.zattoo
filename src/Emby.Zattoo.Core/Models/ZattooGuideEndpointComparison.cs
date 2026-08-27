using System;

namespace Emby.Zattoo.Models
{
    public sealed class ZattooGuideEndpointComparison
    {
        public DateTimeOffset StartDate { get; set; }

        public DateTimeOffset EndDate { get; set; }

        public ZattooGuideEndpointMetrics Version2 { get; set; }
            = new ZattooGuideEndpointMetrics();

        public ZattooGuideEndpointMetrics Version3 { get; set; }
            = new ZattooGuideEndpointMetrics();

        public int SharedPrograms { get; set; }

        public int Version2OnlyPrograms { get; set; }

        public int Version3OnlyPrograms { get; set; }

        public int SharedDescriptionsOnlyInVersion2 { get; set; }

        public int SharedDescriptionsOnlyInVersion3 { get; set; }
    }

    public sealed class ZattooGuideEndpointMetrics
    {
        public int ResponseBytes { get; set; }

        public TimeSpan Elapsed { get; set; }

        public int ChannelsWithPrograms { get; set; }

        public int Programs { get; set; }

        public int ProgramsWithDescription { get; set; }

        public int ProgramsWithEpisodeTitle { get; set; }

        public int ProgramsWithGenres { get; set; }

        public int ProgramsWithSeasonOrEpisodeNumber { get; set; }

        public int ProgramsWithImage { get; set; }
    }
}
