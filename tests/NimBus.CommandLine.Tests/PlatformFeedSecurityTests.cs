#pragma warning disable CA1707, CA2007
using System.Net;
using System.Text;
using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// Guards the credential and feed-addressing rules for the packages `nb` resolves at
/// deployment time (ADR-015). A token belongs to one feed: it must never follow a URL
/// that came from somewhere else, and the flat-container address must come from the
/// feed's service index rather than nuget.org's path layout.
/// </summary>
public sealed class PlatformFeedSecurityTests : IDisposable
{
    private const string Token = "pat-that-must-not-leak";

    private readonly string _cache;

    public PlatformFeedSecurityTests()
    {
        _cache = Path.Combine(Path.GetTempPath(), "NimBusTests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    // ---- credential scoping -------------------------------------------------

    [Fact]
    public async Task NoFeedConfigured_NeverSendsTheTokenToThePublicDefault()
    {
        // With no feed set the default is nuget.org. A PAT for the customer's private
        // mirror must not be handed to it just because the variable happens to be set.
        var handler = new StubHandler();
        var source = new NuGetPackageSource(new HttpClient(handler), feed: null, token: Token, "NIMBUS_ARTIFACT_FEED_TOKEN");

        await Assert.ThrowsAsync<CommandException>(() => Download(source));

        Assert.All(handler.AuthorizationHeaders, header => Assert.Null(header));
    }

    [Fact]
    public async Task ConfiguredFeed_SendsTheToken()
    {
        var handler = new StubHandler();
        var source = new NuGetPackageSource(new HttpClient(handler), "https://pkgs.example/feed", Token, "NIMBUS_PLATFORM_FEED_TOKEN");

        await Assert.ThrowsAsync<CommandException>(() => Download(source));

        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"nb:{Token}"));
        Assert.Contains(handler.AuthorizationHeaders, header => header == $"Basic {expected}");
    }

    [Fact]
    public async Task PlainHttpFeed_NeverSendsTheToken()
    {
        var handler = new StubHandler();
        var source = new NuGetPackageSource(new HttpClient(handler), "http://pkgs.example/feed", Token, "NIMBUS_PLATFORM_FEED_TOKEN");

        await Assert.ThrowsAsync<CommandException>(() => Download(source));

        Assert.All(handler.AuthorizationHeaders, header => Assert.Null(header));
    }

    [Fact]
    public void ExplicitPlatformFeed_DoesNotBorrowTheArtifactToken()
    {
        // The documented leak: NIMBUS_ARTIFACT_FEED_TOKEN is exported for a private NimBus
        // mirror, then --platform-feed points somewhere else entirely.
        var resolved = PlatformPackage.ResolveFeedCredentials(
            explicitFeed: "https://api.nuget.org",
            platformFeed: null,
            platformToken: null,
            artifactFeed: "https://pkgs.dev.azure.com/acme/_packaging/mirror/nuget",
            artifactToken: Token);

        Assert.Equal("https://api.nuget.org", resolved.Feed);
        Assert.Null(resolved.Token);
    }

    [Fact]
    public void PlatformFeedVariable_UsesThePlatformTokenOnly()
    {
        var resolved = PlatformPackage.ResolveFeedCredentials(
            explicitFeed: null,
            platformFeed: "https://pkgs.example/platform",
            platformToken: "platform-token",
            artifactFeed: "https://pkgs.example/artifacts",
            artifactToken: Token);

        Assert.Equal("https://pkgs.example/platform", resolved.Feed);
        Assert.Equal("platform-token", resolved.Token);
    }

    [Fact]
    public void ArtifactFeedFallback_KeepsTheArtifactTokenPairedWithIt()
    {
        // Falling back to the artifact feed is intended; the pair simply has to travel together.
        var resolved = PlatformPackage.ResolveFeedCredentials(
            explicitFeed: null,
            platformFeed: null,
            platformToken: null,
            artifactFeed: "https://pkgs.example/artifacts",
            artifactToken: Token);

        Assert.Equal("https://pkgs.example/artifacts", resolved.Feed);
        Assert.Equal(Token, resolved.Token);
        Assert.Equal(PackagedArtifactSource.FeedTokenEnvironmentVariable, resolved.TokenVariable);
    }

