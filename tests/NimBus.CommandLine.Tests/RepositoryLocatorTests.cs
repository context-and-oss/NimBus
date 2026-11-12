#pragma warning disable CA1707, CA2007
using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

public sealed class RepositoryLocatorTests : IDisposable
{
    private readonly string _tempRoot;

    public RepositoryLocatorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "NimBusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Resolve_AcceptsExplicitPathWhenValid()
    {
        SetupRepoStructure(_tempRoot);

        var result = RepositoryLocator.Resolve(_tempRoot);

        Assert.Equal(Path.GetFullPath(_tempRoot), result);
    }

    [Fact]
    public void Resolve_ThrowsWhenExplicitPathIsNotRepoRoot()
    {
        // No deploy/ or src/ directories
        var exception = Assert.Throws<CommandException>(() => RepositoryLocator.Resolve(_tempRoot));
        Assert.Contains("does not look like", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ThrowsWhenNullAndCurrentDirectoryIsNotRepoRoot()
    {
        var saved = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _tempRoot;
            Assert.Throws<CommandException>(() => RepositoryLocator.Resolve(null));
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    [Fact]
    public void Resolve_FindsRepoRootFromChildDirectory()
    {
        SetupRepoStructure(_tempRoot);
        var childDir = Path.Combine(_tempRoot, "src", "SomeProject");
        Directory.CreateDirectory(childDir);

        var saved = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = childDir;
            var result = RepositoryLocator.Resolve(null);
            Assert.Equal(Path.GetFullPath(_tempRoot), result);
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }

    private static void SetupRepoStructure(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "deploy"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "README.md"), "# Test");
    }
}
