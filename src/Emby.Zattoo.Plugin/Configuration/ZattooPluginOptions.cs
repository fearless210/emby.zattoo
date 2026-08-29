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

        [DisplayName("Channel import mode")]
        [Description(
            "Playable channels only imports channels with at least one available "
            + "non-DRM quality and limits guide processing to those channels.")]
        public ZattooChannelImportMode ChannelImportMode { get; set; } =
            ZattooChannelImportMode.PlayableOnly;

        [DisplayName("Enrich guide descriptions")]
        [Description(
            "Loads detailed descriptions and genres progressively in the background. "
            + "The native Emby guide refresh does not wait for this process.")]
        public bool EnableGuideDetails { get; set; } = true;

        [DisplayName("Channel groups")]
        [Description(
            "Comma separated list of the provider groups to import. Leave empty "
            + "to import every group. The groups your account publishes are "
            + "listed in the server log after each channel refresh.")]
        public string ChannelGroups { get; set; } = string.Empty;

        [DisplayName("Guide days")]
        [Description(
            "Number of days of guide data Emby downloads, from 1 to 14. Leave 0 "
            + "to keep whatever is configured in the Emby Live TV settings, where "
            + "Auto means seven days.")]
        public int GuideDays { get; set; }

        [DisplayName("FFmpeg executable")]
        [Description(
            "Leave empty to use the FFmpeg that Emby itself runs, which is the "
            + "recommended setting. Set an absolute path only to override it.")]
        [EditFilePicker]
        public string FfmpegPath { get; set; } = string.Empty;

        [DisplayName("Provider URL")]
        [Description("Keep the default for Zattoo. Resellers must use an absolute HTTPS URL.")]
        public string ProviderUrl { get; set; } = "https://zattoo.com/";

        [DisplayName("Zattoo web application version")]
        [Description("Advanced diagnostic setting. Keep the default unless the provider changes it.")]
        public string ApplicationVersion { get; set; } = "3.2120.1";

        /// <summary>
        /// Copies every setting, replacing only the password with what may be
        /// shown. The copy is made by reflection on purpose: listing the
        /// properties by hand meant a setting added later was silently dropped
        /// when the page opened, and erased on the next save.
        /// </summary>
        internal ZattooPluginOptions CopyForDisplay(string displayPassword)
        {
            var copy = new ZattooPluginOptions();
            foreach (var property in typeof(ZattooPluginOptions).GetProperties())
            {
                if (property.CanRead && property.CanWrite)
                {
                    property.SetValue(copy, property.GetValue(this));
                }
            }

            copy.Password = displayPassword;
            return copy;
        }

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

            if (GuideDays < 0 || GuideDays > 14)
            {
                context.AddValidationError(
                    nameof(GuideDays),
                    "Choose between 1 and 14 days, or 0 to leave the Emby setting alone.");
            }

            if (!Enum.IsDefined(typeof(ZattooPreferredQuality), PreferredQuality))
            {
                context.AddValidationError(
                    nameof(PreferredQuality),
                    "Select Auto, 1080p, 720p or 540p.");
            }

            if (!Enum.IsDefined(typeof(ZattooChannelImportMode), ChannelImportMode))
            {
                context.AddValidationError(
                    nameof(ChannelImportMode),
                    "Select playable only, exclude DRM-only or all channels.");
            }
        }
    }
}
