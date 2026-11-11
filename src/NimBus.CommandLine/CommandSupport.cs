namespace NimBus.CommandLine;

/// <summary>
/// Where a command reads its deployment assets from. <paramref name="RepositoryRoot"/>
/// is null when running outside a clone — a supported mode since ADR-015: the bicep
/// templates then come from the copies embedded in this package. Only the two
/// application projects still require real sources.
/// </summary>
internal sealed record CommandContext(string? RepositoryRoot)
{
    /// <summary>
    /// An explicit <paramref name="repoRoot"/> is validated and wins. Otherwise the
    /// current directory tree is searched, and finding nothing is not an error —
    /// it selects the packaged assets.
    /// </summary>
    public static CommandContext Create(string? repoRoot) =>
        new(string.IsNullOrWhiteSpace(repoRoot)
            ? RepositoryLocator.TryLocate()
            : RepositoryLocator.Resolve(repoRoot));

    /// <summary>True when the command is running against a repository clone.</summary>
    public bool HasRepository => RepositoryRoot is not null;

    /// <summary>
    /// Root of the asset tree. The packaged extraction mirrors the repository layout,
    /// so every path below is built the same way in both modes.
    /// </summary>
    private string AssetsRoot => RepositoryRoot ?? BicepTemplateProvider.AssetsRoot;

    public string DeployDirectory => Path.Combine(AssetsRoot, "deploy");
    public string CoreBicepPath => Path.Combine(DeployDirectory, "bicep", "deploy.core.bicep");
    public string WebAppBicepPath => Path.Combine(DeployDirectory, "bicep", "deploy.webapp.bicep");

    /// <summary>
    /// The application projects exist only in a clone. Building from source is now the
    /// override rather than the default, so the failure has to say which mode is missing.
    /// </summary>
    public string SourceDirectory => Path.Combine(
        RepositoryRoot ?? throw new CommandException(
            "Building the NimBus applications from source requires a repository clone. Run the command from the repository, pass --repo-root, or omit --from-source to deploy the published release artifacts."),
        "src");

    public string ResolverProjectPath => Path.Combine(SourceDirectory, "NimBus.Resolver", "NimBus.Resolver.csproj");
    public string WebAppProjectPath => Path.Combine(SourceDirectory, "NimBus.WebApp", "NimBus.WebApp.csproj");
}

internal static class RepositoryLocator
{
    public static string Resolve(string? repoRoot)
    {
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            return Validate(Path.GetFullPath(repoRoot));
        }

        return TryLocate()
            ?? throw new CommandException("Could not locate the NimBus repository root. Run the command from the repository or provide --repo-root.");
    }

    /// <summary>
    /// Walks up from the current directory looking for a repository root, returning
    /// null when there is none. Callers that can fall back to packaged assets use this
    /// instead of <see cref="Resolve"/>.
    /// </summary>
    public static string? TryLocate()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (LooksLikeRepositoryRoot(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string Validate(string path)
    {
        if (!LooksLikeRepositoryRoot(path))
        {
            throw new CommandException($"'{path}' does not look like the NimBus repository root. Expected deploy/ and src/ directories.");
        }

        return path;
    }

    private static bool LooksLikeRepositoryRoot(string path) =>
        File.Exists(Path.Combine(path, "README.md")) &&
        Directory.Exists(Path.Combine(path, "deploy")) &&
        Directory.Exists(Path.Combine(path, "src"));
}

internal static class NamingConventions
{
    public static string NormalizePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandException("Azure naming inputs cannot be empty.");
        }

        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new CommandException($"'{value}' does not contain any alpha-numeric characters after normalization.");
        }

        return normalized;
    }

    public static DeploymentNames Build(string solutionId, string environment) =>
        Build(solutionId, environment, serviceBusNamespaceName: null);

    /// <summary>
    /// Same as <see cref="Build(string, string)"/>, with optional overrides:
    /// <paramref name="serviceBusNamespaceName"/> replaces the derived namespace (a Premium
    /// namespace deployed next to the Standard one it replaces, spec 034 §6, option A);
    /// <paramref name="resourceNamePostfix"/> is appended the way the legacy pipeline scripts
    /// appended it (see <see cref="ResourceNaming"/>); <paramref name="funcStorageAccountName"/>
    /// replaces the derived function storage account.
    /// </summary>
    /// <remarks>
    /// The legacy templates postfixed the Service Bus namespace, Cosmos DB account, function
    /// apps and WebApp, but not App Insights or the App Service plans. Matching existing
    /// resources needs exactly that split, so do not "fix" it by postfixing everything.
    /// The SQL server did not exist then; it is postfixed because its name is globally
    /// unique like the others. deploy.core.bicep and deploy.webapp.bicep mirror this rule.
    /// </remarks>
    public static DeploymentNames Build(
        string solutionId,
        string environment,
        string? serviceBusNamespaceName,
        string? resourceNamePostfix = null,
        string? funcStorageAccountName = null)
    {
        var normalizedSolutionId = NormalizePart(solutionId);
        var normalizedEnvironment = NormalizePart(environment);
        var postfix = ResourceNaming.NormalizePostfix(resourceNamePostfix);
        var suffix = postfix is null ? string.Empty : $"-{postfix}";

        var names = new DeploymentNames(
            normalizedSolutionId,
            normalizedEnvironment,
            $"sb-{normalizedSolutionId}-{normalizedEnvironment}{suffix}",
            $"ai-{normalizedSolutionId}-{normalizedEnvironment}-global-tracelog",
            $"cosmos-{normalizedSolutionId}-{normalizedEnvironment}{suffix}",
            $"sql-{normalizedSolutionId}-{normalizedEnvironment}{suffix}",
            $"st{normalizedSolutionId}{normalizedEnvironment}func",
            $"asp-{normalizedSolutionId}-{normalizedEnvironment}-management",
            $"asp-{normalizedSolutionId}-{normalizedEnvironment}-core",
            $"func-{normalizedSolutionId}-{normalizedEnvironment}-resolver{suffix}",
            $"webapp-{normalizedSolutionId}-{normalizedEnvironment}-management{suffix}",
            postfix);

        if (!string.IsNullOrWhiteSpace(serviceBusNamespaceName))
        {
            names = names with { ServiceBusNamespace = serviceBusNamespaceName.Trim() };
        }

        if (ResourceNaming.NormalizeFuncStorageAccountName(funcStorageAccountName) is { } storageAccount)
        {
            names = names with { FuncStorageAccountName = storageAccount };
        }

        return names;
    }
}

