using System.Text.RegularExpressions;

namespace NimBus.CommandLine;

/// <summary>
/// Resource-name options that let NimBus deploy onto resources named by the legacy pipeline
/// scripts: <c>--resource-name-postfix</c> and <c>--func-storage-account-name</c>. Both are
/// recorded as resource group tags by <c>nb infra apply</c>, so later commands address the
/// same resources without the options being repeated.
/// </summary>
internal static partial class ResourceNaming
{
    /// <summary>Resource group tag holding the postfix <c>nb infra apply</c> deployed with.</summary>
    public const string PostfixTag = "nimbus-resource-name-postfix";

    /// <summary>Resource group tag holding the function storage account override.</summary>
    public const string FuncStorageAccountTag = "nimbus-func-storage-account";

    public const string PostfixOptionTemplate = "--resource-name-postfix <VALUE>";

    /// <summary>Shared by every command that takes the option, so they all say the same thing.</summary>
    public const string PostfixOptionDescription =
        "For compatibility with the legacy pipeline scripts: appended as '-<value>' to the Service Bus namespace, " +
        "Cosmos DB account, SQL server, Resolver function app and management WebApp names only. App Insights, the App Service " +
        "plans and the function storage account are not postfixed; use --func-storage-account-name to reuse an existing storage " +
        "account. Recorded on the resource group, so later runs pick it up.";

    /// <summary>Shorter description for commands that only read the deployed names.</summary>
    public const string PostfixLookupOptionDescription =
        "Resource-name postfix passed to 'nb infra apply' (legacy pipeline compatibility). Defaults to the value recorded on the resource group.";

    public const string FuncStorageAccountOptionTemplate = "--func-storage-account-name <NAME>";

    public const string FuncStorageAccountOptionDescription =
        "Name of an existing storage account to use for the Resolver function app instead of 'st{solution-id}{environment}func', " +
        "e.g. the legacy msgstore account. Its SKU and kind are kept. Recorded on the resource group, so later runs pick it up.";

    /// <summary>
    /// Trims, drops one leading '-', and lower-cases. Anything other than letters and digits
    /// fails: silently stripping characters would produce a name that misses the existing resource.
    /// </summary>
    public static string? NormalizePostfix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var normalized = (trimmed.StartsWith('-') ? trimmed[1..] : trimmed).ToLowerInvariant();
        if (!PostfixPattern().IsMatch(normalized))
        {
            throw new CommandException(
                $"Invalid --resource-name-postfix '{value}'. Use letters and digits only; NimBus adds the '-' separator itself.");
        }

        // Azure rejects a Service Bus namespace name ending in '-sb' or '-mgmt'.
        if (normalized is "sb" or "mgmt")
        {
            throw new CommandException(
                $"Invalid --resource-name-postfix '{value}'. Azure does not allow a Service Bus namespace name that ends in '-{normalized}'.");
        }

