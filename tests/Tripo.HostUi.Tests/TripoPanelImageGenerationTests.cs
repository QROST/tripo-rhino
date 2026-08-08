using Xunit;

namespace Tripo.HostUi.Tests;

public sealed class TripoPanelImageGenerationTests
{
    private static Tripo.HostUi.TripoPanelSession CreateSession(
        TripoPanelSessionTests.FakeHostControlClient client) =>
        TripoPanelSessionTests.CreateSession(client);

    private static Tripo.Bridge.StagedImageTransfer ValidImage() =>
        new(
            "11111111-1111-1111-8111-111111111111",
            new string('a', 64),
            1024,
            "image/png");

    [Fact]
    public async Task UnconfirmedImageGenerationMakesNoPaidCallButPreparesOperation()
    {
        TripoPanelSessionTests.FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();

        Tripo.HostUi.PreparedImageGeneration prepared =
            session.PrepareImageGeneration(
                ValidImage(),
                20_000,
                withMaterials: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DispatchPreparedImageGenerationAsync(
                userConfirmedExternalCost: false));

        Assert.Equal(
            prepared.OperationId,
            session.State.PreparedImageGeneration?.OperationId);
        Assert.Equal(0, client.CreateImageCalls);
    }

    [Fact]
    public async Task ImageGenerationDispatchesCreateImageTaskWithStagedDescriptor()
    {
        TripoPanelSessionTests.FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        Tripo.Bridge.StagedImageTransfer image = ValidImage();
        Tripo.HostUi.PreparedImageGeneration prepared =
            session.PrepareImageGeneration(image, 15_000, withMaterials: false);

        await session.DispatchPreparedImageGenerationAsync(
            userConfirmedExternalCost: true);

        Assert.Equal(1, client.CreateImageCalls);
        Tripo.Bridge.HostControlCreateImageTaskRequest request =
            Assert.Single(client.ImageRequests);
        Assert.Equal(prepared.OperationId, request.OperationId);
        Assert.Equal(image.Sha256, request.Image.Sha256);
        Assert.Equal(15_000, request.FaceLimit);
        Assert.False(request.WithMaterials);
        Assert.Equal(
            "task_image456",
            session.State.ImageGenerationReceipt?.TaskId);
        // A successful image dispatch must surface as a durable generation task
        // exactly like a text dispatch, so conversion/import/recovery work.
        Assert.True(session.State.HasDurableGenerationTask);
    }

    [Fact]
    public async Task PreparingImageWhileWorkflowIsActiveIsRejected()
    {
        TripoPanelSessionTests.FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareGeneration("a chair", 10_000, withMaterials: false);

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(
            () => session.PrepareImageGeneration(
                ValidImage(),
                10_000,
                withMaterials: false));
        Assert.Contains("new workflow", exception.Message);
    }

    [Fact]
    public async Task ResetWorkflowClearsPreparedImageGeneration()
    {
        TripoPanelSessionTests.FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareImageGeneration(ValidImage(), 10_000, withMaterials: false);
        Assert.NotNull(session.State.PreparedImageGeneration);

        session.ResetWorkflow();

        Assert.Null(session.State.PreparedImageGeneration);
        Assert.False(session.State.HasWorkflowState);
    }

    [Fact]
    public async Task ImageGenerationTaskIdResolvesForStatusRefresh()
    {
        TripoPanelSessionTests.FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();
        session.PrepareImageGeneration(ValidImage(), 10_000, withMaterials: false);
        await session.DispatchPreparedImageGenerationAsync(
            userConfirmedExternalCost: true);

        await session.RefreshGenerationStatusAsync();

        Assert.Equal(1, client.TaskStatusCalls);
        Assert.Equal(
            "task_image456",
            session.State.GenerationStatus?.TaskId);
        Assert.Equal(
            "image_to_model",
            session.State.GenerationStatus?.Type);
    }

    [Fact]
    public async Task NullImageIsRejectedBeforeAnyStateMutation()
    {
        TripoPanelSessionTests.FakeHostControlClient client = new();
        await using Tripo.HostUi.TripoPanelSession session =
            CreateSession(client);
        await session.ConnectAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Task.FromResult(
                session.PrepareImageGeneration(null!, 10_000, false)));
    }
}
