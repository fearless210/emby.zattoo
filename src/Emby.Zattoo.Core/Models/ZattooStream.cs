namespace Emby.Zattoo.Models
{
    /// <summary>An ephemeral live stream option. URLs must never be logged or cached.</summary>
    public sealed class ZattooStream
    {
        public string? Url { get; set; }

        public ZattooStreamFormat Format { get; set; }

        public string Quality { get; set; } = string.Empty;

        public int? Width { get; set; }

        public int? Height { get; set; }

        public int? BitrateKbps { get; set; }

        public bool DrmRequired { get; set; }

        public bool IsAvailable => !string.IsNullOrWhiteSpace(Url);

        public bool IsSupported => IsAvailable && !DrmRequired;
    }
}
