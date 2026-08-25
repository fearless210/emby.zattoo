using System;
using System.Text.RegularExpressions;

namespace Emby.Zattoo.Infrastructure
{
    /// <summary>Central redaction helper for future structured logging.</summary>
    public static class SensitiveDataSanitizer
    {
        private const string Redacted = "[redacted]";

        private static readonly Regex UrlRegex = new Regex(
            @"https?://[^\s'\""<>]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex AssignmentRegex = new Regex(
            @"(?<key>password|passwd|authorization|cookie|token|session_token|client_app_token|beaker\.session\.id|signature|sig|youth_protection_pin)(?<separator>\s*[:=]\s*)(?<value>[^\s;&,]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex BearerRegex = new Regex(
            @"(?<prefix>Bearer\s+)[A-Za-z0-9._~+\-/=]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string SanitizeText(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sanitized = UrlRegex.Replace(value, match => SanitizeUrl(match.Value));
            sanitized = BearerRegex.Replace(
                sanitized,
                match => match.Groups["prefix"].Value + Redacted);
            sanitized = AssignmentRegex.Replace(
                sanitized,
                match => match.Groups["key"].Value + match.Groups["separator"].Value + Redacted);
            return sanitized;
        }

        public static string SanitizeUrl(string? value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return Redacted;
            }

            return uri.Scheme + "://" + uri.Host + "/" + Redacted;
        }
    }
}
