using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace NimBus.CommandLine;

/// <summary>
/// Supplies the deployment zips `nb deploy apps` hands to the Azure CLI. Both
/// implementations produce the same artifact — a zip of a published application — so the
/// deployment code below them is identical whether the bits were downloaded or built here.
/// </summary>
internal interface IDeploymentArtifactSource
{
    Task<string> GetResolverZipAsync(CancellationToken cancellationToken);

    Task<string> GetWebAppZipAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Version stamped on the deployed applications, for logging. Null when the source
    /// cannot determine one.
    /// </summary>
    Task<string?> GetVersionAsync(CancellationToken cancellationToken);
}

internal static class DeploymentArtifactSource
{
    private static readonly HttpClient Http = new();

    /// <summary>
    /// Chooses where the deployment zips come from. Released artifacts are the default;
    /// source builds are opt-in via <c>--from-source</c> or an explicit <c>--repo-root</c>.
    /// Merely running inside a clone does not switch modes — deploying a working tree has
    /// to be asked for, or a routine deployment from a developer's machine would ship
    /// whatever happened to be checked out.
    /// </summary>
    public static IDeploymentArtifactSource Create(
        CommandContext context,
        bool fromSource,
        bool repoRootSpecified,
        string? configuration,
        string solutionId,
        string environment)
    {
        if (fromSource || repoRootSpecified)
        {
            var publishRoot = Path.Combine(
                Path.GetTempPath(),
                "nb",
                $"{NamingConventions.NormalizePart(solutionId)}-{NamingConventions.NormalizePart(environment)}",
                DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture));

            CliOutput.WriteLine("Building the applications from source.");
            return new SourceBuildArtifactSource(context, configuration ?? "Release", publishRoot);
        }

        if (!string.IsNullOrWhiteSpace(configuration))
        {
            CliOutput.WriteLine($"Ignoring --configuration '{configuration}': released artifacts are prebuilt. Pass --from-source to build locally.");
        }

        return new PackagedArtifactSource(Http);
    }
}

/// <summary>
/// Downloads the prebuilt applications published for this CLI's own version (ADR-015),
/// so deployment needs no repository clone, no .NET SDK and no Node.js — and ships the
/// exact bits that were built and tested for the release.
/// </summary>
internal sealed class PackagedArtifactSource : IDeploymentArtifactSource
{
    internal const string PackageId = "akaule.nimbus.deploy";
    internal const string FeedEnvironmentVariable = "NIMBUS_ARTIFACT_FEED";
    internal const string FeedTokenEnvironmentVariable = "NIMBUS_ARTIFACT_FEED_TOKEN";

    private readonly NuGetPackageSource _packages;
    private readonly string _version;
    private readonly string _cacheDirectory;

    public PackagedArtifactSource(HttpClient http, string? feed = null, string? version = null, string? cacheDirectory = null)
    {
        _version = version ?? BicepTemplateProvider.ResolveVersion();
        _packages = new NuGetPackageSource(
            http,
            feed ?? Environment.GetEnvironmentVariable(FeedEnvironmentVariable),
            Environment.GetEnvironmentVariable(FeedTokenEnvironmentVariable),
            FeedTokenEnvironmentVariable);
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nimbus", "artifacts", _version);
    }

    public Task<string> GetResolverZipAsync(CancellationToken cancellationToken) =>
        GetArtifactAsync("resolver.zip", cancellationToken);

    public Task<string> GetWebAppZipAsync(CancellationToken cancellationToken) =>
        GetArtifactAsync("webapp.zip", cancellationToken);

    // The release artifacts are stamped at build time, so the CLI's own version is the
    // version being deployed — no git tag lookup, and no way for the two to disagree.
    public Task<string?> GetVersionAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(_version);

    private async Task<string> GetArtifactAsync(string fileName, CancellationToken cancellationToken)
    {
        var root = await _packages.EnsureExtractedAsync(
            PackageId,
            _version,
            _cacheDirectory,
            feed => $"No deployment artifacts published for NimBus {_version} on {feed}. Install a CLI version that has them (dotnet tool update --global Akaule.NimBus.CommandLine), point {FeedEnvironmentVariable} at a feed that mirrors them, or build from source with --from-source --repo-root <path>.",
            cancellationToken).ConfigureAwait(false);

        var artifact = Path.Combine(root, "content", fileName);
        if (!File.Exists(artifact))
        {
            throw new CommandException(
                $"The NimBus {_version} deployment package does not contain '{fileName}'. This usually means the package was built by an older release process; deploy from source with --from-source --repo-root <path> instead.");
        }

        return artifact;
    }
}

