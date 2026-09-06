using Emby.Zattoo.Plugin.LiveTv;

namespace Emby.Zattoo.Plugin.Tests;

public sealed class ZattooStreamRestartBudgetTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 6, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryAcquire_AllowsTheConfiguredNumberOfRestarts()
    {
        var budget = new ZattooStreamRestartBudget(3, TimeSpan.FromMinutes(5));

        Assert.True(budget.TryAcquire(Start));
        Assert.True(budget.TryAcquire(Start.AddSeconds(30)));
        Assert.True(budget.TryAcquire(Start.AddSeconds(60)));
        Assert.Equal(3, budget.SpentRestarts);
    }

    [Fact]
    public void TryAcquire_RefusesOneRestartTooMany()
    {
        var budget = new ZattooStreamRestartBudget(2, TimeSpan.FromMinutes(5));
        budget.TryAcquire(Start);
        budget.TryAcquire(Start.AddSeconds(1));

        Assert.False(budget.TryAcquire(Start.AddSeconds(2)));
    }

    [Fact]
    public void TryAcquire_AllowsAgainOnceTheWindowHasSlid()
    {
        // A long recording must survive several unrelated incidents; only a loop
        // has to be refused.
        var budget = new ZattooStreamRestartBudget(1, TimeSpan.FromMinutes(5));
        Assert.True(budget.TryAcquire(Start));
        Assert.False(budget.TryAcquire(Start.AddMinutes(4)));

        Assert.True(budget.TryAcquire(Start.AddMinutes(5)));
        Assert.Equal(1, budget.SpentRestarts);
    }

    [Fact]
    public void TryAcquire_DropsOnlyTheEntriesOutsideTheWindow()
    {
        var budget = new ZattooStreamRestartBudget(2, TimeSpan.FromMinutes(5));
        budget.TryAcquire(Start);
        budget.TryAcquire(Start.AddMinutes(4));

        Assert.True(budget.TryAcquire(Start.AddMinutes(5)));
        Assert.False(budget.TryAcquire(Start.AddMinutes(6)));
    }

    [Fact]
    public void TryAcquire_RefusesEverythingWhenNoRestartIsAllowed()
    {
        var budget = new ZattooStreamRestartBudget(0, TimeSpan.FromMinutes(5));

        Assert.False(budget.TryAcquire(Start));
    }

    [Fact]
    public void Constructor_RejectsAnUnusableConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ZattooStreamRestartBudget(-1, TimeSpan.FromMinutes(5)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ZattooStreamRestartBudget(3, TimeSpan.Zero));
    }

    [Fact]
    public void Constructor_DefaultsToThreeRestartsInFiveMinutes()
    {
        var budget = new ZattooStreamRestartBudget();

        Assert.True(budget.TryAcquire(Start));
        Assert.True(budget.TryAcquire(Start.AddSeconds(1)));
        Assert.True(budget.TryAcquire(Start.AddSeconds(2)));
        Assert.False(budget.TryAcquire(Start.AddSeconds(3)));
        Assert.True(budget.TryAcquire(Start.AddMinutes(5)));
    }
}
