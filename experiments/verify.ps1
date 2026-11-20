[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$experimentRoot = $PSScriptRoot
$repositoryRoot = Split-Path -Parent $experimentRoot

function Invoke-DotNetCheck([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet verification failed with exit code $LASTEXITCODE" }
}

Push-Location $repositoryRoot
try {
    Invoke-DotNetCheck @('build', 'experiments/UnifiedDesktop.Tests/UnifiedDesktop.Tests.csproj', '-c', 'Release', '-warnaserror')
    Invoke-DotNetCheck @('test', 'experiments/UnifiedDesktop.Tests/UnifiedDesktop.Tests.csproj', '-c', 'Release', '--no-build')
    Invoke-DotNetCheck @('run', '--project', 'experiments/UnifiedDesktop', '-c', 'Release', '--no-build')
    Invoke-DotNetCheck @('build', 'DesktopTwin.sln', '-c', 'Release', '-warnaserror')
    Invoke-DotNetCheck @('test', 'DesktopTwin.sln', '-c', 'Release', '--no-build')
}
finally { Pop-Location }