        return normalized;
    }

    /// <summary>
    /// Validates an Azure storage account name (3-24 lowercase letters and digits). It is not
    /// normalized: a changed name would miss the existing account.
    /// </summary>
    public static string? NormalizeFuncStorageAccountName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!StorageAccountPattern().IsMatch(trimmed))
        {
            throw new CommandException(
                $"Invalid --func-storage-account-name '{value}'. A storage account name is 3-24 lowercase letters and digits.");
        }

        return trimmed;
    }

    /// <summary>
    /// The value to deploy with, given the explicit option value and the one recorded on the
    /// resource group. A recorded value fills in a missing option; two different values fail,
    /// because Azure cannot rename resources in place.
    /// </summary>
    public static string? Resolve(
        string? explicitValue,
        string? recordedValue,
        string optionName,
        string tagName,
        string resourceGroupName)
    {
        var recorded = string.IsNullOrWhiteSpace(recordedValue) ? null : recordedValue.Trim();
        if (explicitValue is null)
        {
            if (recorded is not null)
            {
                CliOutput.WriteLine($"Using the {optionName} '{recorded}' recorded on '{resourceGroupName}'.");
            }

            return recorded;
        }

        if (recorded is null || string.Equals(explicitValue, recorded, StringComparison.OrdinalIgnoreCase))
        {
            return explicitValue;
        }

        throw new CommandException(
            $"{optionName} '{explicitValue}' differs from '{recorded}' recorded on resource group '{resourceGroupName}'. " +
            "Azure cannot rename resources in place, so a different value would deploy a second, differently named set of resources " +
            $"next to the existing ones. Pass '{recorded}', or remove the '{tagName}' tag from the resource group if a new set is intended.");
    }

    /// <summary>Resolves the postfix against the tags already read from the resource group.</summary>
    public static string? ResolvePostfix(string? explicitValue, IReadOnlyDictionary<string, string> tags, string resourceGroupName)
    {
        tags.TryGetValue(PostfixTag, out var recorded);
        return Resolve(
            NormalizePostfix(explicitValue),
            NormalizePostfix(recorded),
            "--resource-name-postfix",
            PostfixTag,
            resourceGroupName);
    }

    /// <summary>Resolves the function storage account override against the resource group tags.</summary>
    public static string? ResolveFuncStorageAccountName(string? explicitValue, IReadOnlyDictionary<string, string> tags, string resourceGroupName)
    {
        tags.TryGetValue(FuncStorageAccountTag, out var recorded);
        return Resolve(
            NormalizeFuncStorageAccountName(explicitValue),
            NormalizeFuncStorageAccountName(recorded),
            "--func-storage-account-name",
            FuncStorageAccountTag,
            resourceGroupName);
    }

    /// <summary>
    /// Names for commands that address an existing deployment (topology, apps, endpoint
    /// containers): explicit options win, otherwise what <c>nb infra apply</c> recorded on the
    /// resource group. The tags are always read, so an explicit postfix that differs from the
    /// recorded one fails here instead of addressing resources that do not exist.
    /// </summary>
    public static async Task<DeploymentNames> ResolveDeployedNamesAsync(
        IAzureCliRunner az,
        string solutionId,
        string environment,
        string resourceGroupName,
        string? explicitServiceBusNamespaceName,
        string? explicitPostfix,
        CancellationToken cancellationToken)
    {
        var namespaceName = string.IsNullOrWhiteSpace(explicitServiceBusNamespaceName) ? null : explicitServiceBusNamespaceName.Trim();
        var postfix = NormalizePostfix(explicitPostfix);

        var tags = await ResourceGroupTags.TryReadTagsAsync(az, resourceGroupName, cancellationToken).ConfigureAwait(false);
        if (namespaceName is null
            && tags.TryGetValue(NetworkIntent.ServiceBusNamespaceTag, out var recordedNamespace)
            && !string.IsNullOrWhiteSpace(recordedNamespace))
        {
            namespaceName = recordedNamespace.Trim();
            CliOutput.WriteLine($"Using the Service Bus namespace '{namespaceName}' recorded on '{resourceGroupName}'.");
        }

        postfix = ResolvePostfix(postfix, tags, resourceGroupName);

        return NamingConventions.Build(solutionId, environment, namespaceName, postfix);
    }

    /// <summary>
    /// Guards the first postfix decision on a resource group that has none recorded (deployed
    /// before the option took effect, or by the legacy scripts): Azure cannot rename resources,
    /// so a postfix that does not match the existing names would deploy a second, parallel set.
    /// Fails when none of the target names exist but the same deployment exists under the other
    /// naming. Nothing is checked once a postfix is recorded; <see cref="Resolve"/> covers that.
    /// </summary>
    /// <param name="target">The names this run would deploy.</param>
    /// <param name="existingNames">Names of the resources already in the resource group.</param>
    /// <param name="resourceGroupName">For the error message.</param>
    public static void GuardAgainstParallelDeployment(DeploymentNames target, IReadOnlyCollection<string> existingNames, string resourceGroupName)
    {
        var existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var targetMarkers = new[] { target.ResolverFunctionAppName, target.WebAppName, target.CosmosAccountName };
        if (targetMarkers.Any(existing.Contains))
        {
            return;
        }

        if (target.ResourceNamePostfix is { } postfix)
        {
            var unpostfixed = NamingConventions.Build(target.SolutionId, target.Environment, serviceBusNamespaceName: null);
            var found = new[] { unpostfixed.ResolverFunctionAppName, unpostfixed.WebAppName, unpostfixed.CosmosAccountName }
                .Where(existing.Contains)
                .ToList();
            if (found.Count > 0)
            {
                throw new CommandException(
                    $"Resource group '{resourceGroupName}' already holds a NimBus deployment without a resource-name postfix ({string.Join(", ", found)}). " +
                    $"Deploying with --resource-name-postfix '{postfix}' would create a second, differently named set of resources next to it. " +
                    "Leave the option out to update the existing deployment.");
            }

            return;
        }

        var resolverPrefix = $"func-{target.SolutionId}-{target.Environment}-resolver-";
        var webAppPrefix = $"webapp-{target.SolutionId}-{target.Environment}-management-";
        var detected = existing
            .Select(name => name.StartsWith(resolverPrefix, StringComparison.OrdinalIgnoreCase) ? name[resolverPrefix.Length..]
                : name.StartsWith(webAppPrefix, StringComparison.OrdinalIgnoreCase) ? name[webAppPrefix.Length..]
                : null)
            .Where(candidate => candidate is not null && PostfixPattern().IsMatch(candidate.ToLowerInvariant()))
            .Select(candidate => candidate!.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(candidate => candidate, StringComparer.Ordinal)
            .ToList();
        if (detected.Count > 0)
        {
            throw new CommandException(
                $"Resource group '{resourceGroupName}' already holds a NimBus deployment named with the resource-name postfix " +
                $"{string.Join(" or ", detected.Select(candidate => $"'{candidate}'"))}. Deploying without --resource-name-postfix would create " +
                $"a second, differently named set of resources next to it. Pass --resource-name-postfix {detected[0]} to update the existing deployment.");
        }
    }

    /// <summary>
    /// Fails before anything is deployed when the postfix makes a derived name longer than
    /// Azure allows. Explicit name overrides are validated by their own options.
    /// </summary>
    public static void ValidateLengths(DeploymentNames names, string? serviceBusNamespaceOverride, string? sqlServerOverride)
    {
        if (names.ResourceNamePostfix is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(serviceBusNamespaceOverride))
        {
            CheckLength(names.ServiceBusNamespace, 50, "Service Bus namespace");
        }

        if (string.IsNullOrWhiteSpace(sqlServerOverride))
        {
            CheckLength(names.SqlServerName, 63, "SQL server");
        }

        CheckLength(names.CosmosAccountName, 44, "Cosmos DB account");
        CheckLength(names.ResolverFunctionAppName, 60, "Resolver function app");
        CheckLength(names.WebAppName, 60, "management WebApp");
    }

    /// <summary>
    /// One line for the deployment log saying which names carry the postfix, so a pipeline
    /// run shows which existing resources it will match.
    /// </summary>
    public static string DescribePostfix(DeploymentNames names, IEnumerable<string> postfixed)
    {
        var notPostfixed = new[]
        {
            names.AppInsightsName,
            names.ManagementAppServicePlanName,
            names.CoreAppServicePlanName,
            names.FuncStorageAccountName,
        };

        return $"Resource-name postfix '{names.ResourceNamePostfix}' applies to: {string.Join(", ", postfixed)}. " +
            $"Not postfixed: {string.Join(", ", notPostfixed)}.";
    }

    private static void CheckLength(string name, int maximum, string resource)
    {
        if (name.Length > maximum)
        {
            throw new CommandException(
                $"The {resource} name '{name}' is {name.Length} characters; Azure allows {maximum}. Use a shorter --resource-name-postfix.");
        }
    }

    [GeneratedRegex("^[a-z0-9]+$")]
    private static partial Regex PostfixPattern();

    [GeneratedRegex("^[a-z0-9]{3,24}$")]
    private static partial Regex StorageAccountPattern();
}
