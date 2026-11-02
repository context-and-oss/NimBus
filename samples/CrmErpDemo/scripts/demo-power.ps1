<#
.SYNOPSIS
    Turns the deployed CrmErpDemo (Azure Container Apps) off or on to save compute cost.

.DESCRIPTION
    Every app is pinned to one always-on replica, so the demo bills compute around the clock.
    'off' deactivates each app's latest ready revision (0 replicas, no compute billed); 'on'
    reactivates it. SQL, Service Bus, the registry and the provisioner job stay as they are, so
    no data is lost and those keep their fixed monthly cost. A redeploy (`aspire deploy`) turns
    everything back on.

    'on' leaves enrichment-agent off unless -IncludeEnrichmentAgent is passed: deployed without
    an agent API key it loops on 401s and floods the logs.

    Requires the Azure CLI with the containerapp extension, signed in to the demo's subscription.

.EXAMPLE
    ./demo-power.ps1 status

.EXAMPLE
    ./demo-power.ps1 off

.EXAMPLE
    ./demo-power.ps1 on -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('on', 'off', 'status')]
    [string] $Action,

    [string] $ResourceGroup = 'rg-crmerp-demo',

    [switch] $IncludeEnrichmentAgent
)

$ErrorActionPreference = 'Stop'

function Get-DemoApp {
    $json = az containerapp list -g $ResourceGroup --only-show-errors -o json `
        --query "[].{name:name, revision:properties.latestReadyRevisionName, latest:properties.latestRevisionName, fqdn:properties.configuration.ingress.fqdn, external:properties.configuration.ingress.external}"
    if ($LASTEXITCODE -ne 0) { throw "Could not list container apps in '$ResourceGroup' (run 'az login'?)." }

    # Assign first: Windows PowerShell's ConvertFrom-Json emits an array as one pipeline object.
    $list = ($json -join "`n") | ConvertFrom-Json
    foreach ($app in ($list | Sort-Object name)) {
        $revision = if ($app.revision) { $app.revision } else { $app.latest }
        $rev = az containerapp revision show -g $ResourceGroup -n $app.name --revision $revision --only-show-errors -o json `
            --query "{active:properties.active, replicas:properties.replicas, state:properties.runningState}" |
            ConvertFrom-Json
        [pscustomobject]@{
            App      = $app.name
            Revision = $revision
            Active   = [bool] $rev.active
            Replicas = $rev.replicas
            State    = $rev.state
            Url      = if ($app.external -and $app.fqdn) { "https://$($app.fqdn)" } else { '' }
        }
    }
}

$apps = @(Get-DemoApp)
if ($apps.Count -eq 0) { throw "No container apps found in '$ResourceGroup'." }

if ($Action -ne 'status') {
    $activate = $Action -eq 'on'
    $verb = if ($activate) { 'activate' } else { 'deactivate' }

    foreach ($app in $apps) {
        if ($activate -and $app.App -eq 'enrichment-agent' -and -not $IncludeEnrichmentAgent) {
            Write-Host "skip       $($app.App) (pass -IncludeEnrichmentAgent to start it)"
            continue
        }
        if ($app.Active -eq $activate) {
            Write-Host "skip       $($app.App) (already $($Action))"
            continue
        }
        if (-not $PSCmdlet.ShouldProcess("$($app.App) ($($app.Revision))", $verb)) { continue }

        Write-Host ("{0,-10} {1} ({2})" -f $verb, $app.App, $app.Revision)
        az containerapp revision $verb -g $ResourceGroup -n $app.App --revision $app.Revision --only-show-errors -o none
        if ($LASTEXITCODE -ne 0) { Write-Warning "Failed to $verb $($app.App)." }
    }

    Write-Host ''
    if (-not $WhatIfPreference) { $apps = @(Get-DemoApp) }
}

$apps | Format-Table App, Active, Replicas, State, Url -AutoSize
