#pragma warning disable CA1707, CA2007
using System.IO.Compression;
using System.Net;
using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// Covers how `nb deploy apps` obtains the release artifacts without a repository clone
/// (ADR-015): the cache, the flat-container download, and — most importantly — that a
/// missing package fails loudly instead of silently deploying something else.
/// </summary>
public sealed class PackagedArtifactSourceTests : IDisposable
{
    private const string Version = "9.9.9";

    private readonly string _cache;

    public PackagedArtifactSourceTests()
    {
        _cache = Path.Combine(Path.GetTempPath(), "NimBusTests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    [Fact]
    public async Task DownloadsAndExtractsBothArtifacts()
    {
        var handler = new StubHandler(BuildPackage(("resolver.zip", "resolver-bits"), ("webapp.zip", "webapp-bits")));
        var source = CreateSource(handler);

        var resolver = await source.GetResolverZipAsync(CancellationToken.None);
        var webApp = await source.GetWebAppZipAsync(CancellationToken.None);

        Assert.Equal("resolver-bits", File.ReadAllText(resolver));
        Assert.Equal("webapp-bits", File.ReadAllText(webApp));
        // One probe for the feed's service index, then one download. The stub answers the
        // index with the package bytes, so the source falls back to nuget.org's layout —
        // which is what a feed that serves no usable index should produce.
        Assert.Equal(2, handler.Requests);
        Assert.Contains($"/v3-flatcontainer/akaule.nimbus.deploy/{Version}/akaule.nimbus.deploy.{Version}.nupkg", handler.LastUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecondCall_UsesTheCacheInsteadOfDownloadingAgain()
    {
        var handler = new StubHandler(BuildPackage(("resolver.zip", "resolver-bits")));
        var source = CreateSource(handler);

        await source.GetResolverZipAsync(CancellationToken.None);
        var afterFirst = handler.Requests;
        await source.GetResolverZipAsync(CancellationToken.None);

        // The point is that the cache hit costs nothing more, whatever the first call spent.
        Assert.Equal(afterFirst, handler.Requests);
    }

    [Fact]
    public async Task MissingPackage_FailsWithAnActionableMessage()
    {
        var source = CreateSource(new StubHandler(HttpStatusCode.NotFound));

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => source.GetResolverZipAsync(CancellationToken.None));

        // Must name the version, and must never quietly fall back to a source tree that
        // could be a different revision than this CLI.
        Assert.Contains(Version, exception.Message, StringComparison.Ordinal);
        Assert.Contains("--from-source", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeniedFeed_PointsAtTheTokenVariable()
    {
        var source = CreateSource(new StubHandler(HttpStatusCode.Unauthorized));

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => source.GetWebAppZipAsync(CancellationToken.None));

        Assert.Contains(PackagedArtifactSource.FeedTokenEnvironmentVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageWithoutTheRequestedArtifact_SaysSo()
    {
        var source = CreateSource(new StubHandler(BuildPackage(("resolver.zip", "resolver-bits"))));

        var exception = await Assert.ThrowsAsync<CommandException>(
            () => source.GetWebAppZipAsync(CancellationToken.None));

        Assert.Contains("webapp.zip", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedDownload_LeavesNoPartialPackageBehind()
    {
        var source = CreateSource(new StubHandler(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<CommandException>(() => source.GetResolverZipAsync(CancellationToken.None));

        Assert.False(Directory.Exists(_cache) && Directory.EnumerateFiles(_cache, "*.tmp").Any());
    }

    [Fact]
    public async Task Version_IsTheCliVersion_NotAGitTag()
    {
        var source = CreateSource(new StubHandler(BuildPackage(("resolver.zip", "x"))));

        Assert.Equal(Version, await source.GetVersionAsync(CancellationToken.None));
    }

    private PackagedArtifactSource CreateSource(StubHandler handler) =>
        new(new HttpClient(handler), "https://feed.example", Version, _cache);

    /// <summary>Builds a .nupkg-shaped zip with the artifacts under content/.</summary>
    private static byte[] BuildPackage(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry($"content/{name}").Open());
                writer.Write(content);
            }

            // A real .nupkg carries metadata alongside the payload; it must be ignored.
            using var nuspec = new StreamWriter(archive.CreateEntry("akaule.nimbus.deploy.nuspec").Open());
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

        public StubHandler(HttpStatusCode status)
        {
            _status = status;
        }

        public int Requests { get; private set; }

        public string LastUrl { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastUrl = request.RequestUri!.ToString();

            var response = new HttpResponseMessage(_status);
            if (_payload is not null)
            {
                response.Content = new ByteArrayContent(_payload);
            }

            return Task.FromResult(response);
        }
    }
}
