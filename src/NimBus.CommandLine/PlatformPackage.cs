using System.Reflection;
using NimBus.Core;

namespace NimBus.CommandLine;

/// <summary>
/// Resolves a customer's event catalog from a NuGet package instead of a local file
/// (ADR-015). The catalog lives in the same feed the customer already publishes their
/// contracts to — usually a private Azure Artifacts feed in the organisation running the
/// deployment — so provisioning their topology and showing their endpoints in the
/// management UI needs neither a NimBus clone nor a build of their solution.
/// </summary>
internal sealed class PlatformPackage
{
    internal const string FeedEnvironmentVariable = "NIMBUS_PLATFORM_FEED";
    internal const string FeedTokenEnvironmentVariable = "NIMBUS_PLATFORM_FEED_TOKEN";

    private PlatformPackage(string packageId, string version, IReadOnlyList<string> assemblyPaths, string primaryAssemblyPath, string platformTypeName)
    {
        PackageId = packageId;
        Version = version;
        AssemblyPaths = assemblyPaths;
        PrimaryAssemblyPath = primaryAssemblyPath;
        PlatformTypeName = platformTypeName;
    }

    public string PackageId { get; }

    public string Version { get; }

    /// <summary>Every assembly in the package's chosen target framework folder.</summary>
    public IReadOnlyList<string> AssemblyPaths { get; }

    /// <summary>The assembly that actually exposes the platform.</summary>
    public string PrimaryAssemblyPath { get; }

    /// <summary>Full name of the resolved <see cref="IPlatform"/> type.</summary>
    public string PlatformTypeName { get; }

    /// <summary>
    /// Parses <c>Id@Version</c>. The version is required rather than floating: the catalog
    /// decides the Service Bus topology, so a deployment that silently picked up a newer
    /// contracts release would change routing without a reviewed change.
    /// </summary>
    internal static (string Id, string Version) ParseReference(string reference)
    {
        var parts = reference.Split('@');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new CommandException(
                $"'{reference}' is not a valid platform package reference. Use <PackageId>@<Version>, for example Acme.Contracts@1.4.0.");
        }

