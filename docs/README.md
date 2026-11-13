# NimBus documentation

NimBus is an open-source .NET integration platform on Azure Service Bus. It connects
business systems with session-ordered processing, tracks every message through a central
Resolver, and gives operators a management WebApp to investigate failures and resubmit or
skip blocked work.

## Start here

| If you want to | Read |
| --- | --- |
| Run NimBus on your machine | [Getting started](getting-started.md) |
| See what the platform does | [Features](features.md) |
| Understand the components and how they fit | [Architecture](architecture.md) |
| Take a customer project from zero to go-live | [Onboarding a customer project](onboarding.md) |

## How it works

- [Message flows](message-flows.md) covers how messages move through the Service Bus topology.
- [Error handling](error-handling.md) covers retries, dead-lettering, and resubmit or skip.
- [Deferred messages](deferred-messages.md) covers how a blocked session parks and replays work.
- [Consumer inbox](inbox-pattern.md) covers idempotent handling of redelivered messages.
- [Pending handoff](pending-handoff.md) covers long-running work completed outside the handler.
- [Orchestration](orchestration.md) covers multi-step processes without a saga framework.
- [Circuit breaker](circuit-breaker.md) and [Platform heartbeat](heartbeat.md) cover endpoint health.

## Build integrations

- [Building adapters](building-adapters.md) is the main guide for publishers, handlers and hosting.
- [Azure Functions hosting](azure-functions-hosting.md) covers subscribers on Azure Functions.
- [Pipeline middleware](pipeline-middleware.md) covers cross-cutting behaviors around handlers.
- [Testing](testing.md) covers the in-memory transport and the test fixture.
- [Extensions](extensions.md), [Notification channels](notifications.md) and
  [Integration Intelligence](integration-intelligence.md) cover the optional packages.
- [CloudEvents interoperability](cloudevents.md) and [AsyncAPI mapping](asyncapi-mapping.md)
  cover working with systems that do not use NimBus.

## Deploy and operate

- [Azure requirements](azure-requirements.md) and [Deployment](deployment.md) cover the
  infrastructure and the pipelines.
- [Private networking](private-networking.md) and [Authentication and identity](authentication.md)
  cover network isolation and access.
- [Storage providers](storage-providers.md) covers Cosmos DB and SQL Server.
- [Rate limiting](rate-limiting.md) and [Throughput tuning](throughput-tuning.md) cover load.
- [Operator MCP server](mcp-server.md) covers operating NimBus from an AI agent.
- [Failure-handling agent](failure-agent.md) sets up an unattended agent that retries, reports and fixes failed messages.
- [Reconciling stale Pending rows](stale-pending-reconcile.md) and
  [Service Bus subscriptions](service-bus-subscription-admin.md) are incident runbooks.

## Management WebApp

- [Monitor wall](webapp-monitor.md)
- [Failed messages](webapp-failed-messages.md)
- [Failed message insights](webapp-insights.md)
- [Simulating traffic](webapp-simulate.md)

## Reference

- [CLI (`nb`)](cli.md)
- [SDK API](sdk-api-reference.md)
- [WebApp REST API](webapp-rest-api.md)
- [Architecture decision records](adr/README.md)

## Project

- [Versioning and releases](versioning.md)
- [Where contributions are most welcome](contribution-areas.md)
- [Dependency security](dependency-security.md)
