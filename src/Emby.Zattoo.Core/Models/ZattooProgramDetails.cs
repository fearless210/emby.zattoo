using System;
using System.Collections.Generic;

namespace Emby.Zattoo.Models
{
    public sealed class ZattooProgramDetails
    {
        public string Id { get; set; } = string.Empty;

        public string? EpisodeTitle { get; set; }

        public string? Overview { get; set; }

        public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();

        public int? SeasonNumber { get; set; }

        public int? EpisodeNumber { get; set; }
    }
}
