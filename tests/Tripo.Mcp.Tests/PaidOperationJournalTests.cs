using System.Text;
using Xunit;

namespace Tripo.Mcp.Tests;

public sealed class PaidOperationJournalTests
{
    [Fact]
    public async Task PersistedTaskIdReplaysAcrossJournalInstances()
    {
        using TemporaryJournalRoot root = new();
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.PaidOperationDescriptor descriptor = TextDescriptor(operationId);
        Tripo.Mcp.PaidOperationJournal firstJournal = new(root.Path);

        await using (Tripo.Mcp.PaidOperationLease lease =
                     await firstJournal.AcquireAsync(
                         descriptor,
                         CancellationToken.None))
        {
            await lease.BeforeSendAsync(CancellationToken.None);
            await lease.TaskIdReceivedAsync("task_source123");
        }

        Tripo.Mcp.PaidOperationJournal restartedJournal = new(root.Path);
        await using Tripo.Mcp.PaidOperationLease replay =
            await restartedJournal.AcquireAsync(
                descriptor,
                CancellationToken.None,
                requireExistingOperation: true);

        Assert.Equal("task_id_persisted", replay.Status.State);
        Assert.True(replay.Status.TaskIdDurable);
        Assert.Equal("task_source123", replay.Status.CreatedTaskId);
    }

    [Fact]
    public async Task RecoveredUiRetryRefusesAReplacementMissingJournal()
    {
        using TemporaryJournalRoot root = new(create: false);
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.PaidOperationJournal journal = new(root.Path);

        Tripo.Mcp.TripoWorkflowException exception =
            await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
                () => journal.AcquireAsync(
                    TextDescriptor(operationId),
                    CancellationToken.None,
                    requireExistingOperation: true));

        Assert.Contains(
            "has no local journal",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(File.Exists(JournalPath(root.Path, operationId)));
    }

