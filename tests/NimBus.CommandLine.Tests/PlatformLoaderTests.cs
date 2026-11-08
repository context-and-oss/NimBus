#pragma warning disable CA1707, CA2007
using NimBus.Core;
using Xunit;

namespace NimBus.CommandLine.Tests;

// PlatformLoader resolves a public parameterless IPlatform from a host assembly so
// `nb catalog export --assembly <path>` documents a real integration platform instead
// of the built-in sample. Mirrors AsyncApiProviderLoader's discovery and error style.
public sealed class PlatformLoaderTests
{
    // CreateFactory backs `nb topology apply --assembly` (ADR-015): without an assembly
    // the CLI can only provision the catalog compiled into it, which is nobody's catalog
    // but ours.
    [Fact]
    public void CreateFactory_WithoutAssembly_UsesTheBuiltInPlatform()
    {
        var platform = PlatformLoader.CreateFactory(null)();

        Assert.IsType<PlatformConfiguration>(platform);
    }

    [Fact]
    public void CreateFactory_WithAssembly_LoadsThatCatalog()
    {
        var assemblyPath = typeof(PlatformLoaderTests).Assembly.Location;

        var platform = PlatformLoader.CreateFactory(assemblyPath, nameof(PublicLoaderTestPlatform))();

        Assert.IsType<PublicLoaderTestPlatform>(platform);
    }

    [Fact]
    public void CreateFactory_DefersLoadingUntilInvoked()
    {
        // Option parsing must not fail on a bad path — the command reports it.
        var factory = PlatformLoader.CreateFactory("does-not-exist.dll");

        Assert.Throws<FileNotFoundException>(() => factory());
    }

    [Fact]
    public void Resolve_ByTypeName_ReturnsPlatformInstance()
    {
        var platform = PlatformLoader.Resolve(typeof(PlatformLoaderTests).Assembly, nameof(PublicLoaderTestPlatform));

        Assert.IsType<PublicLoaderTestPlatform>(platform);
    }

    [Fact]
    public void Resolve_ByFullTypeName_ReturnsPlatformInstance()
    {
        var platform = PlatformLoader.Resolve(
            typeof(PlatformLoaderTests).Assembly, typeof(PublicLoaderTestPlatform).FullName);

        Assert.IsType<PublicLoaderTestPlatform>(platform);
    }

    [Fact]
    public void Resolve_UnknownTypeName_ThrowsWithGuidance()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlatformLoader.Resolve(typeof(PlatformLoaderTests).Assembly, "NoSuchPlatform"));

        Assert.Contains("NoSuchPlatform", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IPlatform", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_MultipleCandidatesWithoutName_ThrowsListingThem()
    {
        // This test assembly deliberately exposes two public parameterless platforms.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlatformLoader.Resolve(typeof(PlatformLoaderTests).Assembly, null));

        Assert.Contains(nameof(PublicLoaderTestPlatform), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SecondPublicLoaderTestPlatform), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            PlatformLoader.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll")));
    }
}

/// <summary>Public parameterless platform used by <see cref="PlatformLoaderTests"/>.</summary>
public sealed class PublicLoaderTestPlatform : Platform
{
}

/// <summary>Second public platform proving ambiguity handling in <see cref="PlatformLoaderTests"/>.</summary>
public sealed class SecondPublicLoaderTestPlatform : Platform
{
}
