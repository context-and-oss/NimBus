<#
.SYNOPSIS
    Spec 044 Phase 4 exit run on CrmErpDemo: seed three failures with different causes, then let
    the failure agent handle them unattended.

.DESCRIPTION
    Run by hand against a running CrmErpDemo (aspire run in samples/CrmErpDemo). A real model
    costs money and does not answer the same way every time, so this is not part of the nightly
    e2e suite.

    1. Seeds one failure per reason, each on its own CRM account so each blocks only its own
       session: transient_dependency, contract_schema and authentication_configuration.
    2. Turns error mode off, so a retry of the transient failure can succeed.
    3. Runs FailureAgent.Runner in LocalDevelopment mode. The tracker for the run is a folder:
       each report or bug is a markdown file whose name is the ticket id.
    4. Prints the ErpEndpoint counts before and after, and the reports the agent wrote.

    Expected: the transient failure is resubmitted and Completed; contract_schema is classified
    source_data with ChangeLikelyRequired and marked reported under a ticket; the credentials
    failure is classified configuration and left Failed for a person. Check the classifications on
    each message's intelligence card in nimbus-ops.

.PARAMETER CrmApi
    crm-api base address (Aspire pins http://localhost:5080).
.PARAMETER ErpApi
    erp-api base address (Aspire pins http://localhost:5090).
.PARAMETER NimBusOps
    nimbus-ops HTTPS base address (Aspire pins https://localhost:18443). The dev certificate must
    be trusted by .NET and, for Claude Code, by Node (NODE_EXTRA_CA_CERTS).
.PARAMETER SkipRunner
    Only seed the failures; run the agent yourself (for example from Claude Code with
    handle-failures).
#>
[CmdletBinding()]
param(
    [string]$CrmApi = "http://localhost:5080",
    [string]$ErpApi = "http://localhost:5090",
    [string]$NimBusOps = "https://localhost:18443",
    [decimal]$MaxBudgetUsd = 3,
    [switch]$SkipRunner
)

$ErrorActionPreference = "Stop"
$endpoint = "ErpEndpoint"
$reasons = @("transient_dependency", "contract_schema", "authentication_configuration")
$headers = @{ "api-version" = "2" }

function Get-ErpCounts {
    Invoke-RestMethod -Method Get -Uri "$NimBusOps/api/endpoint/$endpoint/status/count" -Headers $headers -SkipCertificateCheck
}

function Set-ErrorMode([bool]$Enabled, [string]$Reason) {
    $body = if ($Reason) { @{ enabled = $Enabled; reason = $Reason } } else { @{ enabled = $Enabled } }
    Invoke-RestMethod -Method Put -Uri "$ErpApi/api/admin/error-mode" -ContentType "application/json" -Body ($body | ConvertTo-Json) | Out-Null
}

$before = Get-ErpCounts
Write-Host "ErpEndpoint before: failed $($before.failedCount), deferred $($before.deferredCount)"

try {
    foreach ($reason in $reasons) {
        Set-ErrorMode $true $reason
        $account = @{ legalName = "Exit run $reason $([guid]::NewGuid().ToString('N').Substring(0, 6))"; countryCode = "DK" }
        $created = Invoke-RestMethod -Method Post -Uri "$CrmApi/api/accounts" -ContentType "application/json" -Body ($account | ConvertTo-Json)
        Write-Host "Seeded $reason on account $($created.id)"

        # Wait for this failure before switching the reason: the adapter reads it per message.
        $deadline = (Get-Date).AddSeconds(90)
        do {
            Start-Sleep -Seconds 3
            $now = Get-ErpCounts
        } while ($now.failedCount -le $before.failedCount + [array]::IndexOf($reasons, $reason) -and (Get-Date) -lt $deadline)
        if ($now.failedCount -le $before.failedCount + [array]::IndexOf($reasons, $reason)) {
            throw "The $reason failure did not arrive within 90 seconds."
        }
    }
}
finally {
    # A retry can only succeed with error mode off.
    Set-ErrorMode $false $null
}

$seeded = Get-ErpCounts
Write-Host "ErpEndpoint after seeding: failed $($seeded.failedCount)"
if ($SkipRunner) { return }

$work = Join-Path ([System.IO.Path]::GetTempPath()) "nimbus-exit-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Path (Join-Path $work "reports") | Out-Null
$runner = Join-Path $PSScriptRoot "..\FailureAgent.Runner\FailureAgent.Runner.csproj"

$env:FailureAgent__McpUrl = "$NimBusOps/mcp"
$env:FailureAgent__Authentication = "LocalDevelopment"
$env:FailureAgent__EndpointId = $endpoint
$env:FailureAgent__Period = "1h"
$env:FailureAgent__WorkingDirectory = $work
$env:FailureAgent__MaxBudgetUsd = "$MaxBudgetUsd"
$env:FailureAgent__AppendSystemPrompt = "Your issue tracker is the folder ./reports in your working directory. File each data-quality report or bug as a markdown file named REPORT-<n>.md and use REPORT-<n> as its ticket id. There is no adapter repository for this demo: hand adapter defects to a person."

dotnet run --project $runner
$exit = $LASTEXITCODE

$after = Get-ErpCounts
Write-Host "ErpEndpoint after the run: failed $($after.failedCount) (seeded $($seeded.failedCount); expected one fewer)"
Get-ChildItem (Join-Path $work "reports") -Filter *.md | ForEach-Object { Write-Host "Report $($_.BaseName):"; Get-Content $_.FullName | Select-Object -First 12 }
exit $exit