    // ---- feed addressing ----------------------------------------------------

    [Fact]
    public async Task DownloadUrl_ComesFromTheServiceIndex_NotNugetOrgsLayout()
    {
        // Azure Artifacts serves PackageBaseAddress at /nuget/v3/flat2, not /v3-flatcontainer.
        const string feed = "https://pkgs.dev.azure.com/acme/_packaging/contracts/nuget";
        var handler = new StubHandler(
            serviceIndex: $$"""
            {"version":"3.0.0","resources":[
              {"@id":"{{feed}}/v3/registrations2/","@type":"RegistrationsBaseUrl/3.0.0"},
              {"@id":"{{feed}}/v3/flat2/","@type":"PackageBaseAddress/3.0.0"}
            ]}
            """);
        var source = new NuGetPackageSource(new HttpClient(handler), feed, token: null, "NIMBUS_PLATFORM_FEED_TOKEN");

        await Assert.ThrowsAsync<CommandException>(() => Download(source));

        Assert.Contains(
            $"{feed}/v3/flat2/acme.contracts/1.4.0/acme.contracts.1.4.0.nupkg",
            handler.Urls);
    }

    [Fact]
    public async Task FeedWithoutSuppliedIndex_FallsBackToTheFlatContainerPath()
    {
        // A mirror that does not serve an index still works when it uses nuget.org's layout.
        var handler = new StubHandler(indexStatus: HttpStatusCode.NotFound);
        var source = new NuGetPackageSource(new HttpClient(handler), "https://pkgs.example/feed", token: null, "NIMBUS_PLATFORM_FEED_TOKEN");

        await Assert.ThrowsAsync<CommandException>(() => Download(source));

        Assert.Contains(
            "https://pkgs.example/feed/v3-flatcontainer/acme.contracts/1.4.0/acme.contracts.1.4.0.nupkg",
            handler.Urls);
    }

    [Theory]
    [InlineData("1.4.0", "1.4.0")]
    [InlineData("1.4.0.0", "1.4.0")]
    [InlineData("1.04.0", "1.4.0")]
    [InlineData("1.4", "1.4.0")]
    [InlineData("1.4.0-RC1", "1.4.0-rc1")]
    [InlineData("1.4.0+build.7", "1.4.0")]
    public void Version_IsNuGetNormalizedForTheUrl(string supplied, string expected)
    {
        Assert.Equal(expected, NuGetPackageSource.NormalizeVersion(supplied));
    }

    private static Task Download(NuGetPackageSource source) =>
        source.EnsureExtractedAsync(
            "acme.contracts",
            "1.4.0",
            Path.Combine(Path.GetTempPath(), "NimBusTests", Guid.NewGuid().ToString("N")),
            feed => $"not found on {feed}",
            CancellationToken.None);

    /// <summary>
    /// Answers the service index when one is configured and 404s the package download, so
    /// every test observes the requests the source made without needing a real .nupkg.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string? _serviceIndex;
        private readonly HttpStatusCode _indexStatus;

        public StubHandler(string? serviceIndex = null, HttpStatusCode indexStatus = HttpStatusCode.NotFound)
        {
            _serviceIndex = serviceIndex;
            _indexStatus = indexStatus;
        }

        public List<string> Urls { get; } = new();

        public List<string?> AuthorizationHeaders { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());

            if (request.RequestUri!.AbsolutePath.EndsWith("/v3/index.json", StringComparison.Ordinal))
            {
                return Task.FromResult(_serviceIndex is null
                    ? new HttpResponseMessage(_indexStatus)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_serviceIndex) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
