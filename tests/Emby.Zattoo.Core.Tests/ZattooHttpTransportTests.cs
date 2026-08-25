using Emby.Zattoo.Infrastructure;
using Emby.Zattoo.Zattoo;

namespace Emby.Zattoo.Core.Tests;

public sealed class ZattooHttpTransportTests
{
    [Theory]
    [InlineData("/token.json", "https://zattoo.example/token.json")]
    [InlineData("zapi/session/hello", "https://zattoo.example/zapi/session/hello")]
    public void CreateRequestUri_AllowsProviderRelativePaths(
        string relativePath,
        string expected)
    {
        using var transport = CreateTransport();

        var result = transport.CreateRequestUri(relativePath);

        Assert.Equal(new Uri(expected), result);
    }

    [Theory]
    [InlineData("https://example.invalid/token.json")]
    [InlineData("//example.invalid/token.json")]
    [InlineData("file:///tmp/token.json")]
    [InlineData("/\\example.invalid/token.json")]
    [InlineData("/token.json\r\nInjected: value")]
    public void CreateRequestUri_RejectsExternalOrUnsafePaths(string relativePath)
    {
        using var transport = CreateTransport();

        Assert.Throws<ArgumentException>(() => transport.CreateRequestUri(relativePath));
    }

    private static ZattooHttpTransport CreateTransport()
    {
        return new ZattooHttpTransport(new ZattooClientOptions
        {
            ProviderBaseUri = new Uri("https://zattoo.example/"),
            UserAgent = "Emby.Zattoo.Tests/1.0",
        });
    }
}
