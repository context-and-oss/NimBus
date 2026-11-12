using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>
/// What a prepared action token binds: the action, the message at the version it was
/// previewed, the caller and client it was issued to, and the environment.
/// </summary>
/// <param name="Action"><c>resubmit</c> or <c>skip</c>.</param>
/// <param name="EndpointId">The endpoint the message belongs to.</param>
/// <param name="EventId">The message's event id.</param>
/// <param name="MessageVersion">The encoded <see cref="Services.Operations.OperatorMessageVersion"/>.</param>
/// <param name="Caller">The caller's stable identity (object id, subject or local user).</param>
/// <param name="TenantId">The caller's tenant, when known.</param>
/// <param name="ClientId">The OAuth client the caller used, when known.</param>
/// <param name="Environment">The NimBus environment that issued the token.</param>
/// <param name="TicketId">For a resubmit by ticket (Spec 044 Phase 3), the ticket; null for one message.</param>
/// <param name="Members">For a resubmit by ticket, each eligible message at the version it was previewed.</param>
public sealed record OperatorActionGrant(
    string Action,
    string EndpointId,
    string EventId,
    string MessageVersion,
    string Caller,
    string? TenantId,
    string? ClientId,
    string? Environment,
    string? TicketId = null,
    IReadOnlyList<OperatorBatchMember>? Members = null);

/// <summary>One message of a resubmit by ticket, at the version it was previewed.</summary>
/// <param name="EventId">The message's event id.</param>
/// <param name="MessageVersion">The encoded version.</param>
public sealed record OperatorBatchMember(string EventId, string MessageVersion);

/// <summary>
/// Issues and reads the short-lived tokens <c>nimbus_prepare_action</c> hands out. A token is
/// an expiring state check bound to one caller, client and message version, protected with
/// ASP.NET Core Data Protection so it cannot be forged or edited. It is not evidence that a
/// human approved anything; the execute tools revalidate everything it describes.
/// </summary>
public sealed class OperatorActionTokens
{
    /// <summary>How long a prepared action stays valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private const string Purpose = "NimBus.Mcp.ActionToken.v1";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeProvider _time;

    /// <summary>Creates the token service.</summary>
    public OperatorActionTokens(IDataProtectionProvider provider, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _time = time;
    }

    /// <summary>Protects <paramref name="grant"/> for <see cref="Lifetime"/>.</summary>
    public string Issue(OperatorActionGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return _protector.Protect(JsonSerializer.Serialize(grant), _time.GetUtcNow().Add(Lifetime));
    }

    /// <summary>
    /// Reads a token. False when it is malformed, tampered with, expired or protected under a
    /// different purpose.
    /// </summary>
    public bool TryRead(string? token, out OperatorActionGrant? grant)
    {
        grant = null;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            var json = _protector.Unprotect(token.Trim(), out var expiration);
            if (expiration <= _time.GetUtcNow())
                return false;

            grant = JsonSerializer.Deserialize<OperatorActionGrant>(json);
            return grant != null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return false;
        }
    }

    /// <summary>The identity a token is bound to, from the request principal.</summary>
    public static (string Caller, string? TenantId, string? ClientId) Identify(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var caller = user.FindFirstValue("oid")
            ?? user.FindFirstValue("sub")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.Identity?.Name
            ?? string.Empty;
        return (caller, user.FindFirstValue("tid"), user.FindFirstValue("azp") ?? user.FindFirstValue("appid"));
    }
}
