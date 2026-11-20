[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ResultsDirectory,
    [string]$PolicyPath = (Join-Path $PSScriptRoot 'release-gates.json')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$files = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter *.trx -File)
if ($files.Count -ne 1) { throw 'Release gates require exactly one TRX report.' }
[xml]$report = Get-Content -LiteralPath $files[0].FullName -Raw
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
$gates = @($policy.PSObject.Properties)
if ($gates.Count -eq 0) { throw 'The release gate registry must not be empty.' }
$results = @($report.TestRun.Results.UnitTestResult)
foreach ($gate in $gates) {
    $tests = @($gate.Value.tests)
    if ($tests.Count -eq 0) { throw "Release gate $($gate.Name) has no required tests." }
    foreach ($test in $tests) {
        $evidence = @($results | Where-Object { $_.testName -ceq $test })
        if ($evidence.Count -ne 1 -or $evidence[0].outcome -cne 'Passed') {
            throw "Release blocked by $($gate.Name): $test must exist exactly once and pass."
        }
    }
    Write-Host "$($gate.Name) passed: $($gate.Value.description)"
}
