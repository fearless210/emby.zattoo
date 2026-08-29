using System.Reflection;
using Emby.Zattoo.Models;
using Emby.Zattoo.Plugin.Configuration;

namespace Emby.Zattoo.Plugin.Tests;

public sealed class ZattooPluginOptionsTests
{
    [Fact]
    public void CopyForDisplay_KeepsEverySettingExceptThePassword()
    {
        var options = new ZattooPluginOptions();
        var written = FillEverySetting(options);

        var copy = options.CopyForDisplay("**********");

        // Every setting has to survive the round trip to the settings page.
        // Listing them by hand once dropped the ones added later, which erased
        // them on the next save.
        foreach (var property in written)
        {
            if (property.Name == nameof(ZattooPluginOptions.Password))
            {
                continue;
            }

            Assert.Equal(property.GetValue(options), property.GetValue(copy));
        }

        Assert.Equal("**********", copy.Password);
        Assert.NotEmpty(written);
    }

    [Fact]
    public void CopyForDisplay_CoversEveryWritableSetting()
    {
        var writable = typeof(ZattooPluginOptions)
            .GetProperties()
            .Where(property => property.CanRead && property.CanWrite)
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(nameof(ZattooPluginOptions.ChannelGroups), writable);
        Assert.Contains(nameof(ZattooPluginOptions.GuideDays), writable);
        Assert.Contains(nameof(ZattooPluginOptions.FfmpegPath), writable);
    }

    /// <summary>
    /// Writes a value different from the default into every setting, so that a
    /// property left behind by the copy shows up as a mismatch.
    /// </summary>
    private static IReadOnlyList<PropertyInfo> FillEverySetting(
        ZattooPluginOptions options)
    {
        var written = new List<PropertyInfo>();
        foreach (var property in typeof(ZattooPluginOptions).GetProperties())
        {
            if (!property.CanRead || !property.CanWrite)
            {
                continue;
            }

            var type = property.PropertyType;
            if (type == typeof(string))
            {
                property.SetValue(options, "value-of-" + property.Name);
            }
            else if (type == typeof(int))
            {
                property.SetValue(options, 7);
            }
            else if (type == typeof(bool))
            {
                property.SetValue(options, !(bool)property.GetValue(options)!);
            }
            else if (type.IsEnum)
            {
                var values = Enum.GetValues(type);
                property.SetValue(options, values.GetValue(values.Length - 1));
            }
            else
            {
                Assert.Fail(
                    $"{property.Name} has type {type.Name}, which this test does "
                    + "not know how to fill. Teach it, so the property stays covered.");
            }

            written.Add(property);
        }

        return written;
    }
}
