# Failure agent (reference runner)

A scheduled, unattended agent that handles failed NimBus messages
([Spec 044](../../docs/spec/044-agent-first/spec.md) Phase 4). For every failure it reaches one of
four outcomes and leaves the record in NimBus: **retry** a transient failure, **report data
quality** to the source's owner, **register a bug and draft a fix** in the adapter's repository, or
**hand it to a person**.

The agent belongs to you. It runs outside NimBus, with your model and your repository and tracker
credentials. NimBus gives it the evidence, records what it concluded, carries out retries through the
Resolver, and enforces the limits: roles, the Owner's switches, a resubmit budget per agent and a cap
per message. See the [failure agent guide](../../docs/failure-agent.md) for the setup.

## What the runner does

`FailureAgent.Runner` is a small .NET console:

1. Gets an app-only Entra token for the NimBus MCP resource with `DefaultAzureCredential`
   (workload identity federation in CI, managed identity in Azure, or a client secret), or none in
   `LocalDevelopment` mode against the Aspire stack.
2. Calls `nimbus_get_capabilities` and stops unless the token is a workload that may record
   classifications. It notes the remaining resubmit budget.
3. Fetches the `handle-failures` prompt with `unattended: true`.
4. Writes a temporary MCP configuration readable only by the current user (it holds the bearer
   token) and runs the coding agent in print mode:

   ```text
   claude -p --strict-mcp-config --mcp-config <file> --permission-mode dontAsk
          --allowedTools mcp__nimbus,Read,Edit,Write,Glob,Grep,Bash(git *),Bash(gh *),Bash(dotnet *)
          --disallowedTools mcp__nimbus__nimbus_skip_message,WebFetch,WebSearch
          --output-format json --max-budget-usd <n> --no-session-persistence
   ```

   with the prompt on standard input, and deletes the configuration afterwards.
5. Prints the agent's summary table and the run's cost. A failed run exits non-zero.

Claude Code is the reference agent; set `FailureAgent:AgentCommand` and adapt
`AgentInvocation.Arguments` for another CLI. Nothing in NimBus depends on it.

## Configuration

Bound from the `FailureAgent` section: `appsettings.json` next to the runner, environment variables
(`FailureAgent__McpUrl`), or command-line arguments (`--FailureAgent:McpUrl=…`). See
[appsettings.example.json](FailureAgent.Runner/appsettings.example.json).

| Setting | Meaning |
| --- | --- |
| `McpUrl` | The operator MCP endpoint, `https://<webapp>/mcp` |
| `Authentication` | `Entra` (default) or `LocalDevelopment` |
| `Scope` | `api://<mcp-resource-app-id>/.default` (Entra) |
| `EndpointId` | Only this endpoint; empty for every endpoint the runner can read |
| `Period` | `1h`, `12h`, `1d` (default), `3d`, `7d` or `30d` |
| `WorkingDirectory` | An empty directory the agent may clone adapter repositories into |
| `AgentCommand`, `Model` | The agent CLI (default `claude`) and an optional model |
| `MaxBudgetUsd` | The most a run may spend on model calls (default 5) |
| `MaxRunMinutes` | The longest a run may take (default 40); it must fit in the token's lifetime |
| `AppendSystemPrompt` | Extra instructions, for example which tracker to use |

## Scheduling

- [templates/github-failure-agent.yml](templates/github-failure-agent.yml): GitHub Actions every 30
  minutes, signing in to Entra with OIDC and to the adapter repositories with a GitHub App.
- [templates/azure-pipelines-failure-agent.yml](templates/azure-pipelines-failure-agent.yml): the
  Azure DevOps variant, for adapter-kit adapters whose repositories live in Azure DevOps.

Neither runs in this repository; copy one into the repository that holds your copy of the runner.

## Trying it on CrmErpDemo

With CrmErpDemo running (`aspire run` in `samples/CrmErpDemo`; it serves `/mcp` and records
classifications without a provider), run [scripts/crmerp-exit.ps1](scripts/crmerp-exit.ps1). It seeds a
transient failure, a source-data failure (`contract_schema`) and an expired-credentials failure, turns
error mode off, and runs the agent in `LocalDevelopment` mode with a folder as its tracker. Expected:
the transient failure is retried and Completed, the source-data failure is reported under a ticket,
and the credentials failure is left for a person. `LocalDevelopment` proves the workflow, not the
Entra app roles and the Owner's switches; run the same against an Azure deployment with a workload
identity for that.
