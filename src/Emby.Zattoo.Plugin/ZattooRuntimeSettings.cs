using System;
using System.Collections.Generic;
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
            string ffmpegPath,
            int guideDays,
            IReadOnlyList<string> channelGroups)
        {
            ChannelGroups = channelGroups;
            ClientOptions = clientOptions;
            PreferredQuality = preferredQuality;
            ChannelImportMode = channelImportMode;
            FfmpegPath = ffmpegPath;
            GuideDays = guideDays;
        }

        public ZattooClientOptions ClientOptions { get; }

        public ZattooPreferredQuality PreferredQuality { get; }

        public ZattooChannelImportMode ChannelImportMode { get; }

        public string FfmpegPath { get; }

        /// <summary>Gets the guide depth to impose on Emby, or 0 to leave it alone.</summary>
        public int GuideDays { get; }

        /// <summary>Gets the provider groups to import, empty for all of them.</summary>
        public IReadOnlyList<string> ChannelGroups { get; }

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

                // An empty path is not a default: it asks the tuner to use the
                // FFmpeg Emby runs. Resolving it here would hide that intent.
                configuration.FfmpegPath?.Trim() ?? string.Empty,
                configuration.GuideDays,
                SplitChannelGroups(configuration.ChannelGroups));
        }

        private static IReadOnlyList<string> SplitChannelGroups(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            var groups = new List<string>();
            foreach (var group in value.Split(','))
            {
                var normalized = group.Trim();
                if (normalized.Length > 0)
                {
                    groups.Add(normalized);
                }
            }

            return groups;
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
