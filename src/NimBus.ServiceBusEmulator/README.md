# NimBus Service Bus Emulator

This project is a loopback-only Azure Service Bus emulator for NimBus development. The unmodified `Azure.Messaging.ServiceBus` 7.20.x data and administration clients use one connection string and one TCP port.

## Stand-alone

```powershell
dotnet run --project src/NimBus.ServiceBusEmulator -- --port 5672
```

```text
Endpoint=sb://127.0.0.1:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=nimbus-local;UseDevelopmentEmulator=true
```

The process binds loopback only. `GET /health` is unauthenticated; administration routes require a `SharedAccessSignature` authorization header. SAS signatures are accepted but not validated, so the emulator must never be exposed remotely.

## Aspire

Set `NIMBUS_SB_EMULATOR=true` when running `src/NimBus.AppHost`. The AppHost creates the emulator project, injects `ConnectionStrings__servicebus`, waits for readiness, and runs the normal topology provisioner.

The Aspire integration is also available directly:

```csharp
// From the NimBus sources, by project path:
var servicebus = builder.AddNimBusServiceBusEmulatorProject(
    "servicebus", "../NimBus.ServiceBusEmulator/NimBus.ServiceBusEmulator.csproj");
builder.AddDotnetProject("provisioner", "../../samples/AspirePubSub/AspirePubSub.Provisioner/AspirePubSub.Provisioner.csproj")
    .WithReference(servicebus.ConnectionString)
    .WaitFor(servicebus.Project);

// Without the sources, from the Akaule.NimBus.ServiceBusEmulator tool package:
var packaged = builder.AddNimBusServiceBusEmulator("servicebus");
```

`AddNimBusServiceBusEmulator<TProject>(name)` and `AddNimBusServiceBusEmulator(name, projectPath)`
are obsolete: they rest on Aspire's `AddProject`, which Aspire 17 retires. They still work and are
removed in the next major version.

## Storage and limits

Messages, locks, and session state are in memory and are lost on restart. Topology is replayed from an atomically written JSON journal before health becomes ready. Override its location with `NIMBUS_SBEMULATOR_TOPOLOGY_PATH`; Aspire assigns a resource-specific default beneath the operating-system temporary directory.

The broker memory budget defaults to 512 MiB and can be set with `NIMBUS_SBEMULATOR_MAX_STORED_BYTES`. The per-message limit defaults to 256 KiB and can be raised, up to 1 MiB, with `NIMBUS_SBEMULATOR_MAX_MESSAGE_SIZE`. Invalid or incompatible topology journals are renamed with a `.corrupt-{timestamp}` suffix and the broker starts empty so the provisioner can recover it.

Batch sends are atomic across subscriptions and forwarding destinations. Each stored body copy and session state value counts toward the shared budget. A scheduled message that reaches its activation time without room for all delivery copies stays scheduled and cancellable. The broker retries activation when space becomes available or the topology changes. When resuming a paused forwarding subscription, a pending message whose destination exceeds the budget goes to the source transfer dead-letter queue.

The emulator supports the NimBus topic/subscription surface: SQL rules and SET actions, sessions and session state, peek, scheduling/cancellation, settlement, delivery limits, TTL, runtime properties, auto-forwarding, regular dead-letter browsing, and the narrow cross-entity transaction used to complete and replay one dead-lettered Resolver message. Queues, general-purpose transactions, transfer dead-letter browsing, WebSockets, Entra ID, duplicate-detection enforcement, and Service Bus message deferral are intentionally unsupported.

Explicit receivers can lock an empty session id immediately. Transactional completion outside the supported dead-letter replay path is rejected with `amqp:not-implemented` instead of leaving the SDK waiting for settlement.

NimBus detects `UseDevelopmentEmulator=true`, so local `Deferred` subscriptions retain the existing one-hour development TTL profile even though the emulator itself does not impose Azure's emulator TTL cap.
