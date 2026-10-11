using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.Extensions.Identity.Data;

namespace NimBus.Extensions.Identity.Services;

internal sealed class IdentityInitializerHostedService : IHostedService
{
    private static readonly Regex SchemaNamePattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled);

    // The platform-admin marker the WebApp authorizes site Owners by. Identity roles surface
    // as "groups" claims (NimBusClaimsTransformation), so membership makes the user an Owner.
    private const string PlatformAdminRole = "EIP_Management";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NimBusIdentityOptions _options;
    private readonly ILogger<IdentityInitializerHostedService> _logger;

    public IdentityInitializerHostedService(
        IServiceScopeFactory scopeFactory,
        NimBusIdentityOptions options,
        ILogger<IdentityInitializerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NimBusIdentityDbContext>();
            await EnsureSchemaAndTablesAsync(dbContext, cancellationToken).ConfigureAwait(false);

            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            await BootstrapAdminAsync(userManager, roleManager, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Don't crash the host — other request paths still work, and surfacing the error
            // in logs is more useful than a boot loop.
            _logger.LogError(ex, "NimBus Identity initializer failed; sign-in may be unavailable until resolved.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureSchemaAndTablesAsync(NimBusIdentityDbContext dbContext, CancellationToken ct)
    {
        var schema = string.IsNullOrWhiteSpace(_options.Schema) ? "dbo" : _options.Schema;
        if (!SchemaNamePattern.IsMatch(schema))
            throw new InvalidOperationException($"NimBusIdentity:Schema '{schema}' is not a valid SQL identifier.");

        if (!string.Equals(schema, "dbo", StringComparison.OrdinalIgnoreCase))
        {
            // Schema names can't be parameterized in DDL. The schema value is validated
            // against SchemaNamePattern above to be a safe SQL identifier, so direct
            // embedding is acceptable here. EF1002 flags interpolation into raw SQL — the
            // validation upstream is exactly the "make sure the value is sanitized" path
            // the analyzer message points at. EXEC keeps CREATE SCHEMA as the first
            // statement in its batch.
            var createSchemaSql =
                "IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'" + schema + "') "
                + "EXEC('CREATE SCHEMA [" + schema + "]')";
#pragma warning disable EF1002 // schema validated against SchemaNamePattern
            await dbContext.Database.ExecuteSqlRawAsync(createSchemaSql, ct).ConfigureAwait(false);
#pragma warning restore EF1002
        }

        if (await IdentityTablesExistAsync(dbContext, schema, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("NimBus Identity tables already present in schema [{Schema}].", schema);
            return;
        }

        var creator = (IRelationalDatabaseCreator)dbContext.GetService<IDatabaseCreator>();
        await creator.CreateTablesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("NimBus Identity tables created in schema [{Schema}].", schema);
    }

    private static async Task<bool> IdentityTablesExistAsync(NimBusIdentityDbContext dbContext, string schema, CancellationToken ct)
    {
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            openedHere = true;
        }
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = 'AspNetUsers'";
            var p = cmd.CreateParameter();
            p.ParameterName = "@schema";
            p.Value = schema;
            cmd.Parameters.Add(p);
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is not null && Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) > 0;
        }
        finally
        {
            if (openedHere) await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task BootstrapAdminAsync(UserManager<NimBusUser> userManager, RoleManager<IdentityRole> roleManager, CancellationToken ct)
    {
        var email = _options.Bootstrap.Email;
        var password = _options.Bootstrap.Password;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return;

        if (await userManager.Users.AnyAsync(ct).ConfigureAwait(false))
        {
            _logger.LogDebug("Bootstrap admin skipped — user store is not empty.");
            return;
        }

        var user = new NimBusUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(_options.Bootstrap.DisplayName)
                ? null
                : _options.Bootstrap.DisplayName,
        };

        var result = await userManager.CreateAsync(user, password).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogError(
                "Bootstrap admin could not be created: {Errors}",
                string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }

        // Without the platform-admin role the bootstrap admin is denied everywhere on a
        // fresh store and cannot grant anyone access.
        if (!await roleManager.RoleExistsAsync(PlatformAdminRole).ConfigureAwait(false))
        {
            var createRole = await roleManager.CreateAsync(new IdentityRole(PlatformAdminRole)).ConfigureAwait(false);
            if (!createRole.Succeeded)
            {
                LogBootstrapRoleFailure(createRole);
                return;
            }
        }

        var addToRole = await userManager.AddToRoleAsync(user, PlatformAdminRole).ConfigureAwait(false);
        if (!addToRole.Succeeded)
        {
            LogBootstrapRoleFailure(addToRole);
            return;
        }

        // The address is personal data, so it stays out of the logs.
        _logger.LogInformation("Bootstrap admin created as platform admin.");
    }

    private void LogBootstrapRoleFailure(IdentityResult result) =>
        _logger.LogError(
            "Bootstrap admin was created but could not be made platform admin: {Errors}",
            string.Join("; ", result.Errors.Select(e => e.Description)));
}
