#pragma warning disable CA1707, CA2007
using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

public class ProcessResultTests
{
    [Fact]
    public void Succeeded_IsTrueWhenExitCodeIsZero()
    {
        var result = new ProcessResult(0, "output", "");
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(255)]
    public void Succeeded_IsFalseWhenExitCodeIsNonZero(int exitCode)
    {
        var result = new ProcessResult(exitCode, "", "error");
        Assert.False(result.Succeeded);
    }
}