        return (parts[0].Trim(), parts[1].Trim());
    }

    /// <param name="cacheRoot">
    /// Overrides the per-user cache location. Package versions are immutable, so sharing a
    /// cache across deployments is safe in practice; tests override it for isolation.
    /// </param>
    public static async Task<PlatformPackage> ResolveAsync(
        HttpClient http,
        string reference,
        string? feed,
        string? platformTypeName,
        CancellationToken cancellationToken,
        string? cacheRoot = null)
    {
        var (packageId, version) = ParseReference(reference);

        var credentials = ResolveFeedCredentials(
            feed,
            Environment.GetEnvironmentVariable(FeedEnvironmentVariable),
            Environment.GetEnvironmentVariable(FeedTokenEnvironmentVariable),
            Environment.GetEnvironmentVariable(PackagedArtifactSource.FeedEnvironmentVariable),
            Environment.GetEnvironmentVariable(PackagedArtifactSource.FeedTokenEnvironmentVariable));

        var packages = new NuGetPackageSource(
            http,
            credentials.Feed,
            credentials.Token,
            credentials.TokenVariable);

        var cacheDirectory = Path.Combine(
            cacheRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "nimbus", "platform"),
            $"{packageId.ToLowerInvariant()}.{version}");

        var root = await packages.EnsureExtractedAsync(
            packageId.ToLowerInvariant(),
            version,
            cacheDirectory,
            feedUrl => $"Platform package {packageId} {version} was not found on {feedUrl}. Check the id and version, or point {FeedEnvironmentVariable} at the feed that hosts your contracts (with {FeedTokenEnvironmentVariable} when it is private).",
            cancellationToken).ConfigureAwait(false);

        var assemblies = SelectTargetFrameworkAssemblies(root, packageId, version);
        var (primary, typeName) = ResolvePlatformType(assemblies, packageId, version, platformTypeName);

        CliOutput.WriteLine($"Using platform '{typeName}' from {packageId} {version}.");
        return new PlatformPackage(packageId, version, assemblies, primary, typeName);
    }

    /// <summary>
    /// Pairs the feed to contact with the token that belongs to it. A token authenticates
    /// one feed: borrowing the artifact-feed PAT for a platform feed the customer named
    /// separately would send their credential to a host it was never issued for.
    /// </summary>
    /// <remarks>
    /// Falling back to the artifact feed stays supported — a customer mirroring NimBus into
    /// their own feed usually publishes contracts to the same one — but the fallback now
    /// carries that feed's own token rather than mixing the two.
    /// </remarks>
    internal static (string? Feed, string? Token, string TokenVariable) ResolveFeedCredentials(
        string? explicitFeed,
        string? platformFeed,
        string? platformToken,
        string? artifactFeed,
        string? artifactToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitFeed))
            return (explicitFeed, platformToken, FeedTokenEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(platformFeed))
            return (platformFeed, platformToken, FeedTokenEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(artifactFeed))
            return (artifactFeed, artifactToken, PackagedArtifactSource.FeedTokenEnvironmentVariable);

        // No feed configured: the public default, which never receives a credential.
        return (null, null, FeedTokenEnvironmentVariable);
    }

    /// <summary>
    /// Picks the package's best <c>lib/</c> folder for this CLI. Preference order is exact
    /// net10.0, then the highest other .NET version, then netstandard — the same shape a
    /// NuGet restore would choose, without taking a dependency on the NuGet libraries.
    /// </summary>
    private static string[] SelectTargetFrameworkAssemblies(string root, string packageId, string version)
    {
        var lib = Path.Combine(root, "lib");
        if (!Directory.Exists(lib))
        {
            throw new CommandException(
                $"Platform package {packageId} {version} contains no lib/ folder, so it ships no assembly to load. Reference the package that contains your IPlatform type.");
        }

        var best = Directory.EnumerateDirectories(lib)
            .Select(directory => new { Path = directory, Name = Path.GetFileName(directory).ToLowerInvariant() })
            .OrderByDescending(candidate => candidate.Name == "net10.0")
            .ThenByDescending(candidate => candidate.Name.StartsWith("net", StringComparison.Ordinal)
                && !candidate.Name.StartsWith("netstandard", StringComparison.Ordinal))
            .ThenByDescending(candidate => candidate.Name, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new CommandException($"Platform package {packageId} {version} has an empty lib/ folder.");

        var assemblies = Directory.GetFiles(best.Path, "*.dll");
        if (assemblies.Length == 0)
        {
            throw new CommandException($"Platform package {packageId} {version} has no assemblies under lib/{best.Name}/.");
        }

        return assemblies;
    }

    private static (string AssemblyPath, string TypeName) ResolvePlatformType(
        IReadOnlyList<string> assemblies,
        string packageId,
        string version,
        string? platformTypeName)
    {
        var failures = new List<string>();

        foreach (var assemblyPath in assemblies)
        {
            try
            {
                var platform = PlatformLoader.Load(assemblyPath, platformTypeName);
                return (assemblyPath, platform.GetType().FullName!);
            }
            catch (Exception ex) when (ex is InvalidOperationException or BadImageFormatException or FileLoadException or ReflectionTypeLoadException)
            {
                // Packages routinely carry assemblies that expose no platform; only report
                // the failures if none of them does.
                failures.Add($"{Path.GetFileName(assemblyPath)}: {ex.Message}");
            }
        }

        throw new CommandException(
            $"No IPlatform implementation was found in {packageId} {version}. A platform must be a public, concrete, parameterless class implementing IPlatform, built against a compatible NimBus version." +
            (failures.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, failures) : string.Empty));
    }

    /// <summary>Factory the provisioners take, backed by this package's catalog.</summary>
    public Func<IPlatform> CreateFactory() => () => PlatformLoader.Load(PrimaryAssemblyPath, PlatformTypeName);
}
