# Hosting Adapters as a Worker in Azure Container Apps

This guide explains how to host a NimBus adapter as a long-running .NET Worker
in an Azure Container App. The Worker owns its receive loop through
`AddNimBusReceiver(...)` and runs the deferred processor, and any other
background work, as hosted services in the same process.

It is the shape the [adapter kit](https://github.com/akakaule/nimbus-adapter-kit)
generates. For the serverless alternative, see
[Azure Functions hosting](azure-functions-hosting.md).

## Why a Worker?

- **Circuit breaker that pauses receiving.** Only a hosted receiver can stop
  while the external system is down. A Functions trigger keeps receiving (see
  [the limitation](circuit-breaker.md#azure-functions-limitation)).
- **Background services in the same process.** The deferred processor, an
  outbox dispatcher, and polling or ingress loops run next to the receiver.
- **Explicit concurrency.** `MaxConcurrentSessions` and the replica count are
  yours to set, which matters when the external system is rate-limited.
- **The same process locally and in Azure.** The Worker runs unchanged under
  Aspire, in a debugger, and in a container.

The cost is an always-on process: keep at least one replica running. See the
[comparison table](azure-functions-hosting.md#worker-service-vs-azure-functions)
for the two models side by side.

## Project setup

### 1. Create the Worker project

```bash
dotnet new worker -n MyEndpoint.Adapter --framework net10.0
```

Add the packages. Use the NimBus version your Resolver and WebApp run:

```xml
<ItemGroup>
  <PackageReference Include="Akaule.NimBus.SDK" Version="$(NimBusVersion)" />
  <PackageReference Include="Akaule.NimBus.ServiceDefaults" Version="$(NimBusVersion)" />
  <PackageReference Include="Aspire.Azure.Messaging.ServiceBus" Version="13.6.5" />
</ItemGroup>
```

Reference the contracts project or package that holds your events as well.

### 2. Configure Program.cs

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
});

builder.Services.AddNimBusSubscriber("BillingEndpoint", sub =>
{
    sub.AddHandlersFromAssemblyContaining<OrderPlacedHandler>();
});

// The receive loop. Without it the subscriber is configured but consumes nothing.
builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "BillingEndpoint";
    opts.SubscriptionName = "BillingEndpoint";
    opts.MaxConcurrentSessions = 8;
});

// Replays messages parked behind a blocked session.
builder.Services.AddNimBusDeferredProcessorHostedService("BillingEndpoint");

