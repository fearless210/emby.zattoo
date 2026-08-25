using System;
using Emby.Zattoo.Exceptions;
using Emby.Zattoo.Models;
using Emby.Zattoo.Plugin.Configuration;
using Emby.Zattoo.Zattoo;

namespace Emby.Zattoo.Plugin
{
    internal sealed class ZattooRuntimeSettings
    {
        private static readonly string ClientUserAgent = "Emby.Zattoo.Plugin/"
            + (typeof(ZattooRuntimeSettings).Assembly.GetName().Version?.ToString(3) ?? "unknown");

        private ZattooRuntimeSettings(
            ZattooClientOptions clientOptions,
            ZattooPreferredQuality preferredQuality,
            string ffmpegPath)
        {
            ClientOptions = clientOptions;
            PreferredQuality = preferredQuality;
            FfmpegPath = ffmpegPath;
        }

        public ZattooClientOptions ClientOptions { get; }

        public ZattooPreferredQuality PreferredQuality { get; }

        public string FfmpegPath { get; }

        public static ZattooRuntimeSettings FromConfiguration(
            ZattooPluginOptions configuration,
            string decryptedPassword)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (string.IsNullOrWhiteSpace(configuration.Username)
                || string.IsNullOrEmpty(decryptedPassword))
            {
                throw new ZattooAuthenticationException(
                    "Configure the Zattoo account in the Emby plugin settings.");
            }

            var options = new ZattooClientOptions
            {
                Username = configuration.Username.Trim(),
                Password = decryptedPassword,
                UserAgent = ClientUserAgent,
            };

            if (!Uri.TryCreate(
                    configuration.ProviderUrl,
                    UriKind.Absolute,
                    out var providerUri)
                || providerUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ZattooAuthenticationException(
                    "The configured Zattoo provider URL must be absolute HTTPS.");
            }

            options.ProviderBaseUri = providerUri;
            if (!string.IsNullOrWhiteSpace(configuration.ApplicationVersion))
            {
                options.ApplicationVersion = configuration.ApplicationVersion.Trim();
            }

            return new ZattooRuntimeSettings(
                options,
                configuration.PreferredQuality,
                string.IsNullOrWhiteSpace(configuration.FfmpegPath)
                    ? "ffmpeg"
                    : configuration.FfmpegPath.Trim());
        }
    }
}
