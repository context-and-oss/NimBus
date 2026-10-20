# Fix the nullable warnings and make them fail Release

## Outcome

Implemented on branch `fix/nullable-warnings`. All 30 projects are clean, the legacy list and its
exemption are gone, and a nullable warning in any project now fails Release. Where a value can
really be null, the contract now says so. A few `!`s remain at the points listed below.

Remaining `!` worth revisiting:

- `IReceivedMessage.EventTypeId` and `CloudEvent.Id`/`Type` stay non-null. Only an empty or invalid
  message, which is dead-lettered, has them null, and annotating them would push `!` into nearly
  every handler.
- `MessageEntity.EndpointId`: the Resolver passes `From!` for a response; a response without
  `From` would store a null endpoint, as before.
- `CosmosDbClient.RemoveMessage`/`UploadFailedMessage`/`UploadSkippedMessage` take a non-null
  session id that `UnresolvedEvent.SessionId` declares optional; callers pass `SessionId!`.
- The NSwag-generated `EndpointStatusCount.SubscriptionStatus` is non-null in `api-spec.yaml`, but
  the API returns null for unknown endpoints.

## Problem

`Directory.Build.props` exempted the CS86xx/CS87xx nullable warnings from Release's
`TreatWarningsAsErrors`, so they accumulated silently: 1,182 distinct nullable warnings in 30
projects at `bf878c59`, while CI stayed green.

## Approach

- `NimBusNullableLegacyProjects` in `Directory.Build.props` lists the projects that still carry
  nullable warnings. Only those keep the exemption. Every other project fails Release on a new
  nullable warning from the first commit.
- Fix projects bottom-up in dependency order, because annotating a lower layer moves warnings
  into its consumers. Remove each project from the list in the commit that makes it clean.
  Delete the list and its exemption block when it is empty.

| Round | Projects |
|------|----------|
| 1 | NimBus.Abstractions |
| 2 | NimBus.Core, NimBus (sample platform) |
| 3 | NimBus.MessageStore.Abstractions, NimBus.ServiceBus, NimBus.Extensions.Notifications, NimBus.Outbox.SqlServer, NimBus.Management.ServiceBus |
| 4 | NimBus.Manager, NimBus.MessageStore.CosmosDb, NimBus.MessageStore.SqlServer, NimBus.SDK |
| 5 | NimBus.Testing, NimBus.Resolver, NimBus.CommandLine |
| 6 | NimBus.WebApp |
| 7 | Test projects and samples |

## Rules for each fix

- Behavior must not change. Use compile-time annotations (`?`, `!`, `[NotNullWhen]`,
  `[MaybeNullWhen]`, `[MemberNotNull]`, `[NotNull]`), or local null handling where the code
  already treats null as a valid case.
- Annotate the truth. A member that can be null becomes nullable. `!` / `= null!` is only for a
  value an invariant guarantees: set by DI, by a deserializer, or by a required construction path.
- Don't add initializers such as `= string.Empty` or `= new()` to serialized models, entities or
  DTOs. They change JSON and stored output (Newtonsoft, Cosmos, SQL).
- Don't add `required` to public types (source-breaking) or new `throw`s on existing paths.
- No `#nullable disable`, `#pragma warning disable CS86xx` or `<NoWarn>` suppressions.
- Interface implementations must match the interface's annotations. A mismatch (CS8767) fails
  Release.
- Don't edit generated code (`ApiContract.g.cs`, `ClientApp/src/api-client`).

## Verification

- After each round: `dotnet build src/NimBus.slnx -c Release -p:SkipSpaBuild=true` with 0 errors,
  and the round's projects have 0 nullable warnings.
- At the end: a Release build with no nullable warnings anywhere and the exemption removed,
  then `dotnet test src/NimBus.slnx -c Release --no-build`. Report skipped live-provider suites.
