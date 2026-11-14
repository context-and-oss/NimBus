#Requires -Version 7.0
<#
.SYNOPSIS
    Audits every npm workspace in the repository and, optionally, applies the
    non-breaking fixes npm can make on its own.

.DESCRIPTION
    Every tracked package-lock.json is one audit root (today: the WebApp ClientApp, the
    CrmErpDemo sample apps and Playwright suite, and the documentation website).

    Both the audit and the fix run with --package-lock-only, so nothing is installed and
    no postinstall script runs (which also keeps Playwright from downloading browsers).
    `npm audit fix` without --force stays inside the semver range declared in
    package.json; anything needing a major bump is reported for a human instead.

.PARAMETER Fix
    Apply `npm audit fix` and leave the updated lockfiles in the working tree.

.EXAMPLE
    pwsh .github/scripts/Invoke-NpmAudit.ps1
    pwsh .github/scripts/Invoke-NpmAudit.ps1 -Fix -JsonOut npm.json -MarkdownOut npm.md
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$Fix,
    [string]$JsonOut,
    [string]$MarkdownOut
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# `npm audit` exits 1 whenever it finds anything, which is the normal case here.
$PSNativeCommandUseErrorActionPreference = $false

if (-not $RepositoryRoot) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
}
$RepositoryRoot = (Resolve-Path $RepositoryRoot).Path

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw 'npm was not found on PATH.'
}

