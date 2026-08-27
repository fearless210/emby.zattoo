using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
            ZattooChannelImportMode channelImportMode,
            string ffmpegPath)
        {
            ClientOptions = clientOptions;
            PreferredQuality = preferredQuality;
            ChannelImportMode = channelImportMode;
            FfmpegPath = ffmpegPath;
        }

        public ZattooClientOptions ClientOptions { get; }

        public ZattooPreferredQuality PreferredQuality { get; }

        public ZattooChannelImportMode ChannelImportMode { get; }

        public string FfmpegPath { get; }

        public static ZattooRuntimeSettings FromConfiguration(
            ZattooPluginOptions configuration,
            string decryptedPassword,
            string? pluginDataFolder = null)
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
                EnableBackgroundGuideDetails = configuration.EnableGuideDetails,
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

            if (options.EnableBackgroundGuideDetails
                && !string.IsNullOrWhiteSpace(pluginDataFolder))
            {
                options.GuideDetailsCachePath = Path.Combine(
                    pluginDataFolder,
                    "guide-details-cache-v1.jsonl");
                options.GuideDetailsCacheScope = CreateCacheScope(
                    options.ProviderBaseUri,
                    options.Username,
                    options.Language);
            }

            return new ZattooRuntimeSettings(
                options,
                configuration.PreferredQuality,
                configuration.ChannelImportMode,
                string.IsNullOrWhiteSpace(configuration.FfmpegPath)
                    ? "ffmpeg"
                    : configuration.FfmpegPath.Trim());
        }

        private static string CreateCacheScope(
            Uri providerBaseUri,
            string username,
            string language)
        {
            var value = providerBaseUri.GetLeftPart(UriPartial.Authority)
                + "\n"
                + username
                + "\n"
                + language;
            using (var hash = SHA256.Create())
            {
                return BitConverter.ToString(
                        hash.ComputeHash(Encoding.UTF8.GetBytes(value)))
                    .Replace("-", string.Empty);
            }
        }
    }
}
