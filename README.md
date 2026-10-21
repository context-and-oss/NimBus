# NimBus

![NimBus](assets/banner.png)

NimBus is an open-source .NET integration platform built on Azure Service Bus. It keeps related messages in order, tracks their processing history, and gives operators a web console to investigate failures, retry messages, or skip blocked work.

It is designed for teams connecting business systems such as CRM, ERP, and billing, where a failed message must be understood and recovered before subsequent work continues.

[Website](https://context-and-oss.github.io/NimBus/) · [Watch the demo](https://www.youtube.com/watch?v=jZ99gbYZLqU) · [Documentation](docs/)

## What you get

- **Ordered processing and recovery:** messages in the same session at a receiving endpoint are processed in order. A blocked session defers later work until recovery; other sessions can continue.
- **Visibility and control:** a management WebApp for message history, failures, resubmit/skip, and operational metrics.
- **Developer tooling:** a typed publisher/subscriber SDK, configurable retries and middleware, optional SQL outbox and consumer inbox, and a CLI for topology and deployment.
- **Local development:** an Aspire sample with a Service Bus emulator and SQL Server; production message storage supports SQL Server or Cosmos DB.

## How it works

You write adapters that publish business events and handle incoming events. Azure Service Bus routes them between endpoints. The Resolver records processing outcomes in the message store, and operators use the WebApp to inspect and recover failed work.

Deploy the platform into your own environment and integrate applications through the SDK or supported interoperability paths. NimBus builds on Service Bus sessions; it does not make application side effects exactly once. Handlers should remain idempotent.

## Try locally

Start with the [CRM/ERP sample](samples/CrmErpDemo/README.md#running-locally--sql-server-default). It runs the emulator and SQL Server locally, without an Azure account. For Dynamics 365 Sales with Business Central, where CRM owns the pipeline and ERP owns the buying customer, see the [Dynamics 365 Sales ↔ Business Central demo](samples/DynamicsBcDemo/README.md).

You need the .NET 10 SDK, Node.js 22+, and a running Docker-compatible container runtime. The sample guide includes dependency installation and startup commands.

Create an account in CRM, follow it into ERP, then explore its message history in the management WebApp. The demo video shows the failure and recovery workflow.

## Build an integration

```shell
dotnet add package Akaule.NimBus.SDK
```

Follow [Building Adapters](docs/building-adapters.md) to register publishers, handlers, and receiver hosting for a .NET Worker or Azure Function.

Starting a customer project? [Onboarding a Customer Project](docs/onboarding.md) takes a delivery team from prerequisites through catalog design, environments, adapters and pipelines to go-live, using Dynamics 365 Sales and Business Central as the example.

## Learn more

- [Architecture](docs/architecture.md) — components, routing, and recovery.
- [SDK reference](docs/sdk-api-reference.md) — events, handlers, and configuration.
- [Deployment](docs/deployment.md) — Azure setup, prerequisites, and CI/CD.
- [CLI reference](docs/cli.md) — infrastructure, topology, and operations.
- [Contributing](CONTRIBUTING.md) — build, test, and package publishing.

## License

[MIT](LICENSE). An independent implementation inspired by [NServiceBus](https://particular.net/nservicebus).
