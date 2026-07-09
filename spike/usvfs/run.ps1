# run.ps1 — build the USVFS spike harness and run a scenario.
# Usage:
#   .\run.ps1                       # builds, then prints help
#   .\run.ps1 spawn -Exe "C:\Windows\System32\whoami.exe"
#   .\run.ps1 enum
#   .\run.ps1 propagate
#   .\run.ps1 lifetime
#   .\run.ps1 version
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Scenario = 'help',
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest
)

$ErrorActionPreference = 'Stop'
$here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj   = Join-Path $here 'src\UsvfsSpike\UsvfsSpike.csproj'
$binDir = Join-Path $here 'src\UsvfsSpike\bin\Release\net10.0'
$exe    = Join-Path $binDir 'usvfs-spike.exe'

# Ensure binaries are vendored.
if (-not (Test-Path (Join-Path $here 'bin\usvfs_x64.dll'))) {
    throw "usvfs_x64.dll not vendored. Run .\fetch-usvfs.ps1 first."
}

Write-Output "Building harness..."
& dotnet build $proj -c Release -v minimal --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Output "Running: $Scenario $Rest"
& $exe $Scenario @Rest
Write-Output "exit: $LASTEXITCODE"