/// <summary>
/// Builds the applications from a repository clone — the pre-ADR-015 behaviour, kept as
/// the developer override for testing unreleased changes.
/// </summary>
internal sealed class SourceBuildArtifactSource : IDeploymentArtifactSource
{
    private static readonly string[] LatestTagArguments = ["describe", "--tags", "--abbrev=0"];

    private readonly CommandContext _context;
    private readonly string _configuration;
    private readonly ProcessRunner _processRunner = new();
    private readonly string _publishRoot;
    private string? _version;
    private bool _versionResolved;

    public SourceBuildArtifactSource(CommandContext context, string configuration, string publishRoot)
    {
        _context = context;
        _configuration = configuration;
        _publishRoot = publishRoot;
    }

    public async Task<string> GetResolverZipAsync(CancellationToken cancellationToken) =>
        await BuildAsync(_context.ResolverProjectPath, "resolver", "resolver.zip", cancellationToken).ConfigureAwait(false);

    public async Task<string> GetWebAppZipAsync(CancellationToken cancellationToken) =>
        await BuildAsync(_context.WebAppProjectPath, "webapp", "webapp.zip", cancellationToken).ConfigureAwait(false);

    private async Task<string> BuildAsync(string projectPath, string directoryName, string zipFileName, CancellationToken cancellationToken)
    {
        var publishDirectory = Path.Combine(_publishRoot, directoryName);
        Directory.CreateDirectory(publishDirectory);
        await PublishAsync(projectPath, publishDirectory, await GetVersionAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return PackagePublishOutput(publishDirectory, zipFileName);
    }

    private async Task PublishAsync(string projectPath, string outputPath, string? version, CancellationToken cancellationToken)
    {
        CliOutput.WriteLine($"Publishing '{projectPath}'...");
        var arguments = new List<string>
        {
            "publish",
            projectPath,
            "--configuration", _configuration,
            "--output", outputPath,
            "--nologo",
        };
        if (version != null)
        {
            arguments.Add($"-p:Version={version}");
        }

        var result = await _processRunner.RunAsync(
            "dotnet",
            arguments,
            _context.RepositoryRoot,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new CommandException($"dotnet publish failed for '{projectPath}'.{Environment.NewLine}{result.StandardError}");
        }
    }

    /// <summary>
    /// Stamps the published assemblies with the latest git tag so the WebApp footer (and
    /// /api/app/stats platformVersion) reports a real version instead of the
    /// Directory.Build.props 0.0.0 placeholder.
    /// </summary>
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        if (_versionResolved) return _version;
        _versionResolved = true;

        try
        {
            // Latest reachable tag (e.g. "v1.2.0"); commits since the tag keep the same
            // base version — the tag is the release marker.
            var result = await _processRunner.RunAsync(
                "git",
                LatestTagArguments,
                _context.RepositoryRoot,
                echoStandardOutput: false,
                cancellationToken).ConfigureAwait(false);

            _version = result.Succeeded && TryNormalizeTagVersion(result.StandardOutput, out var version)
                ? version
                : null;
        }
        catch (CommandException)
        {
            // git not on PATH — version stamping is best-effort, never fatal.
            _version = null;
        }

        return _version;
    }

    /// <summary>
    /// Normalizes a release tag ("v1.2.0", "1.2.0-rc.1") to an MSBuild Version
    /// value. The numeric core must parse as a version; prerelease suffixes pass
    /// through verbatim.
    /// </summary>
    internal static bool TryNormalizeTagVersion(string? tag, out string version)
    {
        version = string.Empty;
        var value = tag?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var core = value.Split('-')[0];
        if (!Version.TryParse(core, out _))
        {
            return false;
        }

        version = value;
        return true;
    }

    private static string PackagePublishOutput(string publishDirectory, string zipFileName)
    {
        var zipPath = Path.Combine(Path.GetDirectoryName(publishDirectory)!, zipFileName);
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(publishDirectory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        CliOutput.WriteLine($"Created deployment package '{zipPath}'.");
        return zipPath;
    }
}
