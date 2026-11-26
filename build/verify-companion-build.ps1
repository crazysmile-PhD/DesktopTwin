[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '../artifacts/companion-build')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$results = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $results -Force | Out-Null

function Invoke-CheckedBuild([string]$Name, [switch]$Rebuild) {
    $arguments = @('build','DesktopTwin.sln','-c',$Configuration,'--no-restore','-warnaserror','--verbosity:quiet')
    if ($Rebuild) { $arguments += '-t:Rebuild' }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @arguments *> (Join-Path $results ($Name + '.log'))
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath (Join-Path $results ($Name + '.log')) | Write-Host
        throw "$Name build failed: $LASTEXITCODE"
    }
    $timer.Stop()
    return $timer.Elapsed.TotalSeconds
}

function Read-Companions {
    $snapshot = @{}
    foreach ($name in @('DesktopTwin.LaunchWorker.exe','DesktopTwin.InputReceiver.exe')) {
        $file = Get-Item -LiteralPath (Join-Path $repo "src/DesktopTwin/obj/$Configuration/net10.0-windows/$name")
        $snapshot[$name] = [ordered]@{
            sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            lastWriteTicks=$file.LastWriteTimeUtc.Ticks
        }
    }
    return $snapshot
}

Push-Location -LiteralPath $repo
try {
    $prepareSeconds = Invoke-CheckedBuild 'prepare'
    $before = Read-Companions
    $unchangedSeconds = Invoke-CheckedBuild 'unchanged'
    $after = Read-Companions
    foreach ($name in $before.Keys) {
        if ($after[$name].sha256 -ne $before[$name].sha256 -or
            $after[$name].lastWriteTicks -ne $before[$name].lastWriteTicks) {
            throw "Unchanged sources rebuilt companion: $name"
        }
    }
    $rebuildSeconds = Invoke-CheckedBuild 'rebuild' -Rebuild
    $rebuilt = Read-Companions
    foreach ($name in $before.Keys) {
        if ($rebuilt[$name].sha256 -ne $before[$name].sha256) {
            throw "Identical companion inputs produced different bytes: $name"
        }
        if ($rebuilt[$name].lastWriteTicks -eq $before[$name].lastWriteTicks) {
            throw "Rebuild did not regenerate companion: $name"
        }
    }
    [ordered]@{
        configuration=$Configuration
        prepareSeconds=$prepareSeconds
        unchangedBuildSeconds=$unchangedSeconds
        rebuildSeconds=$rebuildSeconds
        before=$before; afterUnchangedBuild=$after; afterRebuild=$rebuilt
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $results 'results.json') -Encoding utf8
    Write-Host "Companion gates passed: no unchanged compilation; identical rebuilt bytes. Unchanged build: $([Math]::Round($unchangedSeconds,2))s."
}
finally { Pop-Location }
