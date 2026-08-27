using System;
using System.IO;
using System.Security.Cryptography;
using Emby.Zattoo.Models;

namespace Emby.Zattoo.Zattoo
{
    public sealed class ZattooClientOptions
    {
        private const string DeviceAlphabet =
            "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-";

        public string Username { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public Uri ProviderBaseUri { get; set; } = new Uri("https://zattoo.com/", UriKind.Absolute);

        /// <summary>Web application version observed in the current Streamlink client.</summary>
        public string ApplicationVersion { get; set; } = "3.2120.1";

        public string Language { get; set; } = "en";

        public string UserAgent { get; set; } = "Emby.Zattoo/0.1.0";

        public string DeviceId { get; set; } = GenerateDeviceId();

        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        public TimeSpan GuideCacheDuration { get; set; } = TimeSpan.FromMinutes(30);

        public bool EnableBackgroundGuideDetails { get; set; }

        public TimeSpan GuideDetailsRequestInterval { get; set; } =
            TimeSpan.FromSeconds(1);

        public TimeSpan GuideDetailsRetryDelay { get; set; } =
            TimeSpan.FromSeconds(30);

        public Action<ZattooGuideDetailsProgress>? GuideDetailsProgress { get; set; }

        public string GuideDetailsCachePath { get; set; } = string.Empty;

        public string GuideDetailsCacheScope { get; set; } = string.Empty;

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(Username))
            {
                throw new ArgumentException("A Zattoo username is required.", nameof(Username));
            }

            if (string.IsNullOrEmpty(Password))
            {
                throw new ArgumentException("A Zattoo password is required.", nameof(Password));
            }

            if (!ProviderBaseUri.IsAbsoluteUri || ProviderBaseUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException("The Zattoo provider URI must be an absolute HTTPS URI.", nameof(ProviderBaseUri));
            }

            if (string.IsNullOrWhiteSpace(ApplicationVersion)
                || string.IsNullOrWhiteSpace(Language)
                || string.IsNullOrWhiteSpace(UserAgent)
                || string.IsNullOrWhiteSpace(DeviceId))
            {
                throw new ArgumentException("Application, language, user agent and device values are required.");
            }

            if (RequestTimeout <= TimeSpan.Zero && RequestTimeout != System.Threading.Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
            }

            if (GuideCacheDuration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(GuideCacheDuration));
            }

            if (GuideDetailsRequestInterval < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(GuideDetailsRequestInterval));
            }

            if (GuideDetailsRetryDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(GuideDetailsRetryDelay));
            }

            var hasCachePath = !string.IsNullOrWhiteSpace(GuideDetailsCachePath);
            var hasCacheScope = !string.IsNullOrWhiteSpace(GuideDetailsCacheScope);
            if (hasCachePath != hasCacheScope)
            {
                throw new ArgumentException(
                    "The guide detail cache path and scope must be configured together.");
            }

            if (hasCachePath && !Path.IsPathRooted(GuideDetailsCachePath))
            {
                throw new ArgumentException(
                    "The guide detail cache path must be absolute.",
                    nameof(GuideDetailsCachePath));
            }
        }

        private static string GenerateDeviceId()
        {
            var bytes = new byte[21];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            var chars = new char[bytes.Length];
            for (var index = 0; index < bytes.Length; index++)
            {
                chars[index] = DeviceAlphabet[bytes[index] % DeviceAlphabet.Length];
            }

            return new string(chars);
        }
    }
}
