using System;
using System.Threading.Tasks;
using TaskbarMonitor;
using Xunit;

namespace ConfigTests;

public sealed class TaskbarRecoveryCoordinatorTests
{
    [Fact]
    public async Task FailedRecovery_ReleasesGate_ForTheNextExplorerRestart()
    {
        var coordinator = new TaskbarRecoveryCoordinator();
        int attempts = 0;

        bool firstSucceeded = await coordinator.RunAsync(() =>
        {
            attempts++;
            throw new InvalidOperationException("Taskbar is temporarily unavailable.");
        });

        bool ranAgain = await coordinator.RunAsync(() =>
        {
            attempts++;
            return Task.CompletedTask;
        });

        Assert.False(firstSucceeded);
        Assert.True(ranAgain);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ConcurrentRecovery_IsIgnored_WithoutBlockingTheCurrentAttempt()
    {
        var coordinator = new TaskbarRecoveryCoordinator();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        Task<bool> first = coordinator.RunAsync(async () =>
        {
            entered.SetResult();
            await release.Task;
        });

        await entered.Task;
        bool second = await coordinator.RunAsync(() => Task.CompletedTask);
        release.SetResult();

        Assert.False(second);
        Assert.True(await first);
    }
}