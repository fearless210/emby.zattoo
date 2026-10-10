using System.Reflection;
using Emby.Zattoo.Models;
using Emby.Zattoo.Plugin.LiveTv;
using Emby.Zattoo.Plugin.Tests.TestInfrastructure;
using MediaBrowser.Model.Logging;

namespace Emby.Zattoo.Plugin.Tests;

public sealed class ZattooLiveStreamConsumerTests
{
    [Fact]
    public void AddConsumer_CountsEachConsumer()
    {
        var stream = CreateStream();

        stream.AddConsumer("first");
        stream.AddConsumer("second");

        Assert.Equal(2, stream.ConsumerCount);
    }

    [Fact]
    public void RemoveConsumer_ReturnsToZeroOnceEveryConsumerLeft()
    {
        var stream = CreateStream();
        stream.AddConsumer("first");
        stream.AddConsumer("second");

        stream.RemoveConsumer("second");
        stream.RemoveConsumer("first");

        Assert.Equal(0, stream.ConsumerCount);
    }

    [Fact]
    public void RemoveConsumer_NeverLeavesTheCountNegative()
    {
        // A negative count would never read as zero again once the real
        // consumers are gone.
        var stream = CreateStream();

        stream.RemoveConsumer("unknown");
        stream.AddConsumer("first");

        Assert.Equal(1, stream.ConsumerCount);
    }

    [Fact]
    public void ConsumerCount_StillAcceptsTheEmby49Setter()
    {
        var stream = CreateStream();

        stream.ConsumerCount++;
        stream.ConsumerCount++;
        stream.ConsumerCount--;

        Assert.Equal(1, stream.ConsumerCount);
    }

    [Theory]
    [InlineData("AddConsumer")]
    [InlineData("RemoveConsumer")]
    [InlineData("set_ConsumerCount")]
    public void ConsumerMembers_AreBindableByEitherEmbyInterface(string name)
    {
        // Emby 4.10 added AddConsumer and RemoveConsumer to ILiveStream and
        // dropped the ConsumerCount setter. The runtime only binds a member the
        // plugin's own SDK does not declare when it is public and virtual: one
        // that is not fails to load the type, and no channel can open.
        var method = typeof(ZattooLiveStream).GetMethod(
            name,
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.True(method!.IsVirtual, name + " must be virtual.");
        Assert.False(method.IsFinal, name + " must not be sealed.");
        Assert.False(typeof(ZattooLiveStream).IsSealed);
    }

    private static ZattooLiveStream CreateStream()
    {
        return new ZattooLiveStream(
            "tuner",
            "channel",
            "Channel",
            new FakeZattooClient(),
            new ZattooStreamCapacity(),
            ZattooPreferredQuality.Auto,
            "ffmpeg",
            "http://localhost:8096",
            DispatchProxy.Create<ILogger, NullLoggerProxy>());
    }

    public class NullLoggerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
