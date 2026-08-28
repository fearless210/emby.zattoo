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

        /// <summary>Gets or sets the production year, when published.</summary>
        public int? ProductionYear { get; set; }

        /// <summary>Gets or sets the age rating published for the program.</summary>
        public string? AgeRating { get; set; }
    }
}