function Get-Property {
    param($InputObject, [Parameter(Mandatory)][string]$Name)

    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# npm audit exits 1 when it finds anything, so the exit code is not an error signal here;
# only a missing/unparsable report is.
function Invoke-NpmAudit {
    param([Parameter(Mandatory)][string]$Directory)

    Push-Location $Directory
    try { $raw = & npm audit --json --package-lock-only 2>&1 }
    finally { Pop-Location }

    $text = ($raw | ForEach-Object { $_.ToString() }) -join "`n"
    $start = $text.IndexOf('{')
    if ($start -lt 0) {
        Write-Warning "npm audit produced no JSON in ${Directory}:`n$text"
        return $null
    }

    try { return $text.Substring($start) | ConvertFrom-Json }
    catch {
        Write-Warning "Could not parse the npm audit report for ${Directory}: $($_.Exception.Message)"
        return $null
    }
}

function ConvertTo-Finding {
    param($Report, [Parameter(Mandatory)][string]$Root)

    $findings = [System.Collections.Generic.List[object]]::new()

    # Guarded rather than inlined: `@($null)` is a one-element array holding $null, so an
    # advisory-free workspace would otherwise iterate once and report a phantom finding.
    $vulnerabilities = Get-Property $Report 'vulnerabilities'
    if ($null -eq $vulnerabilities) { return $findings }

    foreach ($property in $vulnerabilities.PSObject.Properties) {
        $vulnerability = $property.Value

        # `via` holds advisory objects for the offending package itself and plain
        # strings when the package is only affected through a dependency.
        $advisories = @(Get-Property $vulnerability 'via' | Where-Object { $_ -isnot [string] })
        $fix = Get-Property $vulnerability 'fixAvailable'
        $fixDescription = if ($fix -is [bool]) {
            if ($fix) { 'yes' } else { 'no fix published' }
        }
        elseif ($fix) {
            "$(Get-Property $fix 'name')@$(Get-Property $fix 'version')$(if (Get-Property $fix 'isSemVerMajor') { ' (breaking)' })"
        }
        else { 'no fix published' }

        $findings.Add([pscustomobject]@{
                Root       = $Root
                Name       = $property.Name
                Severity   = [string](Get-Property $vulnerability 'severity')
                IsDirect   = [bool](Get-Property $vulnerability 'isDirect')
                Range      = [string](Get-Property $vulnerability 'range')
                Fix        = $fixDescription
                Breaking   = ($fix -isnot [bool] -and $fix -and (Get-Property $fix 'isSemVerMajor') -eq $true)
                # npm repeats an advisory once per affected node in the tree.
                Advisories = @($advisories |
                        ForEach-Object {
                            [pscustomobject]@{
                                Title = [string](Get-Property $_ 'title')
                                Url   = [string](Get-Property $_ 'url')
                            }
                        } |
                        Sort-Object Url -Unique)
            })
    }
    return $findings
}

$lockFiles = & git -c core.quotepath=off ls-files --full-name '*package-lock.json'
if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }
$roots = @($lockFiles |
        Where-Object { $_ -and $_ -notmatch '(^|/)node_modules/' } |
        # Forward slashes so the report reads the same whether it ran on Windows or CI.
        ForEach-Object { (Split-Path $_ -Parent).Replace('\', '/') })

Write-Host "Auditing $($roots.Count) npm workspace(s)..."

$severityRank = @{ 'info' = 0; 'low' = 1; 'moderate' = 2; 'high' = 3; 'critical' = 4 }
$before = [System.Collections.Generic.List[object]]::new()
$after = [System.Collections.Generic.List[object]]::new()
$fixedRoots = [System.Collections.Generic.List[string]]::new()

foreach ($root in $roots) {
    $directory = Join-Path $RepositoryRoot $root
    Write-Host "  auditing $root"

    $initial = @(ConvertTo-Finding (Invoke-NpmAudit $directory) $root)
    $before.AddRange($initial)

    if (-not $Fix -or $initial.Count -eq 0) {
        $after.AddRange($initial)
        continue
    }

    $lockPath = Join-Path $directory 'package-lock.json'
    $hashBefore = (Get-FileHash -LiteralPath $lockPath).Hash

    Push-Location $directory
    try { & npm audit fix --package-lock-only 2>&1 | ForEach-Object { Write-Host "    $_" } }
    finally { Pop-Location }

    if ((Get-FileHash -LiteralPath $lockPath).Hash -ne $hashBefore) {
        $fixedRoots.Add($root)
        Write-Host "  updated $root/package-lock.json"
    }

    $after.AddRange(@(ConvertTo-Finding (Invoke-NpmAudit $directory) $root))
}

$remainingKeys = @($after | ForEach-Object { "$($_.Root)/$($_.Name)" })
$resolved = @($before | Where-Object { "$($_.Root)/$($_.Name)" -notin $remainingKeys })

$markdown = [System.Text.StringBuilder]::new()
[void]$markdown.AppendLine('## npm')
[void]$markdown.AppendLine()

if ($before.Count -eq 0) {
    [void]$markdown.AppendLine('No known vulnerabilities in any npm workspace.')
}
else {
    if ($Fix -and $resolved.Count -gt 0) {
        $lockList = ($fixedRoots | ForEach-Object { '`' + $_ + '/package-lock.json`' }) -join ', '
        [void]$markdown.AppendLine("Fixed $($resolved.Count) advisory package(s) by updating: $lockList")
        [void]$markdown.AppendLine()
        [void]$markdown.AppendLine('| Package | Severity | Workspace |')
        [void]$markdown.AppendLine('| --- | --- | --- |')
        foreach ($finding in $resolved | Sort-Object @{ Expression = { $severityRank[$_.Severity] }; Descending = $true }, Name) {
            [void]$markdown.AppendLine("| $($finding.Name) | $($finding.Severity) | $($finding.Root) |")
        }
        [void]$markdown.AppendLine()
    }

    if ($after.Count -gt 0) {
        [void]$markdown.AppendLine('### Needs a human')
        [void]$markdown.AppendLine()
        [void]$markdown.AppendLine('| Package | Severity | Workspace | Direct | Vulnerable range | Fix | Advisories |')
        [void]$markdown.AppendLine('| --- | --- | --- | --- | --- | --- | --- |')
        foreach ($finding in $after | Sort-Object @{ Expression = { $severityRank[$_.Severity] }; Descending = $true }, Name) {
            $links = ($finding.Advisories | ForEach-Object { "[$($_.Title)]($($_.Url))" }) -join '<br>'
            if (-not $links) { $links = '-' }
            $direct = if ($finding.IsDirect) { 'yes' } else { 'no' }
            [void]$markdown.AppendLine("| $($finding.Name) | $($finding.Severity) | $($finding.Root) | $direct | ``$($finding.Range)`` | $($finding.Fix) | $links |")
        }
    }
    elseif (-not $Fix) {
        [void]$markdown.AppendLine('Run this script with -Fix to apply the available updates.')
    }
}

$report = [pscustomobject]@{
    ecosystem   = 'npm'
    total       = $before.Count
    fixed       = $resolved.Count
    outstanding = $after.Count
    roots       = $fixedRoots
    findings    = $after
}

if ($JsonOut) { $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $JsonOut }
if ($MarkdownOut) { $markdown.ToString() | Set-Content -LiteralPath $MarkdownOut }

if ($env:GITHUB_OUTPUT) {
    # The gate cares about what is still vulnerable after the run, not what was fixed.
    # Assigned before it is formatted: `$null.Severity` is a terminating error under
    # StrictMode, so `?? 'none'` on the property access never gets to default it.
    $worst = $after |
        Sort-Object @{ Expression = { $severityRank[$_.Severity] } } -Descending |
        Select-Object -First 1
    $worstSeverity = if ($worst) { $worst.Severity } else { 'none' }

    @(
        "npm-total=$($before.Count)"
        "npm-fixed=$($resolved.Count)"
        "npm-outstanding=$($after.Count)"
        "npm-max-severity=$worstSeverity"
    ) | Add-Content -LiteralPath $env:GITHUB_OUTPUT
}

Write-Host $markdown.ToString()

# Finding vulnerabilities is a successful run, not a failed one - the workflow's own gate
# step decides whether they are bad enough to fail the job. Without this the script inherits
# $LASTEXITCODE from `npm audit`, which exits 1 whenever it reports anything, and a
# `shell: pwsh` step propagates that. An uncaught error still exits non-zero: the script
# terminates before reaching this line.
exit 0
