#pragma warning disable CA1707, CA2007
using System.IO.Compression;
using System.Net;
using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// Covers resolving a customer's event catalog from a NuGet feed (ADR-015) — the path that
/// lets a deployment provision their endpoints and show their platform in the management UI
/// without a NimBus clone or a build of their solution.
/// </summary>
public sealed class PlatformPackageTests : IDisposable
{
    private readonly List<string> _tempDirectories = new();

    public void Dispose()
    {
        foreach (var directory in _tempDirectories.Where(Directory.Exists))
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("Acme.Contracts@1.4.0", "Acme.Contracts", "1.4.0")]
    [InlineData(" Acme.Contracts @ 1.4.0-rc.1 ", "Acme.Contracts", "1.4.0-rc.1")]
    public void ParseReference_SplitsIdAndVersion(string reference, string expectedId, string expectedVersion)
    {
        var (id, version) = PlatformPackage.ParseReference(reference);

        Assert.Equal(expectedId, id);
        Assert.Equal(expectedVersion, version);
    }

    // The catalog decides the Service Bus topology, so a floating version would let a
    // contracts release change routing without a reviewed change.
    [Theory]
    [InlineData("Acme.Contracts")]
    [InlineData("Acme.Contracts@")]
    [InlineData("@1.0.0")]
    [InlineData("Acme@1.0.0@extra")]
    public void ParseReference_RequiresAnExplicitVersion(string reference)
    {
        var exception = Assert.Throws<CommandException>(() => PlatformPackage.ParseReference(reference));

        Assert.Contains("<PackageId>@<Version>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_LoadsThePlatformFromThePackage()
    {
        // The test assembly exposes two platforms, so the type name disambiguates —
        // exactly what --platform is for.
        var package = BuildPackage("net10.0", typeof(PlatformPackageTests).Assembly.Location);

        var resolved = await PlatformPackage.ResolveAsync(
            new HttpClient(new StubHandler(package)),
            "Acme.Contracts@1.4.0",
            "https://feed.example",
            nameof(PublicLoaderTestPlatform),
            CancellationToken.None,
            NewCacheRoot());

        Assert.Equal("Acme.Contracts", resolved.PackageId);
        Assert.Equal("1.4.0", resolved.Version);
        Assert.Contains(nameof(PublicLoaderTestPlatform), resolved.PlatformTypeName, StringComparison.Ordinal);
        Assert.IsType<PublicLoaderTestPlatform>(resolved.CreateFactory()());
    }

    [Fact]
    public async Task ResolveAsync_PrefersNet10OverOlderTargetFrameworks()
    {
        var package = BuildPackage(
            "netstandard2.0",
            typeof(PlatformPackageTests).Assembly.Location,
            alsoUnder: "net10.0");

        var resolved = await PlatformPackage.ResolveAsync(
            new HttpClient(new StubHandler(package)),
            "Acme.Contracts@1.4.0",
            "https://feed.example",
            nameof(PublicLoaderTestPlatform),
            CancellationToken.None,
            NewCacheRoot());

        Assert.Contains($"net10.0{Path.DirectorySeparatorChar}", resolved.PrimaryAssemblyPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_PackageWithoutAPlatform_ExplainsWhatIsRequired()
    {
        // A package that carries no IPlatform at all — the common mistake of pointing at
        // the wrong package in a solution.
        var package = BuildPackageWithoutAssemblies();

        var exception = await Assert.ThrowsAsync<CommandException>(() => PlatformPackage.ResolveAsync(
            new HttpClient(new StubHandler(package)),
            "Acme.Contracts@1.4.0",
            "https://feed.example",
            null,
            CancellationToken.None,
            NewCacheRoot()));

        Assert.Contains("lib/", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_MissingPackage_NamesTheFeedVariable()
    {
        var exception = await Assert.ThrowsAsync<CommandException>(() => PlatformPackage.ResolveAsync(
            new HttpClient(new StubHandler(HttpStatusCode.NotFound)),
            "Acme.Contracts@9.9.9",
            "https://feed.example",
            null,
            CancellationToken.None,
            NewCacheRoot()));

        Assert.Contains("Acme.Contracts", exception.Message, StringComparison.Ordinal);
        Assert.Contains(PlatformPackage.FeedEnvironmentVariable, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Isolated cache per call — a shared one would let one test's package
    /// satisfy another's download and hide what is actually being resolved.</summary>
    private string NewCacheRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "NimBusTests", Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(root);
        return root;
    }

    private static byte[] BuildPackage(string targetFramework, string assemblyPath, string? alsoUnder = null)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntryFromFile(assemblyPath, $"lib/{targetFramework}/{Path.GetFileName(assemblyPath)}");
            if (alsoUnder is not null)
            {
                archive.CreateEntryFromFile(assemblyPath, $"lib/{alsoUnder}/{Path.GetFileName(assemblyPath)}");
            }

            using var nuspec = new StreamWriter(archive.CreateEntry("Acme.Contracts.nuspec").Open());
            nuspec.Write("<package />");
        }

        return buffer.ToArray();
    }

    private static byte[] BuildPackageWithoutAssemblies()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var nuspec = new StreamWriter(archive.CreateEntry("Acme.Contracts.nuspec").Open());
            nuspec.Write("<package />");
        }

        return buffer.ToArray();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[]? _payload;
        private readonly HttpStatusCode _status;

        public StubHandler(byte[] payload)
        {
            _payload = payload;
            _status = HttpStatusCode.OK;
        }

        public StubHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status);
            if (_payload is not null) response.Content = new ByteArrayContent(_payload);
            return Task.FromResult(response);
        }
    }
}
