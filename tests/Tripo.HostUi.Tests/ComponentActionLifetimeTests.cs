using Tripo.HostUi;
using Xunit;

namespace Tripo.HostUi.Tests;

public sealed class ComponentActionLifetimeTests
{
    [Fact]
    public void ReattachedComponentCanStartAnotherExplicitAction()
    {
        ComponentActionLifetime lifetime = new();
        Assert.False(lifetime.TryBeginAction());
        lifetime.Attach();
        Assert.True(lifetime.TryBeginAction());
        Assert.False(lifetime.TryBeginAction());
        Assert.True(lifetime.IsActionCurrent);
        lifetime.EndAction(() => Assert.Fail("An attached component is still in use."));

        bool cleaned = false;
        lifetime.Remove(() => cleaned = true);
        Assert.True(cleaned);
        Assert.False(lifetime.TryBeginAction());
        lifetime.Attach();
        Assert.True(lifetime.TryBeginAction());
        Assert.True(lifetime.IsActionCurrent);
    }

    [Fact]
    public void RemovalInvalidatesQueuedCallbacksAfterReattachment()
    {
        ComponentActionLifetime lifetime = new();
        lifetime.Attach();
        Assert.True(lifetime.TryBeginAction());
        long? queuedAttachment = lifetime.ActionAttachment;
        lifetime.EndAction(() => Assert.Fail("An attached component is still in use."));
        Assert.True(lifetime.MatchesAttachment(queuedAttachment));

        lifetime.Remove(() => { });
        Assert.False(lifetime.MatchesAttachment(queuedAttachment));
        lifetime.Attach();
        Assert.False(lifetime.MatchesAttachment(queuedAttachment));
        Assert.True(lifetime.TryBeginAction());
        Assert.True(lifetime.MatchesAttachment(lifetime.ActionAttachment));
        Assert.False(lifetime.MatchesAttachment(queuedAttachment));
    }

    [Fact]
    public void ReattachmentCannotReviveOrOverlapAnOldRequest()
    {
        ComponentActionLifetime lifetime = new();
        lifetime.Attach();
        Assert.True(lifetime.TryBeginAction());
        long? oldAttachment = lifetime.ActionAttachment;
        lifetime.Remove(() => Assert.Fail("The pending request still needs its journal."));
        Assert.False(lifetime.IsActionCurrent);
        lifetime.Attach();
        Assert.False(lifetime.IsActionCurrent);
        Assert.False(lifetime.TryBeginAction());
        Assert.False(lifetime.MatchesAttachment(oldAttachment));
        lifetime.EndAction(() => Assert.Fail("The restored component owns its store."));
        Assert.True(lifetime.TryBeginAction());
        Assert.True(lifetime.IsActionCurrent);
    }

    [Fact]
    public void RemovedRequestKeepsItsJournalUntilItFinishes()
    {
        ComponentActionLifetime lifetime = new();
        lifetime.Attach();
        Assert.True(lifetime.TryBeginAction());
        int cleanupCalls = 0;
        lifetime.Remove(() => cleanupCalls++);
        Assert.Equal(0, cleanupCalls);
        lifetime.EndAction(() => cleanupCalls++);
        Assert.Equal(1, cleanupCalls);
        Assert.False(lifetime.TryBeginAction());
        Assert.False(lifetime.IsActionCurrent);
    }
}
