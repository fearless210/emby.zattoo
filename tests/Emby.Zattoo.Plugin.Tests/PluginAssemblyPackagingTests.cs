using Emby.Zattoo.Zattoo;

namespace Emby.Zattoo.Plugin.Tests;

public sealed class PluginAssemblyPackagingTests
{
    [Fact]
    public void PluginAssembly_ContainsCoreWithoutExternalAssemblyReference()
    {
        var pluginAssembly = typeof(global::Emby.Zattoo.Plugin.Plugin).Assembly;

        Assert.Same(pluginAssembly, typeof(ZattooClient).Assembly);
        Assert.DoesNotContain(
            pluginAssembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "Emby.Zattoo.Core", StringComparison.Ordinal));
    }
}
