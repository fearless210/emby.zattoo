using System.ComponentModel;

namespace Emby.Zattoo.Models
{
    /// <summary>Controls which catalogue channels are imported into Emby.</summary>
    public enum ZattooChannelImportMode
    {
        [Description("Playable channels only (recommended)")]
        PlayableOnly = 0,

        [Description("Exclude DRM-only channels")]
        ExcludeDrmOnly = 1,

        [Description("All catalogue channels (diagnostic)")]
        AllChannels = 2,
    }
}
