using System.Threading.Channels;
using Xunit;

namespace Tripo.HostUi.Tests;

public sealed class GenerationStatusPollerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    [Theory]
    [InlineData(null, true)]
    [InlineData("queued", true)]
    [InlineData("RUNNING", true)]
    [InlineData("success", false)]
    [InlineData("failed", false)]
    [InlineData("cancelled", false)]
    [InlineData("expired", false)]
    public void PendingTaskSelectionStopsAtTerminalStatus(
        string? status,
        bool expected)
    {
        Tripo.HostUi.TripoPanelState state =
            Tripo.HostUi.TripoPanelState.Initial with
            {
                Connected = true,
                GenerationReceipt =
                    new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                        "operation-id",
                        "task-id",
                        "model"),
                GenerationStatus = status is null
                    ? null
                    : TaskStatus("task-id", status),
            };

        string? taskId =
            Tripo.HostUi.GenerationStatusPoller.GetPendingTaskId(
                state,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty);

        Assert.Equal(expected ? "task-id" : null, taskId);
    }

    [Fact]
    public void PendingTaskSelectionRejectsNullStatusValue()
    {
        Tripo.HostUi.TripoPanelState state =
            Tripo.HostUi.TripoPanelState.Initial with
            {
                Connected = true,
                GenerationReceipt =
                    new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                        "operation-id",
                        "task-id",
                        "model"),
                GenerationStatus =
                    TaskStatus("task-id", "running") with
                    {
                        Status = null!,
                    },
            };

        Assert.Null(
            Tripo.HostUi.GenerationStatusPoller.GetPendingTaskId(
                state,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty));
    }

    [Fact]
    public void PendingTaskSelectionRequiresConnectionAndDurableIdentity()
    {
        Tripo.HostUi.TripoPanelState disconnected =
            Tripo.HostUi.TripoPanelState.Initial with
            {
                GenerationReceipt =
                    new Tripo.Bridge.HostControlTextTaskCreationReceipt(
                        "operation-id",
                        "task-id",
                        "model"),
            };
        Tripo.HostUi.TripoPanelState nonDurable =
            Tripo.HostUi.TripoPanelState.Initial with
            {
                Connected = true,
                GenerationOperationStatus = OperationStatus(
                    taskIdDurable: false),
            };
        Tripo.HostUi.TripoPanelState durable = nonDurable with
        {
            GenerationOperationStatus = OperationStatus(
                taskIdDurable: true),
        };

        Assert.Null(
            Tripo.HostUi.GenerationStatusPoller.GetPendingTaskId(
                disconnected,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty));
        Assert.Null(
            Tripo.HostUi.GenerationStatusPoller.GetPendingTaskId(
                nonDurable,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty));
        Assert.Equal(
            "task-id",
            Tripo.HostUi.GenerationStatusPoller.GetPendingTaskId(
                durable,
                Tripo.HostUi.TripoPanelRecoveryLoadResult.Empty));
        Assert.Null(
            Tripo.HostUi.GenerationStatusPoller.GetPendingTaskId(
                durable,
                BlockingRecovery()));
    }

    [Fact]
    public async Task SameTaskUsesOneSequentialPollingLoop()
    {
        ControlledDelay delay = new();
        TaskCompletionSource<bool> firstRefreshEntered =
            NewCompletionSource();
        TaskCompletionSource<bool> releaseFirstRefresh =
            NewCompletionSource();
        TaskCompletionSource<bool> secondRefreshEntered =
            NewCompletionSource();
        int calls = 0;
        int inFlight = 0;
        int maximumInFlight = 0;
        List<Exception> failures = [];
        using Tripo.HostUi.GenerationStatusPoller poller = new(
            Interval,
            async (taskId, cancellationToken) =>
            {
                Assert.Equal("task-id", taskId);
                int current = Interlocked.Increment(ref inFlight);
                UpdateMaximum(ref maximumInFlight, current);
                int call = Interlocked.Increment(ref calls);
                try
                {
                    if (call == 1)
                    {
                        firstRefreshEntered.TrySetResult(true);
                        await releaseFirstRefresh.Task.WaitAsync(
                            cancellationToken);
                    }
                    else if (call == 2)
                    {
                        secondRefreshEntered.TrySetResult(true);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
            },
            (_, exception) => failures.Add(exception),
            delay.WaitAsync);

        poller.Reconcile("task-id");
        poller.Reconcile("task-id");
        poller.Resume("task-id");
        ControlledDelay.Request firstDelay = await delay.NextAsync();
        Assert.Equal(Interval, firstDelay.Interval);
        firstDelay.Release();
        await firstRefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        poller.Reconcile("task-id");
        releaseFirstRefresh.TrySetResult(true);
        ControlledDelay.Request secondDelay = await delay.NextAsync();
        secondDelay.Release();
        await secondRefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(1, Volatile.Read(ref maximumInFlight));
        Assert.Empty(failures);
    }

    [Fact]
    public async Task FailureStopsTaskUntilExplicitResume()
    {
        ControlledDelay delay = new();
        TaskCompletionSource<bool> failureReported =
            NewCompletionSource();
        TaskCompletionSource<bool> resumedRefreshEntered =
            NewCompletionSource();
        int calls = 0;
        List<Exception> failures = [];
        using Tripo.HostUi.GenerationStatusPoller poller = new(
            Interval,
            (taskId, _) =>
            {
                Assert.Equal("task-id", taskId);
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException("status unavailable");
                }

                resumedRefreshEntered.TrySetResult(true);
                return Task.CompletedTask;
            },
            (taskId, exception) =>
            {
                Assert.Equal("task-id", taskId);
                failures.Add(exception);
                failureReported.TrySetResult(true);
            },
            delay.WaitAsync);

        poller.Reconcile("task-id");
        (await delay.NextAsync()).Release();
        await failureReported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        poller.Reconcile("task-id");
        await Task.Yield();
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(1, delay.RequestCount);

        poller.Resume("task-id");
        (await delay.NextAsync()).Release();
        await resumedRefreshEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        InvalidOperationException failure =
            Assert.IsType<InvalidOperationException>(Assert.Single(failures));
        Assert.Equal("status unavailable", failure.Message);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ResumeDuringFailureReportRestartsAfterUnwind()
    {
        ControlledDelay delay = new();
        TaskCompletionSource<bool> failureReporterEntered =
            NewCompletionSource();
        TaskCompletionSource<bool> releaseFailureReporter =
            NewCompletionSource();
        TaskCompletionSource<bool> resumedRefreshEntered =
            NewCompletionSource();
        int calls = 0;
        using Tripo.HostUi.GenerationStatusPoller poller = new(
            Interval,
            (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException("status unavailable");
                }

                resumedRefreshEntered.TrySetResult(true);
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                failureReporterEntered.TrySetResult(true);
                releaseFailureReporter.Task.GetAwaiter().GetResult();
            },
            delay.WaitAsync);

        poller.Reconcile("task-id");
        (await delay.NextAsync()).Release();
        await failureReporterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        poller.Resume("task-id");
        releaseFailureReporter.TrySetResult(true);
        (await delay.NextAsync()).Release();
        await resumedRefreshEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task StopCancelsInFlightRefreshWithoutReportingFailure()
    {
        ControlledDelay delay = new();
        TaskCompletionSource<bool> refreshEntered =
            NewCompletionSource();
        TaskCompletionSource<bool> refreshCancelled =
            NewCompletionSource();
        List<Exception> failures = [];
        using Tripo.HostUi.GenerationStatusPoller poller = new(
            Interval,
            async (_, cancellationToken) =>
            {
                refreshEntered.TrySetResult(true);
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    refreshCancelled.TrySetResult(true);
                    throw;
                }
            },
            (_, exception) => failures.Add(exception),
            delay.WaitAsync);
        poller.Reconcile("task-id");
        (await delay.NextAsync()).Release();
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        poller.Stop();
        await refreshCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(failures);
    }

    [Fact]
    public async Task StopThenResumeSameTaskStartsExactlyOneFreshRun()
    {
        ControlledDelay delay = new();
        TaskCompletionSource<bool> firstRefreshEntered =
            NewCompletionSource();
        TaskCompletionSource<bool> firstRefreshCancelled =
            NewCompletionSource();
        TaskCompletionSource<bool> releaseFirstRefreshExit =
            NewCompletionSource();
        TaskCompletionSource<bool> resumedRefreshEntered =
            NewCompletionSource();
        TaskCompletionSource<bool> releaseResumedRefreshExit =
            NewCompletionSource();
        int calls = 0;
        int inFlight = 0;
        int maximumInFlight = 0;
        List<Exception> failures = [];
        using Tripo.HostUi.GenerationStatusPoller poller = new(
            Interval,
            async (_, cancellationToken) =>
            {
                int current = Interlocked.Increment(ref inFlight);
                UpdateMaximum(ref maximumInFlight, current);
                int call = Interlocked.Increment(ref calls);
                try
                {
                    if (call == 1)
                    {
                        firstRefreshEntered.TrySetResult(true);
                        try
                        {
                            await Task.Delay(
                                Timeout.InfiniteTimeSpan,
                                cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            firstRefreshCancelled.TrySetResult(true);
                            await releaseFirstRefreshExit.Task;
                            throw;
                        }
                    }
                    else
                    {
                        resumedRefreshEntered.TrySetResult(true);
                        await releaseResumedRefreshExit.Task;
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
            },
            (_, exception) => failures.Add(exception),
            delay.WaitAsync);
        poller.Reconcile("task-id");
        (await delay.NextAsync()).Release();
        await firstRefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        poller.Stop();
        await firstRefreshCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        poller.Resume("task-id");
        for (int index = 0; index < 10; index++)
        {
            await Task.Yield();
        }

        int requestsBeforeOldRunExit = delay.RequestCount;
        releaseFirstRefreshExit.TrySetResult(true);
        Assert.Equal(1, requestsBeforeOldRunExit);
        (await delay.NextAsync()).Release();
        await resumedRefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        int totalRequests = delay.RequestCount;
        int totalCalls = Volatile.Read(ref calls);
        int observedMaximumInFlight =
            Volatile.Read(ref maximumInFlight);
        releaseResumedRefreshExit.TrySetResult(true);
        Assert.Equal(2, totalRequests);
        Assert.Equal(2, totalCalls);
        Assert.Equal(1, observedMaximumInFlight);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task ReplacingTaskCancelsOldDelayAndPollsOnlyNewTask()
    {
        ControlledDelay delay = new();
        TaskCompletionSource<bool> newTaskRefreshed =
            NewCompletionSource();
        List<string> refreshedTaskIds = [];
        List<Exception> failures = [];
        using Tripo.HostUi.GenerationStatusPoller poller = new(
            Interval,
            (taskId, _) =>
            {
                refreshedTaskIds.Add(taskId);
                if (taskId == "new-task")
                {
                    newTaskRefreshed.TrySetResult(true);
                }

                return Task.CompletedTask;
            },
            (_, exception) => failures.Add(exception),
            delay.WaitAsync);

        poller.Reconcile("old-task");
        ControlledDelay.Request oldDelay = await delay.NextAsync();
        poller.Reconcile("new-task");
        ControlledDelay.Request newDelay = await delay.NextAsync();

        oldDelay.Release();
        newDelay.Release();
        await newTaskRefreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["new-task"], refreshedTaskIds);
        Assert.Empty(failures);
    }

    private static Tripo.Bridge.HostControlTaskStatusReceipt TaskStatus(
        string taskId,
        string status) =>
        new(
            taskId,
            "text_to_model",
            status,
            status == "success" ? 100 : 36,
            null,
            null,
            null,
            null,
            null);

    private static Tripo.Bridge.HostControlOperationStatusReceipt
        OperationStatus(bool taskIdDurable) =>
        new(
            "operation-id",
            "text_task_creation",
            taskIdDurable ? "completed" : "outcome_unknown",
            null,
            "task-id",
            null,
            null,
            taskIdDurable,
            !taskIdDurable,
            false,
            "next action",
            DateTimeOffset.UnixEpoch);

    private static Tripo.HostUi.TripoPanelRecoveryLoadResult
        BlockingRecovery() =>
        new(
            [],
            [
                new Tripo.HostUi.TripoPanelRecoveryIssue(
                    "recovery.json",
                    "invalid",
                    "Manual review required."),
            ]);

    private static TaskCompletionSource<bool> NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int current = Volatile.Read(ref maximum);
        while (value > current)
        {
            int observed = Interlocked.CompareExchange(
                ref maximum,
                value,
                current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private sealed class ControlledDelay
    {
        private readonly Channel<Request> _requests =
            Channel.CreateUnbounded<Request>();
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public Task<bool> WaitAsync(
            TimeSpan interval,
            CancellationToken cancellationToken)
        {
            Request request = new(interval, cancellationToken);
            Interlocked.Increment(ref _requestCount);
            if (!_requests.Writer.TryWrite(request))
            {
                throw new InvalidOperationException(
                    "The controlled delay queue is closed.");
            }

            return request.WaitAsync();
        }

        public async Task<Request> NextAsync() =>
            await _requests.Reader.ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));

        public sealed class Request
        {
            private readonly CancellationToken _cancellationToken;
            private readonly TaskCompletionSource<bool> _release =
                NewCompletionSource();

            public Request(
                TimeSpan interval,
                CancellationToken cancellationToken)
            {
                Interval = interval;
                _cancellationToken = cancellationToken;
            }

            public TimeSpan Interval { get; }

            public void Release() => _release.TrySetResult(true);

            public Task<bool> WaitAsync() =>
                _release.Task.WaitAsync(_cancellationToken);
        }
    }
}
