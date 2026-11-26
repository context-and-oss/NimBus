using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.SqlServer;

namespace NimBus.WebApp.Services.IntegrationIntelligence;

/// <summary>Adapts the WebApp's existing ACL and audit services to the extension boundary.</summary>
public sealed class WebAppIntegrationIntelligenceHostAdapter : IIntegrationIntelligenceHost
{
    private readonly IEndpointAuthorizationService _authorization;
    private readonly IEndpointMetadataStore _endpoints;
    private readonly IAuditLogService _audit;
    private readonly IHttpContextAccessor _httpContext;

    /// <summary>Creates the adapter.</summary>
    public WebAppIntegrationIntelligenceHostAdapter(
        IEndpointAuthorizationService authorization,
        IEndpointMetadataStore endpoints,
        IAuditLogService audit,
        IHttpContextAccessor httpContext)
    {
        _authorization = authorization;
        _endpoints = endpoints;
        _audit = audit;
        _httpContext = httpContext;
    }

    /// <inheritdoc />
    public string? CurrentActor => _authorization.GetCurrentUserName();

    /// <inheritdoc />
    public async Task<bool> EndpointExistsAsync(string endpointId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _endpoints.GetEndpointMetadata(endpointId).ConfigureAwait(false) is not null;
        }
        catch (EndpointNotFoundException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<bool> HasReaderAsync(string endpointId, CancellationToken cancellationToken = default)
        => _authorization.HasRoleAsync(AccessRole.Reader, endpointId);

    /// <inheritdoc />
    public Task<bool> HasContributorAsync(string endpointId, CancellationToken cancellationToken = default)
        => _authorization.HasRoleAsync(AccessRole.Contributor, endpointId);

    /// <inheritdoc />
    public Task AuditAsync(MessageAuditType type, string? eventId, string? endpointId, string data, bool accessDenied, CancellationToken cancellationToken = default)
    {
        var context = _httpContext.HttpContext ?? new DefaultHttpContext();
        return _audit.LogAuditAsync(type, context, accessDenied, data, eventId, endpointId, cancellationToken: cancellationToken);
    }
}

/// <summary>Supplies already-resolved message-store settings to the extension.</summary>
public sealed class WebAppIntegrationIntelligenceStorageSettings : IIntegrationIntelligenceStorageSettings
{
    private readonly string _provider;
    private readonly IServiceProvider _services;

    /// <summary>Creates settings bound to the selected WebApp storage provider.</summary>
    public WebAppIntegrationIntelligenceStorageSettings(string provider, IServiceProvider services)
    {
        _provider = provider;
        _services = services;
    }

    /// <inheritdoc />
    public string Provider => _provider;

    /// <inheritdoc />
    public string? SqlConnectionString => _services.GetService<IOptions<SqlServerMessageStoreOptions>>()?.Value.ConnectionString;

    /// <inheritdoc />
    public string SqlSchema => _services.GetService<IOptions<SqlServerMessageStoreOptions>>()?.Value.Schema ?? "nimbus";

    /// <inheritdoc />
    public int SqlSchemaUpgradeTimeoutSeconds => _services.GetService<IOptions<SqlServerMessageStoreOptions>>()?.Value.SchemaUpgradeTimeoutSeconds
        ?? SqlServerMessageStoreOptions.DefaultSchemaUpgradeTimeoutSeconds;

    /// <inheritdoc />
    public string? CosmosDatabaseName => _services.GetService<CosmosClient>() is not null ? "MessageDatabase" : null;

    /// <inheritdoc />
    public CosmosClient? CosmosClient => _services.GetService<CosmosClient>();
}
