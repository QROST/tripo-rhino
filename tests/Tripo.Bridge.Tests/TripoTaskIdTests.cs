using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class TripoTaskIdTests
{
    [Theory]
    [InlineData("task_abc")]
    [InlineData("task_source123")]
    [InlineData("task_A-Z_0")]
    [InlineData("ef731ad6-aeb0-4950-9a2e-2298359dfaf8")]
    public void KnownTaskIdFormatsAreAccepted(string value)
    {
        Assert.True(Tripo.Bridge.TripoTaskId.IsValid(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("task_ab")]
    [InlineData("task_source123\n")]
    [InlineData("task_source123/../task_other")]
    [InlineData("EF731AD6-AEB0-4950-9A2E-2298359DFAF8")]
    [InlineData("ef731ad6aeb049509a2e2298359dfaf8")]
    [InlineData("{ef731ad6-aeb0-4950-9a2e-2298359dfaf8}")]
    [InlineData("ef731ad6-aeb0-4950-9a2e-2298359dfaf8 ")]
    public void UnsafeOrNoncanonicalTaskIdsAreRejected(string? value)
    {
        Assert.False(Tripo.Bridge.TripoTaskId.IsValid(value));
    }
}
