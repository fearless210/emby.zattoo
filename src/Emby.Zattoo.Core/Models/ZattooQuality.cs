namespace Emby.Zattoo.Models
{
    /// <summary>A quality advertised in the Zattoo channel catalogue.</summary>
    public sealed class ZattooQuality
    {
        public string Level { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public int? Width { get; set; }

        public int? Height { get; set; }

        public int? BitrateKbps { get; set; }

        public bool IsAvailable { get; set; }

        public bool DrmRequired { get; set; }
    }
}