internal sealed record DeploymentNames(
    string SolutionId,
    string Environment,
    string ServiceBusNamespace,
    string AppInsightsName,
    string CosmosAccountName,
    string SqlServerName,
    string FuncStorageAccountName,
    string ManagementAppServicePlanName,
    string CoreAppServicePlanName,
    string ResolverFunctionAppName,
    string WebAppName,
    string? ResourceNamePostfix = null);

internal sealed record InfrastructureOptions(
    string SolutionId,
    string Environment,
    string ResourceGroupName,
    string? ResourceNamePostFix,
    string? Location,
    string WebAppVersion,
    StorageProviderChoice StorageProvider = StorageProviderChoice.Cosmos,
    SqlProvisioningMode SqlMode = SqlProvisioningMode.Provision,
    string? SqlConnectionString = null,
    string? SqlAdminLogin = null,
    string? SqlAdminPassword = null,
    string? SqlServerName = null,
    ResolverPlanChoice? ResolverPlan = null,
    string? IdentityAdminEmail = null,
    string? IdentityAdminPassword = null,
    string? ManagementPlanSku = null,
    int? ResolverMaxConcurrentSessions = null,
    int? ResolverMaxInstances = null,
    NetworkOptions? Network = null,
    int? ServiceBusCapacity = null,
    string? ServiceBusNamespaceName = null,
    TimeSpan? DnsWait = null,
    AlertMailDeployment? AlertMail = null,
    string? FuncStorageAccountName = null);

internal enum StorageProviderChoice
{
    Cosmos,
    SqlServer,
}

internal enum SqlProvisioningMode
{
    Provision,
    External,
}

internal enum ResolverPlanChoice
{
    ElasticPremium,
    FlexConsumption,
}

internal sealed record TopologyOptions(
    string SolutionId,
    string Environment,
    string ResourceGroupName,
    string? ServiceBusNamespaceName = null,
    TimeSpan? DnsWait = null,
    string? ResourceNamePostfix = null);

internal sealed record AppDeploymentOptions(
    string SolutionId,
    string Environment,
    string ResourceGroupName,
    string Configuration,
    AppDeploymentTarget Target = AppDeploymentTarget.All,
    TimeSpan? DnsWait = null,
    string? ResourceNamePostfix = null);

/// <summary>Which application(s) `nb deploy apps` builds and deploys.</summary>
internal enum AppDeploymentTarget
{
    All,
    Resolver,
    WebApp,
}

internal static class DeployTargetSelection
{
    /// <summary>Parses the --only option value. Null/blank means "deploy everything".</summary>
    public static AppDeploymentTarget ParseOnlyOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return AppDeploymentTarget.All;
        return value.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "resolver" => AppDeploymentTarget.Resolver,
            "webapp" => AppDeploymentTarget.WebApp,
            "all" => AppDeploymentTarget.All,
            _ => throw new CommandException($"Unknown --only value '{value}'. Expected 'resolver' or 'webapp'."),
        };
    }
}

internal sealed class CommandException : Exception
{
    public CommandException(string message)
        : base(message)
    {
    }
}
