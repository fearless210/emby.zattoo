using System;

namespace Emby.Zattoo.Models
{
    /// <summary>Non-secret metadata for the current authenticated session.</summary>
    public sealed class ZattooSessionInfo
    {
        public bool IsActive { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public string CountryCode { get; set; } = string.Empty;

        public string ServiceCountry { get; set; } = string.Empty;

        /// <summary>
        /// Gets the opaque guide version used in provider paths. This value must not be logged.
        /// </summary>
        public string PowerGuideHash { get; set; } = string.Empty;
    }
}
