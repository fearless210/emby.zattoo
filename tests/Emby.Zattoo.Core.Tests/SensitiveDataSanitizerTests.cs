using Emby.Zattoo.Infrastructure;

namespace Emby.Zattoo.Core.Tests;

public sealed class SensitiveDataSanitizerTests
{
    [Fact]
    public void SanitizeText_RedactsSecretsAndCompleteUrls()
    {
        const string input =
            "password=hunter2 Cookie: beaker.session.id=abc "
            + "Authorization=Bearer ey.secret "
            + "https://example.invalid/live/path?token=signed-value";

        var sanitized = SensitiveDataSanitizer.SanitizeText(input);

        Assert.DoesNotContain("hunter2", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("ey.secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("signed-value", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("/live/path", sanitized, StringComparison.Ordinal);
        Assert.Contains("[redacted]", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://cdn.example.invalid/signed/manifest.mpd?sig=abc", "https://cdn.example.invalid/[redacted]")]
    [InlineData("not a URL", "[redacted]")]
    public void SanitizeUrl_NeverReturnsPathOrQuery(string input, string expected)
    {
        Assert.Equal(expected, SensitiveDataSanitizer.SanitizeUrl(input));
    }
}
