using Emby.Web.GenericEdit.Validation;
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
            ChannelImportMode = ZattooChannelImportMode.ExcludeDrmOnly,
            EnableGuideDetails = false,
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
        Assert.Matches(
            @"^Emby\.Zattoo\.Plugin/\d+\.\d+\.\d+$",
            settings.ClientOptions.UserAgent);
        Assert.False(settings.ClientOptions.EnableBackgroundGuideDetails);
        Assert.Equal(ZattooPreferredQuality.P720, settings.PreferredQuality);
        Assert.Equal(
            ZattooChannelImportMode.ExcludeDrmOnly,
            settings.ChannelImportMode);
        Assert.Equal("/opt/emby-server/bin/ffmpeg", settings.FfmpegPath);
    }

    [Fact]
    public void FromConfiguration_KeepsAnEmptyFfmpegPathForTheTunerToResolve()
    {
        var options = new ZattooPluginOptions
        {
            Username = "user@example.invalid",
            Password = "encrypted-value-not-used-here",
            FfmpegPath = "   ",
        };

        var settings = ZattooRuntimeSettings.FromConfiguration(
            options,
            "decrypted-test-password");

        // Empty means "use the FFmpeg Emby runs", which only the tuner can resolve.
        Assert.Equal(string.Empty, settings.FfmpegPath);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(14)]
    public void FromConfiguration_CarriesTheGuideDepthUnchanged(int guideDays)
    {
        var options = new ZattooPluginOptions
        {
            Username = "user@example.invalid",
            Password = "encrypted-value-not-used-here",
            GuideDays = guideDays,
        };

        var settings = ZattooRuntimeSettings.FromConfiguration(
            options,
            "decrypted-test-password");

        Assert.Equal(guideDays, settings.GuideDays);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(15)]
    public void Validate_RejectsAGuideDepthEmbyWouldNotAccept(int guideDays)
    {
        var options = new ZattooPluginOptions
        {
            Username = "user@example.invalid",
            Password = "encrypted-value-not-used-here",
            GuideDays = guideDays,
        };

        var failure = Assert.Throws<ValidationException>(() => options.ValidateOrThrow());

        Assert.Contains("14", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromConfiguration_ScopesPersistentGuideCacheWithoutAccountName()
    {
        var options = new ZattooPluginOptions
        {
            Username = "private-account@example.invalid",
            Password = "encrypted-value-not-used-here",
            EnableGuideDetails = true,
            ProviderUrl = "https://zattoo.example/",
        };
        var dataFolder = Path.Combine(Path.GetTempPath(), "plugin-data-fixture");

        var settings = ZattooRuntimeSettings.FromConfiguration(
            options,
            "decrypted-test-password",
            dataFolder);

        Assert.Equal(
            Path.Combine(dataFolder, "guide-details-cache-v1.jsonl"),
            settings.ClientOptions.GuideDetailsCachePath);
        Assert.Equal(64, settings.ClientOptions.GuideDetailsCacheScope.Length);
        Assert.DoesNotContain(
            "private-account",
            settings.ClientOptions.GuideDetailsCacheScope,
            StringComparison.OrdinalIgnoreCase);
    }
}
