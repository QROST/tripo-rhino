using System.Collections.Concurrent;
using Xunit;

namespace Tripo.HostUi.Tests;

public sealed class CoalescingUiRenderQueueTests
{
    [Fact]
    public void BurstRendersLeadingBusyAndTrailingFinalFrameSeparately()
    {
        ConcurrentQueue<Action> ui = new();
        List<TestFrame> rendered = [];
        using Tripo.HostUi.CoalescingUiRenderQueue<TestFrame> queue =
            new(ui.Enqueue, rendered.Add, FailUnexpectedly);
        TestFrame busy = new("busy", Busy: true);
        TestFrame intermediate = new("status", Busy: true);
        TestFrame final = new("final", Busy: false);

        Assert.True(queue.Request(busy));
        Assert.True(queue.Request(intermediate));
        Assert.True(queue.Request(final));

        Action first = Dequeue(ui);
        first();
        Assert.Equal([busy], rendered);
        Assert.Single(ui);

        Action trailing = Dequeue(ui);
        trailing();
        Assert.Equal([busy, final], rendered);
        Assert.Empty(ui);
    }

    [Fact]
    public void TrailingFinalErrorIsNeverLost()
    {
        ConcurrentQueue<Action> ui = new();
        List<TestFrame> rendered = [];
        using Tripo.HostUi.CoalescingUiRenderQueue<TestFrame> queue =
            new(ui.Enqueue, rendered.Add, FailUnexpectedly);
        TestFrame busy = new("busy", Busy: true);
        TestFrame final = new(
            "final",
            Busy: false,
            Error: "The task ID is not durable.");

        queue.Request(busy);
        queue.Request(new TestFrame("status", Busy: true));
        queue.Request(final);
        Dequeue(ui)();
        Dequeue(ui)();

        Assert.Equal(final, rendered[^1]);
        Assert.Equal("The task ID is not durable.", rendered[^1].Error);
    }

    [Fact]
    public void DisposeBeforeDrainSuppressesAllRenders()
    {
        ConcurrentQueue<Action> ui = new();
        List<TestFrame> rendered = [];
        Tripo.HostUi.CoalescingUiRenderQueue<TestFrame> queue =
            new(ui.Enqueue, rendered.Add, FailUnexpectedly);
        queue.Request(new TestFrame("old", Busy: true));

        queue.Dispose();
        Dequeue(ui)();

        Assert.Empty(rendered);
        Assert.False(queue.Request(new TestFrame("new", Busy: false)));
    }

    [Fact]
    public void CancelPendingLetsExistingCallbackRenderOnlyReplacement()
    {
        ConcurrentQueue<Action> ui = new();
        List<TestFrame> rendered = [];
        using Tripo.HostUi.CoalescingUiRenderQueue<TestFrame> queue =
            new(ui.Enqueue, rendered.Add, FailUnexpectedly);
        TestFrame replacement = new("replacement", Busy: false);
        queue.Request(new TestFrame("old", Busy: true));

        queue.CancelPending();
        queue.Request(replacement);
        Assert.Single(ui);
        Dequeue(ui)();

        Assert.Equal([replacement], rendered);
        Assert.Empty(ui);
    }

    [Fact]
    public void RequestsDuringRenderPostOneFollowUpWithOnlyTheLatestFrame()
    {
        ConcurrentQueue<Action> ui = new();
        List<TestFrame> rendered = [];
        Tripo.HostUi.CoalescingUiRenderQueue<TestFrame>? queue = null;
        TestFrame final = new("final", Busy: false);
        queue = new(
            ui.Enqueue,
            frame =>
            {
                rendered.Add(frame);
                if (frame.Name == "busy")
                {
                    queue!.Request(new TestFrame("status", Busy: true));
                    queue!.Request(final);
                }
            },
            FailUnexpectedly);
        using (queue)
        {
            queue.Request(new TestFrame("busy", Busy: true));
            Dequeue(ui)();
            Assert.Single(ui);
            Dequeue(ui)();
        }

        Assert.Equal(["busy", "final"], rendered.Select(frame => frame.Name));
        Assert.Equal(final, rendered[^1]);
        Assert.Empty(ui);
    }

