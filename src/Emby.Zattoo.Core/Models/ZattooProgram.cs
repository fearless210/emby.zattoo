using System;
using System.Collections.Generic;

namespace Emby.Zattoo.Models
{
    public sealed class ZattooProgram
    {
        public string Id { get; set; } = string.Empty;

        public string ChannelId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? EpisodeTitle { get; set; }

        public string? Overview { get; set; }

        public DateTimeOffset StartDate { get; set; }

        public DateTimeOffset EndDate { get; set; }

        public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();

        public int? SeasonNumber { get; set; }

        public int? EpisodeNumber { get; set; }

        public string? ImageUrl { get; set; }
    }
}
