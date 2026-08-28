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

        /// <summary>
        /// Gets or sets the provider identifier of the content itself, stable
        /// across airings and channels, as opposed to <see cref="Id"/> which
        /// identifies one broadcast.
        /// </summary>
        public string? ContentId { get; set; }

        /// <summary>Gets or sets whether the provider marks this as a series.</summary>
        public bool IsSeries { get; set; }

        /// <summary>Gets or sets the age rating published for the program.</summary>
        public string? AgeRating { get; set; }

        /// <summary>
        /// Gets or sets the provider category identifiers. They are numeric and
        /// therefore stable, unlike the category names which are localised.
        /// </summary>
        public IReadOnlyList<int> CategoryIds { get; set; } = Array.Empty<int>();

        /// <summary>Gets or sets the production year, when published.</summary>
        public int? ProductionYear { get; set; }

        public int? SeasonNumber { get; set; }

        public int? EpisodeNumber { get; set; }

        public string? ImageUrl { get; set; }
    }
}
