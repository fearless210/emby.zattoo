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

        public bool ReplayAvailable { get; set; }

        public int RecordingNumberLimit { get; set; }

        public int MaximumConcurrentStreams { get; set; } = 1;

        public bool ConcurrentStreamLimitIsInferred { get; set; } = true;

        public int PlayableChannelCount { get; set; }

        public int DrmOnlyChannelCount { get; set; }

        public int UnavailableChannelCount { get; set; }

        public int? MaximumPlayableHeight { get; set; }

        /// <summary>
        /// Gets the opaque guide version used in provider paths. This value must not be logged.
        /// </summary>
        public string PowerGuideHash { get; set; } = string.Empty;
    }
}
