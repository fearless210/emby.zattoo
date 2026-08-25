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

        public IReadOnlyList<ZattooQuality> Qualities { get; set; }
            = Array.Empty<ZattooQuality>();
    }
}
