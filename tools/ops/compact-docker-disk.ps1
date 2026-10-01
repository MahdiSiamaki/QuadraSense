<#
.SYNOPSIS
    Shrinks the Docker Desktop data disk (docker_data.vhdx) to what it actually holds.

.DESCRIPTION
    The disk only ever grows: ClickHouse writes new parts, merges them and deletes the old
    ones, and the freed space stays allocated in the .vhdx file. On 2026-10-01 the file was
    238 GiB while the filesystem inside it held 206 GiB, and the partition it lived on ran out.

    Two steps, in this order:

      1. Trim, with Docker running - tells the virtual disk which blocks are free. Without it
         the compaction below cannot tell free blocks from used ones and reclaims nothing:

           docker exec --privileged sqm-clickhouse fstrim -v /var/lib/clickhouse

      2. Compact, with Docker Desktop stopped - this script. It needs an elevated PowerShell
         (diskpart), which is why it is a separate step.

    Read-only attach: diskpart compacts a virtual disk attached read-only, so nothing inside
    the filesystem is touched.

.PARAMETER Path
    The .vhdx to compact. Defaults to Docker Desktop's configured disk image location.

.EXAMPLE
    # From an elevated PowerShell, with Docker Desktop stopped:
    powershell -ExecutionPolicy Bypass -File tools\ops\compact-docker-disk.ps1
#>
param(
    [string]$Path
)

$ErrorActionPreference = 'Stop'

if (-not $Path) {
    $settings = Join-Path $env:APPDATA 'Docker\settings-store.json'
    $dir = (Get-Content $settings -Raw | ConvertFrom-Json).CustomWslDistroDir
    if (-not $dir) { throw "No CustomWslDistroDir in $settings; pass -Path." }
    $Path = Join-Path $dir 'disk\docker_data.vhdx'
}

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'diskpart needs an elevated PowerShell: right-click PowerShell, Run as administrator.'
}

if (-not (Test-Path $Path)) { throw "Not found: $Path" }

if (Get-Process 'com.docker.backend' -ErrorAction SilentlyContinue) {
    throw 'Docker Desktop is running. Stop it first: docker desktop stop'
}

# The file must not be open - a disk still attached to the WSL VM would be compacted under it.
try { [IO.File]::Open($Path, 'Open', 'Read', 'None').Close() }
catch { throw "The disk is in use (is WSL still holding it? try: wsl --shutdown): $Path" }

$before = (Get-Item $Path).Length
Write-Host ("Compacting {0}" -f $Path)
Write-Host ("  before: {0:N1} GiB" -f ($before / 1GB))

$script = New-TemporaryFile
@"
select vdisk file="$Path"
attach vdisk readonly
compact vdisk
detach vdisk
exit
"@ | Set-Content -Path $script -Encoding ascii

$started = Get-Date
diskpart /s $script | Out-Host
$code = $LASTEXITCODE
Remove-Item $script

if ($code -ne 0) { throw "diskpart exited with $code" }

$after = (Get-Item $Path).Length
Write-Host ("  after:  {0:N1} GiB  ({1:N1} GiB reclaimed in {2:N0} min)" -f `
    ($after / 1GB), (($before - $after) / 1GB), ((Get-Date) - $started).TotalMinutes)
Write-Host 'Done. Start Docker Desktop again: docker desktop start'