builder.Build().Run();
```

Three registrations make a consuming Worker:

- **`AddNimBusSubscriber(...)`** configures handlers and the dispatch pipeline.
- **`AddNimBusReceiver(...)`** hosts the session processor that feeds it.
- **`AddNimBusDeferredProcessorHostedService(...)`** drives deferred replay on
  the `deferredprocessor` subscription.

Host one subscriber endpoint per process. The circuit breaker is scoped to the
process's single endpoint.

[Building adapters](building-adapters.md#complete-worker-shape) shows the same
`Program.cs` with retry rules, a circuit breaker and a publisher, and lists every
[receiver option](building-adapters.md#worker-receiver).

### 3. Add other background services

A Worker can host more than the receiver:

- **Outbox dispatcher** when publishing is coupled to a local database
  transaction; see [transactional outbox](building-adapters.md#transactional-outbox).
- **Polling or ingress loops** that read changes from the external system and
  publish them.

Every hosted service runs in every replica. Design a service you add for that,
or keep the app at one replica.

## Build the container image

The .NET SDK builds the image without a Dockerfile:

```bash
dotnet publish src/MyEndpoint.Adapter -c Release -t:PublishContainer
```

Set `ContainerRepository` in the project file to name the image.

## Configure the Container App

The adapter kit's
[`deploy/main.bicep`](https://github.com/akakaule/nimbus-adapter-kit/blob/main/skills/nimbus-adapter/template/deploy/main.bicep)
is a complete template. These are the settings that matter.

### No ingress

A Worker serves no HTTP traffic, so the Container App has no ingress. Its health
shows in the logs and on the WebApp's [Heartbeat](heartbeat.md) page, which
probes every endpoint.

### Identity instead of keys

Give the app a user-assigned managed identity with these roles:

| Role | Scope | Why |
| --- | --- | --- |
| Azure Service Bus Data Receiver | Service Bus namespace | Receive and settle messages |
| Azure Service Bus Data Sender | Service Bus namespace | Send responses, retries and deferrals; publish events |
| AcrPull | Container registry | Pull the image |

Then point the client at the namespace by host name, with no key:

```bicep
env: [
  // DefaultAzureCredential picks the user-assigned identity by its client id.
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
  // A namespace host name (no key): the client authenticates with the identity.
  { name: 'ConnectionStrings__servicebus', value: '${serviceBus.name}.servicebus.windows.net' }
]
```

Reference secrets the adapter still needs, such as a database connection string,
from Key Vault with the same identity.

### Scale on the subscription backlog

```bicep
scale: {
  minReplicas: 1
  maxReplicas: 3
  rules: [
    {
      name: 'servicebus-backlog'
      custom: {
        type: 'azure-servicebus'
        identity: identity.id
        metadata: {
          namespace: serviceBus.name
          topicName: 'BillingEndpoint'
          subscriptionName: 'BillingEndpoint'
          messageCount: '50'
        }
      }
    }
  ]
}
```

- **`minReplicas: 1`** keeps a receiver running. The kit template does not
  allow zero.
- **`maxReplicas`** is a limit on the external system as much as on cost.
  Replicas multiply concurrency.
- **`messageCount`** is the backlog per replica that triggers another replica.

## Replicas and concurrency

Replicas are competing receivers on the same subscription. Service Bus hands
each session to one receiver at a time, so per-session ordering holds across
replicas.

- **Total concurrency** is `MaxConcurrentSessions × replicas`, capped by the
  number of distinct active sessions. Size the product to what the external
  system tolerates.
- **Scale order:** raise `MaxConcurrentSessions` until CPU is the limit, then
  add replicas. See [throughput tuning](throughput-tuning.md#scale-out-vs-scale-up).
- **Circuit breaker state is per process.** Each replica counts its own
  failures and opens its own circuit.
- **A rate-limited upstream** may need a fixed replica count. The CrmErpDemo
  pins its adapters to one replica (`PinToOneReplica` in
  [`CrmErpDemo.AppHost/Program.cs`](../samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs)).

## Shutdown and new revisions

Container Apps stops a replica when it scales in or when a new revision takes
over. The host then stops the receiver:

- A handler that has already returned or failed settles in full.
- A handler that honours its cancellation token stops without settling, and the
  message is redelivered.
- The processor waits for in-flight handlers before it closes.

[Settlement and receiver shutdown](message-flows.md#settlement-and-receiver-shutdown)
covers the mechanics. Both the .NET host's `ShutdownTimeout` and the Container
App's termination grace period default to 30 seconds. Raise both when a handler
can run longer, or pass the token to the calls that take the time.

## Deployment

Apply the Service Bus topology before the code. The adapter never creates it at
startup:

```bash
nb topology apply --solution-id nimbus --environment prod --resource-group rg-nimbus-prod --assembly <catalog dll>
```

Then push the image and deploy the Container App. The adapter kit's pipeline
does both per environment, in that order.

## Run it locally

The Worker runs under Aspire with the Service Bus emulator, the Resolver and the
WebApp. See
[run a local stack from packages](building-adapters.md#run-a-local-stack-from-packages).

## Key source files

| File | Purpose |
| --- | --- |
| `samples/CrmErpDemo/Crm.Adapter/Program.cs` | Minimal Worker adapter |
| `samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs` | Adapters published as Container Apps |
| `src/NimBus.SDK/Hosting/NimBusReceiverHostedService.cs` | The hosted session receiver |
| `src/NimBus.SDK/Hosting/CircuitBreakerLifecycleHostedService.cs` | Pauses and resumes receivers with the circuit |
