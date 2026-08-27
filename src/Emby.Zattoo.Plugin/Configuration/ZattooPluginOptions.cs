using System;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Validation;
using Emby.Zattoo.Models;
using MediaBrowser.Model.Attributes;

namespace Emby.Zattoo.Plugin.Configuration
{
    /// <summary>Options rendered by Emby's native simple plugin UI.</summary>
    public sealed class ZattooPluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Zattoo Live TV";

        public override string EditorDescription =>
            "Configure the server-side Zattoo account and HLS-to-MPEG-TS remux. "
            + "Only non-DRM streams are supported.";

        [DisplayName("Zattoo username")]
        [Description("Username or email address used to sign in to Zattoo.")]
        public string Username { get; set; } = string.Empty;

        [DisplayName("Zattoo password")]
        [Description("Stored encrypted by Emby Server and never returned to the browser.")]
        [IsPassword]
        public string Password { get; set; } = string.Empty;

        [DisplayName("Preferred quality")]
        [Description("Auto selects the highest available non-DRM quality.")]
        public ZattooPreferredQuality PreferredQuality { get; set; } =
            ZattooPreferredQuality.Auto;

        [DisplayName("Enrich guide descriptions")]
        [Description(
            "Loads detailed descriptions and genres progressively in the background. "
            + "The native Emby guide refresh does not wait for this process.")]
        public bool EnableGuideDetails { get; set; } = true;

        [DisplayName("FFmpeg executable")]
        [Description("Absolute Linux path to ffmpeg, or 'ffmpeg' when it is available in Emby's PATH.")]
        [EditFilePicker]
        public string FfmpegPath { get; set; } = "ffmpeg";

        [DisplayName("Provider URL")]
        [Description("Keep the default for Zattoo. Resellers must use an absolute HTTPS URL.")]
        public string ProviderUrl { get; set; } = "https://zattoo.com/";

        [DisplayName("Zattoo web application version")]
        [Description("Advanced diagnostic setting. Keep the default unless the provider changes it.")]
        public string ApplicationVersion { get; set; } = "3.2120.1";

        protected override void Validate(ValidationContext context)
        {
            if (string.IsNullOrWhiteSpace(Username))
            {
                context.AddValidationError(nameof(Username), "A Zattoo username is required.");
            }

            if (string.IsNullOrEmpty(Password))
            {
                context.AddValidationError(nameof(Password), "A Zattoo password is required.");
            }

            if (!Uri.TryCreate(ProviderUrl, UriKind.Absolute, out var providerUri)
                || providerUri.Scheme != Uri.UriSchemeHttps)
            {
                context.AddValidationError(
                    nameof(ProviderUrl),
                    "The provider URL must be an absolute HTTPS URL.");
            }

            if (string.IsNullOrWhiteSpace(ApplicationVersion))
            {
                context.AddValidationError(
                    nameof(ApplicationVersion),
                    "The Zattoo web application version is required.");
            }

            if (!Enum.IsDefined(typeof(ZattooPreferredQuality), PreferredQuality))
            {
                context.AddValidationError(
                    nameof(PreferredQuality),
                    "Select Auto, 1080p, 720p or 540p.");
            }
        }
    }
}
