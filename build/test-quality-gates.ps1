# Prove the gate rejects missing reports, skipped tests and insufficient coverage.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$directory = Join-Path ([IO.Path]::GetTempPath()) ('DesktopTwin-quality-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
$trx = Join-Path $directory 'tests.trx'
$coverage = Join-Path $directory 'coverage.cobertura.xml'
$policy = Join-Path $directory 'policy.json'
$gate = Join-Path $PSScriptRoot 'verify-test-results.ps1'

function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw 'An invalid quality report was accepted.' }
}

try {
    Set-Content -LiteralPath $policy -Value '{"Example.Core":{"line":0.9,"branch":0.8}}' -Encoding UTF8
    Assert-Rejected { & $gate -ResultsDirectory $directory -PolicyPath $policy }
    Set-Content -LiteralPath $trx -Value '<TestRun><ResultSummary><Counters total="1" executed="0" passed="0" /></ResultSummary></TestRun>' -Encoding UTF8
    Assert-Rejected { & $gate -ResultsDirectory $directory -PolicyPath $policy }
    Set-Content -LiteralPath $trx -Value '<TestRun><ResultSummary><Counters total="1" executed="1" passed="1" /></ResultSummary></TestRun>' -Encoding UTF8
    Assert-Rejected { & $gate -ResultsDirectory $directory -PolicyPath $policy }
    Set-Content -LiteralPath $coverage -Value '<coverage line-rate="0.1"><packages><package><classes><class name="Example.Core" line-rate="0.89" branch-rate="1" /></classes></package></packages></coverage>' -Encoding UTF8
    Assert-Rejected { & $gate -ResultsDirectory $directory -PolicyPath $policy }
    Set-Content -LiteralPath $coverage -Value '<coverage line-rate="1"><packages><package><classes><class name="Example.Core" line-rate="1" branch-rate="0.79" /></classes></package></packages></coverage>' -Encoding UTF8
    Assert-Rejected { & $gate -ResultsDirectory $directory -PolicyPath $policy }
    Set-Content -LiteralPath $coverage -Value '<coverage line-rate="1"><packages><package><classes><class name="Example.Core" line-rate="1" branch-rate="1" /></classes></package></packages></coverage>' -Encoding UTF8
    & $gate -ResultsDirectory $directory -PolicyPath $policy
    Set-Content -LiteralPath $policy -Value '{"Example.Core":{"line":0.9,"branch":0.8,"asyncMethods":["RunAsync"]}}' -Encoding UTF8
    Assert-Rejected { & $gate -ResultsDirectory $directory -PolicyPath $policy }
    Set-Content -LiteralPath $coverage -Value '<coverage line-rate="1"><packages><package><classes><class name="Example.Core" line-rate="1" branch-rate="1" /><class name="Example.Core/&lt;RunAsync&gt;d__0" line-rate="0" branch-rate="0" /></classes></package></packages></coverage>' -Encoding UTF8
    Assert-Rejected { & $gate -ResultsDirectory $directory -PolicyPath $policy }
    Set-Content -LiteralPath $coverage -Value '<coverage line-rate="1"><packages><package><classes><class name="Example.Core" line-rate="1" branch-rate="1" /><class name="Example.Core/&lt;RunAsync&gt;d__0" line-rate="1" branch-rate="1" /></classes></package></packages></coverage>' -Encoding UTF8
    & $gate -ResultsDirectory $directory -PolicyPath $policy
    Set-Content -LiteralPath $policy -Value '{"Missing.Class":{"line":0.9,"branch":0.8}}' -Encoding UTF8
    Assert-Rejected { & $gate -ResultsDirectory $directory -PolicyPath $policy }
    Write-Host 'Quality gate negative checks passed.'

    $releaseGate = Join-Path $PSScriptRoot 'verify-release-gates.ps1'
    Set-Content -LiteralPath $policy -Value '{"EXAMPLE001":{"description":"required evidence","tests":["Example.Required"]}}' -Encoding UTF8
    Set-Content -LiteralPath $trx -Value '<TestRun><Results><UnitTestResult testName="Example.Renamed" outcome="Passed" /></Results></TestRun>' -Encoding UTF8
    Assert-Rejected { & $releaseGate -ResultsDirectory $directory -PolicyPath $policy }
    Set-Content -LiteralPath $trx -Value '<TestRun><Results><UnitTestResult testName="Example.Required" outcome="NotExecuted" /></Results></TestRun>' -Encoding UTF8
    Assert-Rejected { & $releaseGate -ResultsDirectory $directory -PolicyPath $policy }
    Set-Content -LiteralPath $trx -Value '<TestRun><Results><UnitTestResult testName="Example.Required" outcome="Failed" /></Results></TestRun>' -Encoding UTF8
    Assert-Rejected { & $releaseGate -ResultsDirectory $directory -PolicyPath $policy }
    Set-Content -LiteralPath $trx -Value '<TestRun><Results><UnitTestResult testName="Example.Required" outcome="Passed" /></Results></TestRun>' -Encoding UTF8
    & $releaseGate -ResultsDirectory $directory -PolicyPath $policy
    Set-Content -LiteralPath $policy -Value '{"EXAMPLE001":{"description":"empty gate","tests":[]}}' -Encoding UTF8
    Assert-Rejected { & $releaseGate -ResultsDirectory $directory -PolicyPath $policy }
    Write-Host 'Registered release gate negative checks passed.'
}
finally {
    # Remove only these explicitly created files, without recursively deleting a computed path.
    foreach ($file in @($trx, $coverage, $policy)) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
    Remove-Item -LiteralPath $directory
}
