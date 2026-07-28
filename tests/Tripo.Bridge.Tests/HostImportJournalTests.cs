using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class HostImportJournalTests
{
    [Fact]
    public void PreparedStateSurvivesReopenAndBlocksNativeRetry()
    {
        using TemporaryDataRoot root = new();
        Tripo.Bridge.HostImportJournalIdentity identity = Identity();
        using (Tripo.Bridge.HostImportJournal journal =
               Tripo.Bridge.HostImportJournal.Open(identity))
        {
            Assert.Null(journal.Current);
            journal.RecordPrepared();
        }

        using Tripo.Bridge.HostImportJournal reopened =
            Tripo.Bridge.HostImportJournal.Open(identity);
        Assert.Equal(
            Tripo.Bridge.HostImportJournal.PreparedState,
            reopened.Current?.State);
        Tripo.Bridge.BridgeCallException exception =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(
                reopened.RecordPrepared);
        Assert.Equal(
            Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
            exception.Code);
    }

    [Fact]
    public void AbortedBeforeImportAuthorizesSameIdentityRetry()
    {
        using TemporaryDataRoot root = new();
        Tripo.Bridge.HostImportJournalIdentity identity = Identity();
        using (Tripo.Bridge.HostImportJournal first =
               Tripo.Bridge.HostImportJournal.Open(identity))
        {
            first.RecordPrepared();
            first.RecordAbortedBeforeImport();
        }

        using Tripo.Bridge.HostImportJournal retry =
            Tripo.Bridge.HostImportJournal.Open(identity);
        retry.RecordPrepared();

        Assert.Equal(
            Tripo.Bridge.HostImportJournal.PreparedState,
            retry.Current?.State);
    }

    [Fact]
    public void CommittedStatePersistsBoundedReceiptAndRejectsIdentityChange()
    {
        using TemporaryDataRoot root = new();
        Tripo.Bridge.HostImportJournalIdentity identity = Identity();
        Tripo.Bridge.HostImportCommitReceipt commit = new(
            Guid.NewGuid().ToString("D"),
            VertexCount: 12,
            TriangleCount: 4,
            MaterialCount: 2,
            TextureCount: 3,
            DefinitionMemberCount: 2,
            DefinitionMemberDigest: new string('d', 64),
            PbrContentDigest: new string('e', 64));
        using (Tripo.Bridge.HostImportJournal journal =
               Tripo.Bridge.HostImportJournal.Open(identity))
        {
            journal.RecordPrepared();
            journal.RecordCommitted(commit);
        }

        using (Tripo.Bridge.HostImportJournal reopened =
               Tripo.Bridge.HostImportJournal.Open(identity with
               {
                   DocumentSessionId = Guid.NewGuid().ToString("D"),
               }))
        {
            Assert.Equal(
                Tripo.Bridge.HostImportJournal.CommittedState,
                reopened.Current?.State);
            Assert.Equal(commit, reopened.Current?.Commit);
        }

        Tripo.Bridge.HostImportJournalIdentity conflicting = identity with
        {
            ArtifactId = new string('b', 64),
        };
        Tripo.Bridge.BridgeCallException exception =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.HostImportJournal.Open(conflicting));
        Assert.Equal("idempotency_conflict", exception.Code);
    }

    [Fact]
    public void CommitRejectsMissingPbrContentProof()
    {
        using TemporaryDataRoot root = new();
        using Tripo.Bridge.HostImportJournal journal =
            Tripo.Bridge.HostImportJournal.Open(Identity());
        journal.RecordPrepared();

        Tripo.Bridge.BridgeCallException exception =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(
                () => journal.RecordCommitted(
                    new Tripo.Bridge.HostImportCommitReceipt(
                        Guid.NewGuid().ToString("D"),
                        VertexCount: 3,
                        TriangleCount: 1,
                        MaterialCount: 1,
                        TextureCount: 1,
                        DefinitionMemberCount: 1,
                        DefinitionMemberDigest: new string('a', 64),
                        PbrContentDigest: null!)));

        Assert.Equal("invalid_request", exception.Code);
        Assert.Equal(
            Tripo.Bridge.HostImportJournal.PreparedState,
            journal.Current?.State);
    }

    [Fact]
    public void IncompleteTailFallsBackToPreparedAndStillFailsClosed()
    {
        using TemporaryDataRoot root = new();
        Tripo.Bridge.HostImportJournalIdentity identity = Identity();
        using (Tripo.Bridge.HostImportJournal journal =
               Tripo.Bridge.HostImportJournal.Open(identity))
        {
            journal.RecordPrepared();
        }

        string path = JournalPath(root.Path, identity);
        File.AppendAllText(path, """{"schemaVersion":1""");

        using Tripo.Bridge.HostImportJournal reopened =
            Tripo.Bridge.HostImportJournal.Open(identity);
        Assert.Equal(
            Tripo.Bridge.HostImportJournal.PreparedState,
            reopened.Current?.State);
        Tripo.Bridge.BridgeCallException exception =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(
                reopened.RecordPrepared);
        Assert.Equal(
            Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
            exception.Code);
    }

    [Fact]
    public void EmptyOrCorruptExistingJournalFailsClosed()
    {
        using TemporaryDataRoot root = new();
        Tripo.Bridge.HostImportJournalIdentity identity = Identity();
        string directory = Path.GetDirectoryName(
            JournalPath(root.Path, identity))!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(JournalPath(root.Path, identity), string.Empty);

        Tripo.Bridge.BridgeCallException emptyException =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.HostImportJournal.Open(identity));
        Assert.Equal(
            Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
            emptyException.Code);

        File.WriteAllText(
            JournalPath(root.Path, identity),
            "{\"not\":\"a journal\"}\n");

        Tripo.Bridge.BridgeCallException exception =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.HostImportJournal.Open(identity));
        Assert.Equal(
            Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
            exception.Code);
    }

    [Fact]
    public void ActiveWriterExcludesASecondWriter()
    {
        using TemporaryDataRoot root = new();
        Tripo.Bridge.HostImportJournalIdentity identity = Identity();
        using Tripo.Bridge.HostImportJournal first =
            Tripo.Bridge.HostImportJournal.Open(identity);

        Tripo.Bridge.BridgeCallException exception =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.HostImportJournal.Open(identity));

        Assert.Equal(
            Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
            exception.Code);
    }

    [Fact]
    public void DisposingBeforePreparedRemovesTheUnusedJournal()
    {
        using TemporaryDataRoot root = new();
        Tripo.Bridge.HostImportJournalIdentity identity = Identity();
        using (Tripo.Bridge.HostImportJournal journal =
               Tripo.Bridge.HostImportJournal.Open(identity))
        {
            Assert.Null(journal.Current);
        }

        Assert.False(File.Exists(JournalPath(root.Path, identity)));
        using Tripo.Bridge.HostImportJournal retry =
            Tripo.Bridge.HostImportJournal.Open(identity);
        retry.RecordPrepared();
        Assert.Equal(
            Tripo.Bridge.HostImportJournal.PreparedState,
            retry.Current?.State);
    }

    [Fact]
    public void RecordCountLimitPreventsAnAppendThatReopenWouldReject()
    {
        using TemporaryDataRoot root = new();
        Tripo.Bridge.HostImportJournalIdentity identity = Identity();
        using Tripo.Bridge.HostImportJournal journal =
            Tripo.Bridge.HostImportJournal.Open(identity);
        for (int index = 0; index < 16; index++)
        {
            journal.RecordPrepared();
            journal.RecordAbortedBeforeImport();
        }

        Tripo.Bridge.BridgeCallException exception =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(
                journal.RecordPrepared);

        Assert.Equal(
            Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
            exception.Code);
    }

    [Fact]
    public void IncompleteTailNeverAuthorizesAFurtherAppend()
    {
        using TemporaryDataRoot root = new();
        Tripo.Bridge.HostImportJournalIdentity identity = Identity();
        using (Tripo.Bridge.HostImportJournal first =
               Tripo.Bridge.HostImportJournal.Open(identity))
        {
            first.RecordPrepared();
            first.RecordAbortedBeforeImport();
        }

        string path = JournalPath(root.Path, identity);
        File.AppendAllText(path, """{"schemaVersion":1""");

        using Tripo.Bridge.HostImportJournal reopened =
            Tripo.Bridge.HostImportJournal.Open(identity);
        Tripo.Bridge.BridgeCallException exception =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(
                reopened.RecordPrepared);

        Assert.Equal(
            Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
            exception.Code);
    }

    [Fact]
    public void IncompleteTailAfterCommitFailsClosedOnReopen()
    {
        using TemporaryDataRoot root = new();
        Tripo.Bridge.HostImportJournalIdentity identity = Identity();
        using (Tripo.Bridge.HostImportJournal first =
               Tripo.Bridge.HostImportJournal.Open(identity))
        {
            first.RecordPrepared();
            first.RecordCommitted(new Tripo.Bridge.HostImportCommitReceipt(
                Guid.NewGuid().ToString("D"),
                VertexCount: 3,
                TriangleCount: 1,
                MaterialCount: 1,
                TextureCount: 1,
                DefinitionMemberCount: 1,
                DefinitionMemberDigest: new string('a', 64),
                PbrContentDigest: new string('b', 64)));
        }

        File.AppendAllText(
            JournalPath(root.Path, identity),
            """{"schemaVersion":1""");

        Tripo.Bridge.BridgeCallException exception =
            Assert.Throws<Tripo.Bridge.BridgeCallException>(
                () => Tripo.Bridge.HostImportJournal.Open(identity));

        Assert.Equal(
            Tripo.Bridge.BridgeConstants.MutationStateUncertainError,
            exception.Code);
    }

    private static Tripo.Bridge.HostImportJournalIdentity Identity() =>
        new(
            "rhino",
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            new string('f', 64),
            new string('a', 64),
            new string('c', 64),
            EntryByteLength: 64);

    private static string JournalPath(
        string root,
        Tripo.Bridge.HostImportJournalIdentity identity) =>
        Path.Combine(
            root,
            "host-imports",
            identity.Host,
            identity.OperationId + ".jsonl");
}
