using System.ComponentModel;

namespace Emby.Zattoo.Models
{
    public enum ZattooPreferredQuality
    {
        [Description("Auto")]
        Auto = 0,

        [Description("1080p")]
        P1080 = 1080,

        [Description("720p")]
        P720 = 720,

        [Description("540p")]
        P540 = 540,
    }
}
