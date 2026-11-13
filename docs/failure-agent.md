# Failure-handling agent

An agent you run handles failed messages without a person
([Spec 044](spec/044-agent-first/spec.md)). For every failure it records a classification in NimBus
and reaches one of four outcomes:

| Outcome | When | What the agent does |
| --- | --- | --- |
| Retry | Guidance `RetryMayHelp` | Resubmits within its budget and polls `nimbus_get_operation` |
| Report data quality | `ChangeLikelyRequired`, origin `source_data` | Files a report for the source's owner, reusing a ticket for a repeated pattern, and marks the failures reported |
| Register a bug and draft a fix | `ChangeLikelyRequired`, origin `adapter_code` | Opens an issue and a draft pull request with a regression test in the adapter's repository, marks the failures reported, and resubmits them by ticket after the fix ships |
| Hand to a person | Anything else | Leaves its classification and rationale |

The agent never merges, never skips a message, never edits a payload and never files anything
against the NimBus repository. NimBus calls no model and holds no repository or tracker credentials.
The reference runner is [samples/FailureAgent](../samples/FailureAgent/README.md).

## 1. Give the runner an identity

1. **Register an application** for the runner in Entra (for example "NimBus failure agent"). It
   needs no API permissions of its own and no Azure subscription access.
2. **Let it sign in without a secret where you can.** In GitHub Actions add a federated credential
   for the repository and branch that run it; in Azure use a managed identity; elsewhere use a client
   secret (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_CLIENT_SECRET`).
3. **Assign app roles on the NimBus MCP resource** (the registration described in
   [MCP server](mcp-server.md#azure-entra-id)) to the runner's service principal. App roles for
   applications are assigned with Microsoft Graph, through `appRoleAssignedTo` on the MCP resource's
   service principal (the Entra admin center's **Users and groups** page assigns roles to users and
   groups only):

   | Role | Admits |
   | --- | --- |
   | `Nimbus.Observe` | The read tools |
   | `Nimbus.Classify` | `nimbus_record_classification` |
   | `Nimbus.Resubmit` | `nimbus_prepare_action` and `nimbus_resubmit_message`, within the budget |
   | `Nimbus.Annotate` | `nimbus_set_message_reported` |

   Define the last two as app roles on the MCP resource registration first if your registration
   predates them (allowed member type **Applications**).
4. **Grant its object id a NimBus role** in **Settings → Access Control**: Contributor on the
   endpoints it should act on (Reader is enough to look).

The runner requests a token for `api://<mcp-resource-app-id>/.default`. A token for a workload
carries the roles in `roles` and no `scp` claim; NimBus names it after its client in audit rows
(`app:<client id>`).

## 2. Allow it in NimBus

In **Settings → MCP access** (site Owner):

- **Unattended workloads** on.
- **Workloads may record classifications** on.
- **Workloads may resubmit** on, with **Resubmits per agent per day** (default 20) and
  **Unsuccessful resubmits per message** (default 2). The per-minute change limit still applies.
- **Workloads may mark messages reported** on, if the agent should link failures to tickets.
- Optionally approve the runner's client id under **Client applications**, so no other app-only
  client gets in.

In **Settings → Failure intelligence**: failure classification on with `Provider: None`. Turn on
**Agents may see redacted payloads** and **Agents may see stack traces** only if your
data-sharing policy allows it; both are off by default. Stack traces exist only for subscribers that
set `IncludeStackTraceInErrorResponses` (see [Building adapters](building-adapters.md)).

On each endpoint's **Ownership** tab (endpoint Owner): the source system, the adapter's repository
and the ticket link. Without a repository the agent hands adapter defects to a person.

## 3. Give it a repository token that cannot ship

The agent opens issues and draft pull requests in the adapters' repositories with a token of its
own. Use one that:

- covers the adapters' repositories only, never the NimBus repository;
- can create branches, issues and pull requests (GitHub App permissions: contents, issues and pull
  requests, read and write);
- cannot merge or push to protected branches: protect each default branch with required reviews and
  checks, and give the token no bypass.

Review and CI decide what ships. The prompt tells the agent never to copy payload values into an
issue, a report, a pull request or a test.

## 4. Schedule it

Copy a template from [samples/FailureAgent/templates](../samples/FailureAgent/README.md#scheduling)
into the repository that holds your copy of the runner. The runner exits non-zero when the agent
fails, and prints its summary table and cost. Each run is bounded by `MaxBudgetUsd` and
`MaxRunMinutes`; the token must outlive the run.

The runner must reach `/mcp`: with [private networking](private-networking.md) that means a runner inside
the network, for example a self-hosted runner or an Azure Container Apps job.

## 5. Try it locally

Under Aspire, `/mcp` needs no sign-in (local development, loopback only). Run the runner with
`FailureAgent:Authentication=LocalDevelopment` on the same machine; in CrmErpDemo,
[crmerp-exit.ps1](../samples/FailureAgent/README.md#trying-it-on-crmerpdemo) seeds failures and runs it.
Local development acts as the "Local Developer" user, so it proves the workflow but not the app roles,
switches and budget: verify those against a deployment with a workload identity.

## What NimBus enforces

| Limit | Where |
| --- | --- |
| Which tools a workload may use | App roles plus the Owner's switches; skip has no workload role |
| Which endpoints | Access Control, the hidden endpoints and the change scope in **MCP access** |
| How many resubmits | The budget per client and the cap per message, counted from the [operation journal](mcp-server.md#operation-journal) |
| That nothing runs twice | Idempotency keys; an `OutcomeUnknown` operation is never resent |
| What it can see | Redaction, and the payload and stack-trace switches |

Every recording, resubmit and report is audited with the client application.
