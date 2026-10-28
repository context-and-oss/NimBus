using McMaster.Extensions.CommandLineUtils;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// Characterizes the <c>nb</c> command graph so relocating command registrations cannot
/// silently change command names, option names, required flags or shared-option wiring.
/// Help text is deliberately not snapshotted.
/// </summary>
public class CliApplicationFactoryTests
{
    [Fact]
    public void Top_level_commands_are_stable()
    {
        using var app = CliApplicationFactory.Create();

        var names = app.Commands.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            new[] { "asyncapi", "catalog", "container", "deploy", "endpoint", "infra", "setup", "topology" },
            names);
    }

    [Fact]
    public void Command_tree_with_options_and_required_flags_is_stable()
    {
        using var app = CliApplicationFactory.Create();

        var actual = Describe(app).ToArray();

        Assert.Equal(ExpectedTree, actual);
    }

    [Fact]
    public void Shared_connection_options_are_the_same_instance_across_commands()
    {
        using var app = CliApplicationFactory.Create();

        var sbOptions = FindOptions(app, "sb-connection-string").Distinct().ToArray();
        var dbOptions = FindOptions(app, "db-connection-string").Distinct().ToArray();

        Assert.Single(sbOptions);
        Assert.Single(dbOptions);
    }

    [Fact]
    public async Task No_subcommand_shows_help_and_returns_1()
    {
        using var app = CliApplicationFactory.Create();
        app.Out = TextWriter.Null;

        Assert.Equal(1, await app.ExecuteAsync(Array.Empty<string>()));
    }

    [Fact]
    public async Task Missing_required_option_fails_validation_without_executing()
    {
        using var app = CliApplicationFactory.Create();
        app.Out = TextWriter.Null;
        app.Error = TextWriter.Null;

        Assert.NotEqual(0, await app.ExecuteAsync(new[] { "infra", "apply" }));
    }

    private static IEnumerable<string> Describe(CommandLineApplication command, string path = "nb")
    {
        var options = command.Options
            .Where(o => o.LongName != "help")
            .Select(o => "--" + (o.LongName ?? o.ShortName) + (o.Validators.Any() ? "!" : ""))
            .OrderBy(o => o, StringComparer.Ordinal);
        var arguments = command.Arguments.Select(a => "<" + a.Name + (a.Validators.Any() ? "!" : "") + ">");
        yield return $"{path}: {string.Join(" ", arguments.Concat(options))}".TrimEnd();
        foreach (var child in command.Commands.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            foreach (var line in Describe(child, $"{path} {child.Name}"))
                yield return line;
        }
    }

    private static IEnumerable<CommandOption> FindOptions(CommandLineApplication command, string longName)
    {
        foreach (var option in command.Options.Where(o => o.LongName == longName))
            yield return option;
        foreach (var child in command.Commands)
        {
            foreach (var option in FindOptions(child, longName))
                yield return option;
        }
    }

    private static readonly string[] ExpectedTree =
    [
        "nb:",
        "nb asyncapi:",
        "nb asyncapi diff: <old-file!> <new-file!>",
        "nb asyncapi export: --assembly --format --output --provider",
        "nb asyncapi validate: <file!>",
        "nb catalog:",
        "nb catalog asyncapi: --format --output",
        "nb catalog export: --assembly --output --platform --title",
        "nb container:",
        "nb container copy: <endpoint-name!> --batch-size --db-connection-string --from --status --target-db-connection-string! --to",
        "nb container delete: <endpoint-name!> --db-connection-string --status",
        "nb container event:",
        "nb container event delete: <endpoint-name!> <event-id!> --db-connection-string",
        "nb container message:",
        "nb container message delete: <to!> --db-connection-string",
        "nb container resubmit: <endpoint-name!> --db-connection-string --sb-connection-string --unresolved-retention-days",
        "nb container skip: <endpoint-name!> --before --db-connection-string --sb-connection-string --status!",
        "nb deploy:",
        "nb deploy apps: --configuration --dns-wait --environment! --from-source --only --platform --platform-feed --platform-package --repo-root --resource-group! --solution-id!",
        "nb endpoint:",
        "nb endpoint purge: <endpoint-name!> --before --sb-connection-string --state --subscription",
        "nb endpoint session:",
        "nb endpoint session delete: <endpoint-name!> <session!> --db-connection-string --sb-connection-string",
        "nb endpoint topics:",
        "nb endpoint topics removeDeprecated: <endpoint-name!> --sb-connection-string",
        "nb infra:",
        "nb infra apply: --allow-public-access --dns-wait --environment! --location --management-plan-sku --monitor-private-link --network-mode --private-dns --private-dns-link-vnet-id --private-dns-zone-scope --private-endpoint-subnet-id --repo-root --resolver-max-instances --resolver-max-sessions --resolver-plan --resolver-subnet-id --resource-group! --resource-name-postfix --service-bus-capacity --service-bus-namespace-name --skip-transition --solution-id! --sql-admin-login --sql-mode --sql-server-name --storage-provider --webapp-subnet-id --webapp-version",
        "nb setup: --allow-public-access --assembly --configuration --dns-wait --environment! --from-source --identity-admin-email --location --management-plan-sku --monitor-private-link --network-mode --platform --platform-feed --platform-package --private-dns --private-dns-link-vnet-id --private-dns-zone-scope --private-endpoint-subnet-id --repo-root --resolver-max-instances --resolver-max-sessions --resolver-plan --resolver-subnet-id --resource-group! --resource-name-postfix --service-bus-capacity --service-bus-namespace-name --skip-transition --solution-id! --sql-admin-login --sql-mode --sql-server-name --storage-provider --webapp-subnet-id --webapp-version",
        "nb topology:",
        "nb topology apply: --assembly --dns-wait --environment --platform --platform-feed --platform-package --resource-group --sb-connection-string --service-bus-namespace-name --solution-id --storage-provider",
        "nb topology export: --assembly --output --platform --platform-feed --platform-package",
    ];
}
