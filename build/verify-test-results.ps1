[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ResultsDirectory,
    [string]$PolicyPath = (Join-Path $PSScriptRoot 'coverage-policy.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$results = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter *.trx -Recurse -File)
if ($results.Count -ne 1) { throw "Expected one TRX report; found $($results.Count)." }
[xml]$trx = Get-Content -LiteralPath $results[0].FullName -Raw
if ($trx.TestRun.ResultSummary.outcome -cne 'Completed') {
    throw "Test run did not complete successfully, including fixture cleanup: $($trx.TestRun.ResultSummary.outcome)."
}
$counters = $trx.TestRun.ResultSummary.Counters
$total = [int]$counters.total
if ($total -le 0 -or [int]$counters.executed -ne $total -or [int]$counters.passed -ne $total) {
    throw "Every discovered test must execute and pass; total=$total, executed=$($counters.executed), passed=$($counters.passed)."
}

$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter coverage.cobertura.xml -Recurse -File)
if ($reports.Count -eq 0) { throw 'Missing coverage report.' }
# VSTest can copy the same attachment into its TRX In/ directory.
$hashes = @($reports | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } | Select-Object -Unique)
if ($hashes.Count -ne 1) { throw 'Found multiple distinct coverage reports; use a fresh results directory.' }
[xml]$coverage = Get-Content -LiteralPath $reports[0].FullName -Raw
$classes = @($coverage.SelectNodes('/coverage/packages/package/classes/class'))
if ($classes.Count -eq 0) { throw 'Coverage report has no classes.' }
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
$entries = @($policy.PSObject.Properties)
if ($entries.Count -eq 0) { throw 'Coverage policy must contain at least one class.' }
$failures = @()
foreach ($entry in $entries) {
    # Include async state machines; otherwise a fully untested async body could pass.
    $matches = @($classes | Where-Object {
        $_.name -ceq $entry.Name -or $_.name.StartsWith($entry.Name + '/', [StringComparison]::Ordinal)
    })
    if ($matches.Count -eq 0) {
        $failures += "Missing coverage for $($entry.Name)."
        continue
    }
    $asyncProperty = $entry.Value.PSObject.Properties['asyncMethods']
    if ($null -ne $asyncProperty) {
        foreach ($method in $asyncProperty.Value) {
            $prefix = $entry.Name + '/<' + $method + '>d__'
            if (-not @($matches | Where-Object { $_.name.StartsWith($prefix, [StringComparison]::Ordinal) }).Count) {
                $failures += "Missing async body coverage for $($entry.Name).$method."
            }
        }
    }
    foreach ($class in $matches) {
      foreach ($metric in @('line', 'branch')) {
        $minimum = [double]$entry.Value.$metric
        $actual = [double]::Parse($class.GetAttribute("$metric-rate"), [Globalization.CultureInfo]::InvariantCulture)
        if ([double]::IsNaN($minimum) -or $minimum -le 0 -or $minimum -gt 1) {
            throw "Invalid $metric threshold for $($entry.Name)."
        }
        if ([double]::IsNaN($actual) -or $actual -lt 0 -or $actual -gt 1 -or $actual -lt $minimum) {
            $failures += "$($class.name) $metric coverage: $actual; required: $minimum."
        }
        Write-Host "$($class.name) $metric=$actual (minimum $minimum)"
      }
    }
}
if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
Write-Host "Passed $total tests and all core coverage gates. Full application line coverage: $($coverage.coverage.'line-rate')."
