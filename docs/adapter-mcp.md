# Adapter MCP server

`NimBus.Adapter.Mcp` (package `Akaule.NimBus.Adapter.Mcp`) lets an adapter host serve a small,
read-only MCP server next to its handlers ([Spec 051 §7](spec/051-operations-agent-support/spec.md#7-an-adapter-mcp-kit)).
An operations agent that is looking at a failed message in NimBus can then ask the adapter what
its own system holds, without NimBus forwarding anything: the agent connects to the adapter
directly, with a token of its own, and the adapter decides what it may read.

The package is not referenced by any NimBus core project. An adapter that does not add it changes
nothing for anyone.

## Tools

Every tool only reads, and is annotated `readOnlyHint: true`. Results come back both as
`structuredContent` and as one text block carrying the same JSON, in camelCase.

| Tool | Arguments | Returns |
|---|---|---|
| `adapter_get_record` | `entity`, `key` | `{entity, key, found, record}`: the record as it is now in the system, masked |
| `adapter_get_mapping` | `eventTypeId` | `{eventTypeId, known, fields: [{eventPath, systemField, note}], settings}` |
| `adapter_get_last_call` | `entity`, `key` | `{entity, key, found, at, statusCode, outcome, message}`, `outcome` being `succeeded` or `failed` |
| `adapter_get_health` | none | `{reachable, message, version, checkedAt}` |

`version` is the commit the deployment was built from: the part of the host's entry assembly's
`AssemblyInformationalVersion` after `+`, which SourceLink stamps. Without a `+` it is the whole
informational version.

A refused call is a tool result with `isError: true` and one text block `<code>: <message>`:

| Code | When |
|---|---|
| `unknown-entity` | The entity is not in `IAdapterSystem.Entities`. Nothing is read |
| `invalid-arguments` | An argument is missing, empty or not a string |
| `system-error` | The adapter threw. The message names only the exception's type |

## Implement the system

```csharp
public sealed class ErpSystem(ErpClient erp) : IAdapterSystem
{
    public IReadOnlyCollection<string> Entities { get; } = ["customer"];

    public async Task<object?> GetRecordAsync(string entity, string key, CancellationToken cancellationToken)
        => await erp.GetCustomerAsync(key, cancellationToken); // null when there is none

    public AdapterMapping? GetMapping(string eventTypeId) => eventTypeId switch
    {
        nameof(CustomerChanged) => new AdapterMapping
        {
            Fields = [new("customerId", "No"), new("email", "E-Mail", "Lower-cased")],
            Settings = new Dictionary<string, string> { ["company"] = "CRONUS" },
        },
        _ => null,
    };

    public async Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
        => new(await erp.PingAsync(cancellationToken), Message: null);
}
```

An entity left out of `Entities` is never served, by any tool. The server cannot limit a caller to
the records of one message: a caller with the read role can read any record of a served entity by
key. Leave out what callers should not read.

## Masking

Return the typed object the adapter already uses. Properties marked with NimBus's `[Sensitive]`
attribute are masked before the record leaves the server, by NimBus's own masker: the same
`MaskMode` values (`Redact` writes `***`, `PartialReveal` keeps the last `Reveal` characters,
`Hash` writes a salted SHA-256), the same inheritance from a marked class or complex property, and
the same walk into nested objects and collection elements. Set `AdapterMcp:PiiHashSalt` to the
WebApp's `NimBus:PiiHashSalt` to make hashes comparable.

A record returned as JSON (`JsonElement`, `JsonNode`, `JToken`) has no type to read markers from
and is returned as it is. Mapping settings are returned as they are too: never put a secret there.

## The call log

`adapter_get_last_call` reads `AdapterCallLog`, a bounded in-memory log of the most recent call
per (entity, key). Add it to the adapter's HTTP client:

```csharp
builder.Services.AddHttpClient<ErpClient>()
    .AddAdapterCallLog("customer", request => request.RequestUri?.Segments[^1]);
```

It records the status code and reason phrase of each answer, or the exception's type when there
was none (the exception still reaches the adapter). A request for which the key function returns
null is not recorded. For another kind of client, call `AdapterCallLog.Record` yourself.

**The log is per instance.** It lives in the process's memory and holds only the calls this
instance made, up to `AdapterMcp:CallLogCapacity` keys (1000 by default), dropping the oldest. With
several instances, or after a restart or a Functions scale-in, a caller can find no call for a key
another instance handled. Treat `found: false` as "not known here", not as "never called".

## Sign-in

A deployment has one Entra app registration for its adapter MCP servers, separate from the
[NimBus MCP resource](mcp-server.md#1-register-the-mcp-resource):

1. Create an app registration, for example `NimBus adapters (dev)`, single tenant. Note its
   **Application (client) ID**; keep the default Application ID URI (`api://<client-id>`) or set
   your own.
2. Add an **app role** `Adapter.Read` with allowed member type **Applications** (and **Users** if
   people's agents call adapters), and assign it to each caller.
3. Configure every adapter host:

```json
"AdapterMcp": {
  "TenantId": "<tenant id>",
  "Audience": "<client id of the adapter registration>",
  "ApplicationIdUri": "api://nimbus-adapters"
}
```

| Setting | Default | |
|---|---|---|
| `TenantId` | required | The tenant whose tokens are accepted |
| `Audience` | required | The adapter registration's client id |
| `ApplicationIdUri` | `api://<Audience>` | Accepted as an audience next to the client id |
| `ReadRole` | `Adapter.Read` | The app role a token must carry in `roles` |
| `Instance` | `https://login.microsoftonline.com/` | The Entra instance |
| `PiiHashSalt` | empty | The salt for `MaskMode.Hash` |
| `CallLogCapacity` | `1000` | Keys the call log keeps |

The server accepts only a token for that registration (audience the client id or the App ID URI),
issued by the tenant (v2 `https://login.microsoftonline.com/<tenant>/v2.0` or v1
`https://sts.windows.net/<tenant>/`) and carrying the read role. A token for NimBus is refused, as
an adapter token is refused by NimBus. No token, or a token that fails validation, is answered 401
with a `WWW-Authenticate: Bearer` challenge; a valid token without the role, 403. Signing keys are
read from the tenant's OIDC metadata.

All adapters of a deployment accept the same audience, so a token is valid at each of them
([Spec 051 §11](spec/051-operations-agent-support/spec.md#11-risks)). A deployment that cannot
accept that registers a resource per adapter and configures each host with its own.

## The read log

Every tool call writes one structured log entry, event `AdapterRead` (id 5101, category
`NimBus.Adapter.Mcp.Reads`), with the caller's object id (`CallerObjectId`), the client's
application id (`CallerAppId`, from `azp` or `appid`), the tool, the entity, the key and the
outcome. It never carries a record value. Route the category to a sink you keep.

## Host it in ASP.NET Core

```csharp
builder.Services.AddAdapterMcp<ErpSystem>(builder.Configuration);

var app = builder.Build();
app.MapAdapterMcp(); // /mcp
```

The endpoint signs callers in itself and needs no authentication middleware.

## Host it in an Azure Functions HttpTrigger

Use the isolated worker with ASP.NET Core integration (`ConfigureFunctionsWebApplication` and
`Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore`), so the trigger receives the
request's `HttpContext`:

```csharp
builder.Services.AddAdapterMcp<ErpSystem>(builder.Configuration);

public sealed class AdapterMcpFunction(AdapterMcpHandler mcp)
{
    [Function("adapter-mcp")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "delete", Route = "mcp")] HttpRequest request)
    {
        await mcp.HandleAsync(request.HttpContext);
        return new EmptyResult();
    }
}
```

The function is anonymous at the Functions level because the handler validates the Entra token.
With the default route prefix the address is `https://<app>.azurewebsites.net/api/mcp`.

## Transport

The server is the MCP SDK's (`ModelContextProtocol.AspNetCore`) stateless Streamable HTTP server,
run from the request's `HttpContext`. POST carries JSON-RPC; there are no sessions and no
server-to-client stream, so GET and DELETE answer 405. A client must accept both
`application/json` and `text/event-stream`; the SDK frames each POST's answer as a single
server-sent event and closes the response. Clients on protocol versions with a handshake send
`initialize`; the SDK's own client, from protocol `2026-07-28`, sends `server/discover` instead.

Set the adapter's MCP address on its endpoint in NimBus
([Spec 051 §6](spec/051-operations-agent-support/spec.md#6-the-adapters-mcp-address-on-the-endpoint))
so that callers can find it.
