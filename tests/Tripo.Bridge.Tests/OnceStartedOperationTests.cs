using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class OnceStartedOperationTests
{
    [Fact]
    public async Task CancellationBeforeStartSkipsQueuedOperation()
    {
        Action? queued = null;
        bool invoked = false;
        using CancellationTokenSource cancellation = new();
        Task<int> task = Tripo.Bridge.OnceStartedOperation.DispatchAsync(
            () =>
            {
                invoked = true;
                return 7;
            },
            callback => queued = callback,
            cancellation.Token);

        cancellation.Cancel();
        queued!();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await task);
        Assert.False(invoked);
    }

    [Fact]
    public async Task CancellationAfterStartWaitsForRealOperationCompletion()
    {
        TaskCompletionSource<bool> started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        Task<int> task = Tripo.Bridge.OnceStartedOperation.DispatchAsync(
            () =>
            {
                started.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
                return 11;
            },
            callback => _ = Task.Run(callback),
            cancellation.Token);

        await started.Task;
        cancellation.Cancel();
        Assert.False(task.IsCompleted);
        release.TrySetResult(true);

        Assert.Equal(11, await task);
    }

    [Fact]
    public async Task DuplicateDispatcherCallbackRunsOperationOnlyOnce()
    {
        int calls = 0;
        Task<int> task = Tripo.Bridge.OnceStartedOperation.DispatchAsync(
            () =>
            {
                calls++;
                return 13;
            },
            callback =>
            {
                callback();
                callback();
            },
            CancellationToken.None);

        Assert.Equal(13, await task);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DispatcherThrowAfterStartCannotCompleteAheadOfOperation()
    {
        TaskCompletionSource<bool> started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> task = Tripo.Bridge.OnceStartedOperation.DispatchAsync(
            () =>
            {
                started.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
                return 17;
            },
            callback =>
            {
                Task runner = Task.Run(callback);
                started.Task.GetAwaiter().GetResult();
                _ = runner;
                throw new InvalidOperationException("late dispatcher failure");
            },
            CancellationToken.None);

        Assert.False(task.IsCompleted);
        release.TrySetResult(true);

        Assert.Equal(17, await task);
    }
}
