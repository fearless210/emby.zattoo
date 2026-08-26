using Emby.Zattoo.Models;
using Emby.Zattoo.Plugin.Configuration;

namespace Emby.Zattoo.Plugin.Tests;

public sealed class ZattooRuntimeSettingsTests
{
    [Fact]
    public void FromConfiguration_MapsServerOptionsWithoutEnvironmentVariables()
    {
        var options = new ZattooPluginOptions
        {
            Username = " user@example.invalid ",
            Password = "encrypted-value-not-used-here",
            PreferredQuality = ZattooPreferredQuality.P720,
            FfmpegPath = " /opt/emby-server/bin/ffmpeg ",
            ProviderUrl = "https://zattoo.example/",
            ApplicationVersion = "3.2120.1",
        };

        var settings = ZattooRuntimeSettings.FromConfiguration(
            options,
            "decrypted-test-password");

        Assert.Equal("user@example.invalid", settings.ClientOptions.Username);
        Assert.Equal("decrypted-test-password", settings.ClientOptions.Password);
        Assert.Equal(new Uri("https://zattoo.example/"), settings.ClientOptions.ProviderBaseUri);
        Assert.Equal("Emby.Zattoo.Plugin/0.2.4", settings.ClientOptions.UserAgent);
        Assert.Equal(ZattooPreferredQuality.P720, settings.PreferredQuality);
        Assert.Equal("/opt/emby-server/bin/ffmpeg", settings.FfmpegPath);
    }
}
