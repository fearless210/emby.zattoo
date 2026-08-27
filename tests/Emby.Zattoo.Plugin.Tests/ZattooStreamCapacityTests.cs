using Emby.Zattoo.Plugin.LiveTv;

namespace Emby.Zattoo.Plugin.Tests;

public sealed class ZattooStreamCapacityTests
{
    [Fact]
    public void TryAcquire_EnforcesAndReleasesTheDetectedLimit()
    {
        var capacity = new ZattooStreamCapacity();
        capacity.UpdateLimit(2);

        using var first = capacity.TryAcquire();
        using var second = capacity.TryAcquire();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(capacity.TryAcquire());
        Assert.Equal(2, capacity.ActiveStreams);

        first.Dispose();

        using var replacement = capacity.TryAcquire();
        Assert.NotNull(replacement);
        Assert.Equal(2, capacity.ActiveStreams);
    }

    [Fact]
    public void UpdateLimit_BlocksNewStreamsWithoutInterruptingActiveOnes()
    {
        var capacity = new ZattooStreamCapacity();
        capacity.UpdateLimit(2);
        using var first = capacity.TryAcquire();
        using var second = capacity.TryAcquire();

        capacity.UpdateLimit(1);

        Assert.Null(capacity.TryAcquire());
        Assert.Equal(2, capacity.ActiveStreams);
        Assert.Equal(1, capacity.Limit);
    }
}
