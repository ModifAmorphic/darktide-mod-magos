# fetch-usvfs.ps1 — vendor the USVFS release binaries into spike/usvfs/bin/.
# One-time setup for the USVFS spike. Idempotent.
#
# Downloads usvfs_v0.5.7.2.7z from the ModOrganizer2/usvfs release, verifies its
# SHA-256, extracts it with the built-in `tar` (libarchive reads .7z; no 7-Zip
# install needed), and copies usvfs_x64.dll + usvfs_proxy_x64.exe into bin/.
# The .7z and full extraction are kept under .cache/ (gitignored).
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$cache = Join-Path $here '.cache'
$bin   = Join-Path $here 'bin'

$version = '0.5.7.2'
$asset   = "usvfs_v$version.7z"
$url     = "https://github.com/ModOrganizer2/usvfs/releases/download/v$version/$asset"
$sha256  = 'c6252eed78ee1c307733a4412cb68522cffc48107be4795c4e38b2b8d7c76d01'

New-Item -ItemType Directory -Force -Path $cache, $bin | Out-Null
$archive = Join-Path $cache $asset

if (-not (Test-Path $archive)) {
    Write-Output "Downloading $url ..."
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $url -OutFile $archive
}

$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLower()
if ($hash -ne $sha256) { throw "SHA-256 mismatch for $archive`n got: $hash`n want: $sha256" }
Write-Output "SHA-256 OK: $hash"

$extracted = Join-Path $cache 'extracted'
if (-not (Test-Path (Join-Path $extracted 'bin'))) {
    Write-Output "Extracting $asset with tar ..."
    New-Item -ItemType Directory -Force -Path $extracted | Out-Null
    & tar -xf $archive -C $extracted
    if ($LASTEXITCODE -ne 0) { throw "tar extraction failed (exit $LASTEXITCODE)" }
}

Copy-Item (Join-Path $extracted 'bin\usvfs_x64.dll')        $bin -Force
Copy-Item (Join-Path $extracted 'bin\usvfs_proxy_x64.exe')  $bin -Force
Write-Output "Vendored into bin/:"
Get-ChildItem $bin | Select-Object Name, Length | Format-Table -AutoSize
Write-Output "Done. Now run: .\run.ps1 <scenario>"
