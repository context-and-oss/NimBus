#pragma warning disable CA1707, CA2007
using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

public sealed class CommandContextTests : IDisposable
{
    private readonly string _tempRoot;

    public CommandContextTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "NimBusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "deploy", "bicep"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "src", "NimBus.Resolver"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "src", "NimBus.WebApp"));
        File.WriteAllText(Path.Combine(_tempRoot, "README.md"), "# Test");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Create_SetsRepositoryRoot()
    {
        var context = CommandContext.Create(_tempRoot);
        Assert.Equal(Path.GetFullPath(_tempRoot), context.RepositoryRoot);
    }

    [Fact]
    public void DeployDirectory_IsUnderRepoRoot()
    {
        var context = CommandContext.Create(_tempRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(_tempRoot), "deploy"), context.DeployDirectory);
    }

    [Fact]
    public void SourceDirectory_IsUnderRepoRoot()
    {
        var context = CommandContext.Create(_tempRoot);
        Assert.Equal(Path.Combine(Path.GetFullPath(_tempRoot), "src"), context.SourceDirectory);
    }

    [Fact]
    public void CoreBicepPath_PointsToDeployBicep()
    {
        var context = CommandContext.Create(_tempRoot);
        Assert.EndsWith("deploy.core.bicep", context.CoreBicepPath, StringComparison.Ordinal);
        Assert.StartsWith(context.DeployDirectory, context.CoreBicepPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectPaths_PointToSourceDirectory()
    {
        var context = CommandContext.Create(_tempRoot);
        Assert.StartsWith(context.SourceDirectory, context.ResolverProjectPath, StringComparison.Ordinal);
        Assert.StartsWith(context.SourceDirectory, context.WebAppProjectPath, StringComparison.Ordinal);
        Assert.EndsWith(".csproj", context.ResolverProjectPath, StringComparison.Ordinal);
        Assert.EndsWith(".csproj", context.WebAppProjectPath, StringComparison.Ordinal);
    }
}