    [Fact]
    public async Task SameOperationWithDifferentFingerprintFailsClosed()
    {
        using TemporaryJournalRoot root = new();
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.PaidOperationJournal journal = new(root.Path);
        await using (Tripo.Mcp.PaidOperationLease lease =
                     await journal.AcquireAsync(
                         TextDescriptor(operationId, 'a'),
                         CancellationToken.None))
        {
        }

        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => journal.AcquireAsync(
                TextDescriptor(operationId, 'b'),
                CancellationToken.None));
    }

    [Fact]
    public void UppercaseFingerprintFailsBeforeJournalCreation()
    {
        Assert.Throws<Tripo.Mcp.TripoWorkflowException>(
            () => Tripo.Mcp.PaidOperationDescriptor.ForTextTask(
                Guid.NewGuid().ToString("D"),
                "11111111-1111-1111-1111-111111111111",
                new string('A', 64)));
    }

    [Fact]
    public async Task InterruptedDispatchBecomesOutcomeUnknown()
    {
        using TemporaryJournalRoot root = new();
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.PaidOperationDescriptor descriptor = TextDescriptor(operationId);
        Tripo.Mcp.PaidOperationJournal journal = new(root.Path);
        await using (Tripo.Mcp.PaidOperationLease lease =
                     await journal.AcquireAsync(
                         descriptor,
                         CancellationToken.None))
        {
            await lease.BeforeSendAsync(CancellationToken.None);
        }

        await using Tripo.Mcp.PaidOperationLease recovered =
            await new Tripo.Mcp.PaidOperationJournal(root.Path).AcquireAsync(
                descriptor,
                CancellationToken.None);

        Assert.Equal("outcome_unknown", recovered.Status.State);
        Assert.True(recovered.Status.MayHaveCreatedRemoteTask);
        Assert.False(recovered.Status.CanResumeCreation);
        Assert.Null(recovered.Status.CreatedTaskId);
    }

    [Fact]
    public async Task PersistedImageFileTokenResumesAcrossJournalInstances()
    {
        using TemporaryJournalRoot root = new();
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.PaidOperationDescriptor descriptor =
            ImageDescriptor(operationId);
        Tripo.Mcp.PaidOperationJournal firstJournal = new(root.Path);

        await using (Tripo.Mcp.PaidOperationLease lease =
                     await firstJournal.AcquireAsync(
                         descriptor,
                         CancellationToken.None))
        {
            await lease.BeforeImageUploadAsync(CancellationToken.None);
            await lease.ImageFileTokenReceivedAsync(
                "file_resume123",
                new string('e', 64));
        }

        await using (Tripo.Mcp.PaidOperationLease resumed =
                     await new Tripo.Mcp.PaidOperationJournal(root.Path)
                         .AcquireAsync(
                             descriptor,
                             CancellationToken.None))
        {
            Assert.Equal(
                "image_file_token_persisted",
                resumed.Status.State);
            Assert.True(resumed.Status.CanResumeCreation);
            Assert.Equal("file_resume123", resumed.FileToken);
            Assert.Equal(
                new string('e', 64),
                resumed.GenerationRequestFingerprint);
            await resumed.BeforeImageGenerationAsync(
                CancellationToken.None);
            await resumed.TaskIdReceivedAsync("task_image123");
        }

        Tripo.Mcp.PaidOperationStatusReceipt status =
            await firstJournal.GetStatusAsync(
                operationId,
                CancellationToken.None);
        Assert.True(status.TaskIdDurable);
        Assert.Equal("task_image123", status.CreatedTaskId);
    }

    [Fact]
    public async Task InterruptedImageUploadBecomesUploadOutcomeUnknown()
    {
        using TemporaryJournalRoot root = new();
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.PaidOperationDescriptor descriptor =
            ImageDescriptor(operationId);
        await using (Tripo.Mcp.PaidOperationLease lease =
                     await new Tripo.Mcp.PaidOperationJournal(root.Path)
                         .AcquireAsync(
                             descriptor,
                             CancellationToken.None))
        {
            await lease.BeforeImageUploadAsync(CancellationToken.None);
        }

        await using Tripo.Mcp.PaidOperationLease recovered =
            await new Tripo.Mcp.PaidOperationJournal(root.Path)
                .AcquireAsync(descriptor, CancellationToken.None);

        Assert.Equal("outcome_unknown", recovered.Status.State);
        Assert.Equal("upload", recovered.Status.FailureStage);
        Assert.False(recovered.Status.MayHaveCreatedRemoteTask);
        Assert.False(recovered.Status.CanResumeCreation);
    }

    [Fact]
    public async Task InterruptedImageGenerationBecomesGenerationOutcomeUnknown()
    {
        using TemporaryJournalRoot root = new();
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.PaidOperationDescriptor descriptor =
            ImageDescriptor(operationId);
        await using (Tripo.Mcp.PaidOperationLease lease =
                     await new Tripo.Mcp.PaidOperationJournal(root.Path)
                         .AcquireAsync(
                             descriptor,
                             CancellationToken.None))
        {
            await lease.BeforeImageUploadAsync(CancellationToken.None);
            await lease.ImageFileTokenReceivedAsync(
                "file_resume123",
                new string('e', 64));
            await lease.BeforeImageGenerationAsync(
                CancellationToken.None);
        }

        await using Tripo.Mcp.PaidOperationLease recovered =
            await new Tripo.Mcp.PaidOperationJournal(root.Path)
                .AcquireAsync(descriptor, CancellationToken.None);

        Assert.Equal("outcome_unknown", recovered.Status.State);
        Assert.Equal("generation", recovered.Status.FailureStage);
        Assert.True(recovered.Status.MayHaveCreatedRemoteTask);
        Assert.False(recovered.Status.CanResumeCreation);
    }

    [Fact]
    public async Task SameImageOperationWithChangedIdentityFailsClosed()
    {
        using TemporaryJournalRoot root = new();
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.PaidOperationJournal journal = new(root.Path);
        await using (Tripo.Mcp.PaidOperationLease lease =
                     await journal.AcquireAsync(
                         ImageDescriptor(operationId),
                         CancellationToken.None))
        {
        }

        Tripo.Bridge.StagedImageTransfer changedImage = new(
            "22222222-2222-2222-2222-222222222222",
            new string('f', 64),
            10,
            "image/png");
        Tripo.Mcp.PaidOperationDescriptor changed =
            Tripo.Mcp.PaidOperationDescriptor.ForImageTask(
                operationId,
                "11111111-1111-1111-1111-111111111111",
                new string('d', 64),
                changedImage);

        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => journal.AcquireAsync(
                changed,
                CancellationToken.None));
    }

    [Fact]
    public async Task ActiveLeaseBlocksCompetitorsAndStatusCannotAuthorizeReplay()
    {
        using TemporaryJournalRoot root = new();
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.PaidOperationJournal journal = new(root.Path);
        Tripo.Mcp.PaidOperationDescriptor descriptor = TextDescriptor(operationId);
        await using Tripo.Mcp.PaidOperationLease active =
            await journal.AcquireAsync(descriptor, CancellationToken.None);

        Tripo.Mcp.PaidOperationStatusReceipt status =
            await journal.GetStatusAsync(operationId, CancellationToken.None);
        Assert.Equal("operation_in_progress", status.State);
        Assert.True(status.OperationInProgress);
        await active.BeforeSendAsync(CancellationToken.None);
        status =
            await journal.GetStatusAsync(operationId, CancellationToken.None);
        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => new Tripo.Mcp.PaidOperationJournal(root.Path).AcquireAsync(
                descriptor,
                CancellationToken.None));

        Assert.Equal("dispatching", status.State);
        Assert.True(status.OperationInProgress);
        Assert.True(status.MayHaveCreatedRemoteTask);
        Assert.False(status.CanResumeCreation);
    }

    [Fact]
    public async Task ActiveImageDispatchStagesExposeInProgressFlag()
    {
        using TemporaryJournalRoot root = new();
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.PaidOperationJournal journal = new(root.Path);
        await using Tripo.Mcp.PaidOperationLease active =
            await journal.AcquireAsync(
                ImageDescriptor(operationId),
                CancellationToken.None);

        await active.BeforeImageUploadAsync(CancellationToken.None);
        Tripo.Mcp.PaidOperationStatusReceipt upload =
            await journal.GetStatusAsync(
                operationId,
                CancellationToken.None);
        Assert.Equal("image_upload_dispatching", upload.State);
        Assert.True(upload.OperationInProgress);

        await active.ImageFileTokenReceivedAsync(
            "file_active123",
            new string('e', 64));
        await active.BeforeImageGenerationAsync(CancellationToken.None);
        Tripo.Mcp.PaidOperationStatusReceipt generation =
            await journal.GetStatusAsync(
                operationId,
                CancellationToken.None);
        Assert.Equal("image_generation_dispatching", generation.State);
        Assert.True(generation.OperationInProgress);
    }

    [Fact]
    public async Task CompleteRecordWithoutFinalNewlineCanContinueSafely()
    {
        using TemporaryJournalRoot root = new();
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.PaidOperationDescriptor descriptor = TextDescriptor(operationId);
        Tripo.Mcp.PaidOperationJournal journal = new(root.Path);
        await using (Tripo.Mcp.PaidOperationLease lease =
                     await journal.AcquireAsync(
                         descriptor,
                         CancellationToken.None))
        {
        }

        string path = JournalPath(root.Path, operationId);
        using (FileStream stream = new(
                   path,
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.None))
        {
            Assert.True(stream.Length > 1);
            stream.SetLength(stream.Length - 1);
            stream.Flush(flushToDisk: true);
        }

        await using (Tripo.Mcp.PaidOperationLease resumed =
                     await new Tripo.Mcp.PaidOperationJournal(root.Path)
                         .AcquireAsync(descriptor, CancellationToken.None))
        {
            await resumed.BeforeSendAsync(CancellationToken.None);
            await resumed.TaskIdReceivedAsync("task_source123");
        }

        Tripo.Mcp.PaidOperationStatusReceipt status =
            await journal.GetStatusAsync(operationId, CancellationToken.None);
        Assert.Equal("task_source123", status.CreatedTaskId);
        Assert.True(status.TaskIdDurable);
    }

    [Fact]
    public async Task TornFinalTailIsTruncatedBeforeTheNextAppend()
    {
        using TemporaryJournalRoot root = new();
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.PaidOperationDescriptor descriptor = TextDescriptor(operationId);
        Tripo.Mcp.PaidOperationJournal journal = new(root.Path);
        await using (Tripo.Mcp.PaidOperationLease lease =
                     await journal.AcquireAsync(
                         descriptor,
                         CancellationToken.None))
        {
        }

        string path = JournalPath(root.Path, operationId);
        await File.AppendAllTextAsync(
            path,
            """{"schemaVersion":""",
            Encoding.UTF8);

        await using (Tripo.Mcp.PaidOperationLease resumed =
                     await new Tripo.Mcp.PaidOperationJournal(root.Path)
                         .AcquireAsync(descriptor, CancellationToken.None))
        {
            await resumed.BeforeSendAsync(CancellationToken.None);
            await resumed.TaskIdReceivedAsync("task_source123");
        }

        Tripo.Mcp.PaidOperationStatusReceipt status =
            await journal.GetStatusAsync(operationId, CancellationToken.None);
        Assert.Equal("task_id_persisted", status.State);
    }

    [Fact]
    public async Task TornInitialPreparedRecordCanBeRecreated()
    {
        using TemporaryJournalRoot root = new();
        string operationId = Guid.NewGuid().ToString("D");
        await File.WriteAllTextAsync(
            JournalPath(root.Path, operationId),
            """{"schemaVersion":""",
            Encoding.UTF8);
        Tripo.Mcp.PaidOperationJournal journal = new(root.Path);

        await using Tripo.Mcp.PaidOperationLease lease =
            await journal.AcquireAsync(
                TextDescriptor(operationId),
                CancellationToken.None);

        Assert.Equal("prepared", lease.Status.State);
        Assert.True(lease.Status.CanResumeCreation);
    }

    [Fact]
    public async Task LegacyV1ChecksumRemainsReadableAndAppendable()
    {
        using TemporaryJournalRoot root = new();
        const string operationId =
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string legacyRecord =
            """
            {"schemaVersion":1,"revision":1,"operationId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","kind":"text_task_creation","requestFingerprint":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","documentSessionId":"11111111-1111-1111-1111-111111111111","state":"prepared","sourceTaskId":null,"createdTaskId":null,"createdAtUtc":"2026-01-02T03:04:05+00:00","updatedAtUtc":"2026-01-02T03:04:05+00:00","failureCode":null,"failureMessage":null,"recordChecksum":"b346f55a075e48fd1f4ab05a72c76986212cd944b5fc482bdbbdf3b37a41e240"}
            """;
        await File.WriteAllBytesAsync(
            JournalPath(root.Path, operationId),
            Encoding.UTF8.GetBytes(legacyRecord.Trim() + "\n"));
        Tripo.Mcp.PaidOperationDescriptor descriptor =
            TextDescriptor(operationId);

        await using (Tripo.Mcp.PaidOperationLease lease =
                     await new Tripo.Mcp.PaidOperationJournal(root.Path)
                         .AcquireAsync(
                             descriptor,
                             CancellationToken.None))
        {
            Assert.Equal("prepared", lease.Status.State);
            await lease.BeforeSendAsync(CancellationToken.None);
            await lease.TaskIdReceivedAsync("task_legacy123");
        }

        Tripo.Mcp.PaidOperationStatusReceipt status =
            await new Tripo.Mcp.PaidOperationJournal(root.Path)
                .GetStatusAsync(
                    operationId,
                    CancellationToken.None);
        Assert.Equal("task_id_persisted", status.State);
        Assert.Equal("task_legacy123", status.CreatedTaskId);
    }

    [Fact]
    public async Task TerminatedCorruptRecordFailsClosed()
    {
        using TemporaryJournalRoot root = new();
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.PaidOperationDescriptor descriptor = TextDescriptor(operationId);
        Tripo.Mcp.PaidOperationJournal journal = new(root.Path);
        await using (Tripo.Mcp.PaidOperationLease lease =
                     await journal.AcquireAsync(
                         descriptor,
                         CancellationToken.None))
        {
        }

        await File.AppendAllTextAsync(
            JournalPath(root.Path, operationId),
            "not-json\n",
            Encoding.UTF8);

        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => new Tripo.Mcp.PaidOperationJournal(root.Path).AcquireAsync(
                descriptor,
                CancellationToken.None));
    }

    [Fact]
    public async Task MissingStatusLookupDoesNotCreateTheJournalDirectory()
    {
        using TemporaryJournalRoot root = new(create: false);
        Tripo.Mcp.PaidOperationJournal journal = new(root.Path);

        await Assert.ThrowsAsync<Tripo.Mcp.TripoWorkflowException>(
            () => journal.GetStatusAsync(
                Guid.NewGuid().ToString("D"),
                CancellationToken.None));

        Assert.False(Directory.Exists(root.Path));
    }

    [Fact]
    public async Task UnixJournalPathsArePrivate()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryJournalRoot root = new(create: false);
        string operationId = Guid.NewGuid().ToString("D");
        Tripo.Mcp.PaidOperationJournal journal = new(root.Path);
        await using (Tripo.Mcp.PaidOperationLease lease =
                     await journal.AcquireAsync(
                         TextDescriptor(operationId),
                         CancellationToken.None))
        {
        }

        Assert.Equal(
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute,
            File.GetUnixFileMode(root.Path));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(JournalPath(root.Path, operationId)));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(
                Path.Combine(root.Path, operationId + ".lock")));
    }

    private static Tripo.Mcp.PaidOperationDescriptor TextDescriptor(
        string operationId,
        char fingerprintCharacter = 'a') =>
        Tripo.Mcp.PaidOperationDescriptor.ForTextTask(
            operationId,
            "11111111-1111-1111-1111-111111111111",
            new string(fingerprintCharacter, 64));

    private static Tripo.Mcp.PaidOperationDescriptor ImageDescriptor(
        string operationId) =>
        Tripo.Mcp.PaidOperationDescriptor.ForImageTask(
            operationId,
            "11111111-1111-1111-1111-111111111111",
            new string('d', 64),
            new Tripo.Bridge.StagedImageTransfer(
                "11111111-1111-1111-1111-111111111111",
                new string('c', 64),
                10,
                "image/png"));

    private static string JournalPath(string root, string operationId) =>
        Path.Combine(root, operationId + ".jsonl");

    private sealed class TemporaryJournalRoot : IDisposable
    {
        public TemporaryJournalRoot(bool create = true)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tripo-journal-tests",
                Guid.NewGuid().ToString("N"));
            if (create)
            {
                Directory.CreateDirectory(Path);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
