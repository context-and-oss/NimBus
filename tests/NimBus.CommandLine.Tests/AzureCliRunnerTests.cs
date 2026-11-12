#pragma warning disable CA1707, CA2007
using Xunit;

namespace NimBus.CommandLine.Tests;

public sealed class AzureCliRunnerTests
{
    [Fact]
    public void ResolveProcessCommand_UsesPlatformAppropriateAzExecutable()
    {
        var arguments = new[] { "--only-show-errors", "account", "show" };

        var (fileName, resolvedArguments) = AzureCliRunner.ResolveProcessCommand(arguments);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd.exe", fileName);
            Assert.Equal(new[] { "/d", "/c", "az.cmd --only-show-errors account show" }, resolvedArguments);
            return;
        }

        Assert.Equal("az", fileName);
        Assert.Equal(arguments, resolvedArguments);
    }

    [Fact]
    public void ResolveProcessCommand_QuotesWindowsArgumentsThatNeedIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var arguments = new[] { "--template-file", @"C:\git\Nim Bus\deploy\main.bicep" };

        var (_, resolvedArguments) = AzureCliRunner.ResolveProcessCommand(arguments);

        Assert.Equal(new[] { "/d", "/c", "az.cmd --template-file \"C:\\git\\Nim Bus\\deploy\\main.bicep\"" }, resolvedArguments);
    }
}
