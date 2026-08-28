using System;
using System.Collections.Generic;

namespace Emby.Zattoo.Models
{
    /// <summary>A channel returned by the Zattoo power guide.</summary>
    public sealed class ZattooChannel
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public int Number { get; set; }

        public string? LogoUrl { get; set; }

        public bool IsFavorite { get; set; }

        /// <summary>
        /// Gets or sets the provider group this channel belongs to, empty when the
        /// catalogue publishes none.
        /// </summary>
        public string GroupName { get; set; } = string.Empty;

        /// <summary>Gets or sets whether the provider publishes a radio channel.</summary>
        public bool IsRadio { get; set; }

        public IReadOnlyList<ZattooQuality> Qualities { get; set; }
            = Array.Empty<ZattooQuality>();
    }
}