    [Fact]
    public async Task WorkerRequestRendersOnlyWhenUiCallbackDrains()
    {
        ConcurrentQueue<Action> ui = new();
        List<int> renderThreadIds = [];
        using Tripo.HostUi.CoalescingUiRenderQueue<TestFrame> queue =
            new(
                ui.Enqueue,
                _ => renderThreadIds.Add(Environment.CurrentManagedThreadId),
                FailUnexpectedly);
        int workerThreadId = -1;
        await Task.Run(
            () =>
            {
                workerThreadId = Environment.CurrentManagedThreadId;
                queue.Request(new TestFrame("worker", Busy: true));
            });

        Assert.Empty(renderThreadIds);
        int drainThreadId = Environment.CurrentManagedThreadId;
        Dequeue(ui)();

        Assert.Equal([drainThreadId], renderThreadIds);
        Assert.NotEqual(workerThreadId, renderThreadIds[0]);
    }

    [Fact]
    public void PendingFollowUpIsReplacedByTheLatestFrame()
    {
        ConcurrentQueue<Action> ui = new();
        List<TestFrame> rendered = [];
        using Tripo.HostUi.CoalescingUiRenderQueue<TestFrame> queue =
            new(ui.Enqueue, rendered.Add, FailUnexpectedly);
        TestFrame leading = new("leading", Busy: true);
        TestFrame staleFollowUp = new("stale", Busy: true);
        TestFrame final = new("final", Busy: false);
        queue.Request(leading);
        queue.Request(staleFollowUp);

        Dequeue(ui)();
        Assert.Equal([leading], rendered);
        queue.Request(final);
        Dequeue(ui)();

        Assert.Equal([leading, final], rendered);
        Assert.Empty(ui);
    }

    [Fact]
    public void RenderFailureIsReportedAndDoesNotStarveLaterRequests()
    {
        ConcurrentQueue<Action> ui = new();
        List<TestFrame> rendered = [];
        List<Exception> failures = [];
        TestFrame failure = new("failure", Busy: true);
        TestFrame recovery = new("recovery", Busy: false);
        using Tripo.HostUi.CoalescingUiRenderQueue<TestFrame> queue =
            new(
                ui.Enqueue,
                frame =>
                {
                    if (frame == failure)
                    {
                        throw new InvalidOperationException("render failed");
                    }

                    rendered.Add(frame);
                },
                failures.Add);
        queue.Request(failure);

        Dequeue(ui)();
        InvalidOperationException exception =
            Assert.IsType<InvalidOperationException>(Assert.Single(failures));
        Assert.Equal("render failed", exception.Message);
        Assert.True(queue.Request(recovery));
        Dequeue(ui)();

        Assert.Equal([recovery], rendered);
        Assert.Empty(ui);
    }

    [Fact]
    public void FollowUpDispatchFailureIsReportedAndNewestFrameCanRecover()
    {
        ConcurrentQueue<Action> ui = new();
        List<TestFrame> rendered = [];
        List<Exception> failures = [];
        int enqueueCalls = 0;
        using Tripo.HostUi.CoalescingUiRenderQueue<TestFrame> queue =
            new(
                callback =>
                {
                    enqueueCalls++;
                    if (enqueueCalls == 2)
                    {
                        throw new InvalidOperationException(
                            "dispatcher unavailable");
                    }

                    ui.Enqueue(callback);
                },
                rendered.Add,
                failures.Add);
        TestFrame leading = new("leading", Busy: true);
        TestFrame staleFollowUp = new("stale", Busy: true);
        TestFrame final = new("final", Busy: false);
        queue.Request(leading);
        queue.Request(staleFollowUp);

        Dequeue(ui)();
        InvalidOperationException failure =
            Assert.IsType<InvalidOperationException>(Assert.Single(failures));
        Assert.Equal("dispatcher unavailable", failure.Message);
        Assert.Empty(ui);
        Assert.True(queue.Request(final));
        Dequeue(ui)();

        Assert.Equal([leading, final], rendered);
        Assert.Empty(ui);
    }

    private static void FailUnexpectedly(Exception exception) =>
        Assert.Fail($"Unexpected queue failure: {exception}");

    private static Action Dequeue(ConcurrentQueue<Action> queue)
    {
        Assert.True(queue.TryDequeue(out Action? callback));
        return callback;
    }

    private sealed record TestFrame(
        string Name,
        bool Busy,
        string? Error = null);
}
