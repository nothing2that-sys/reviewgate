<#
.SYNOPSIS
  Installs ReviewGate for the current Windows user so `reviewgate` works from any folder.

.DESCRIPTION
  - Publishes the CLI to <Destination>\cli and the desktop app to <Destination>\desktop.
  - Adds <Destination>\cli to the user PATH, so `reviewgate ...` works in any new terminal.
  - Sets the user variable REVIEWGATE_CLI to the full path of reviewgate.exe (for tools such as HGLOS).
  - Adds a Start menu shortcut "ReviewGate" for the desktop app.
  Settings, keys and history live in %USERPROFILE%\.reviewgate and are not touched.
  Run it again to update. scripts\uninstall.ps1 removes it.
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\ReviewGate'),
    [switch]$NoDesktop
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$Destination = [IO.Path]::GetFullPath($Destination)

function Assert-NotRunning([string]$folder) {
    if (-not (Test-Path $folder)) { return }
    $prefix = $folder.TrimEnd('\') + '\'
    $running = Get-Process -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    }
    if ($running) {
        throw "Close these first (they run from $folder): " + (($running | ForEach-Object { "$($_.ProcessName) ($($_.Id))" }) -join ', ')
    }
}

function Publish-Into([string]$project, [string]$target) {
    $staging = "$target.new"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    & dotnet publish (Join-Path $repo $project) -c Release -o $staging --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
    Assert-NotRunning $target
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Move-Item $staging $target
}

New-Item -ItemType Directory -Force $Destination | Out-Null
$cli = Join-Path $Destination 'cli'
Publish-Into 'src\ReviewGate.Cli' $cli
# Same .NET app host under a short name; it still loads ReviewGate.Cli.dll next to it.
Copy-Item (Join-Path $cli 'ReviewGate.Cli.exe') (Join-Path $cli 'reviewgate.exe') -Force
$exe = Join-Path $cli 'reviewgate.exe'

if (-not $NoDesktop) {
    $desktop = Join-Path $Destination 'desktop'
    Publish-Into 'src\ReviewGate.Desktop' $desktop
    $programs = [Environment]::GetFolderPath('Programs')
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path $programs 'ReviewGate.lnk'))
    $link.TargetPath = Join-Path $desktop 'ReviewGate.Desktop.exe'
    $link.WorkingDirectory = $desktop
    $link.Description = 'ReviewGate'
    $link.Save()
}

# User PATH: edit the raw registry value so %VARS% in it stay unexpanded.
$envKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
$raw = [string]$envKey.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
$parts = @($raw -split ';' | Where-Object { $_ -ne '' })
if (-not ($parts | Where-Object { $_.TrimEnd('\') -ieq $cli.TrimEnd('\') })) {
    $envKey.SetValue('Path', (($parts + $cli) -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString)
    $pathAdded = $true
}
$envKey.Close()
# Setting a user variable through .NET also tells running programs that the environment changed.
[Environment]::SetEnvironmentVariable('REVIEWGATE_CLI', $exe, 'User')

$commit = (& git -C $repo rev-parse --short HEAD 2>$null)
$dirty = [bool](& git -C $repo status --porcelain 2>$null)
[ordered]@{
    installed_at = (Get-Date).ToString('o')
    source = $repo
    commit = $commit
    source_had_uncommitted_changes = $dirty
    cli = $exe
    desktop = $(if ($NoDesktop) { $null } else { Join-Path $Destination 'desktop\ReviewGate.Desktop.exe' })
} | ConvertTo-Json | Set-Content (Join-Path $Destination 'install.json') -Encoding utf8

Write-Host "Installed ReviewGate ($commit) to $Destination"
Write-Host "  CLI:  $exe"
Write-Host "  REVIEWGATE_CLI = $exe"
if ($pathAdded) { Write-Host "  Added $cli to your user PATH." } else { Write-Host "  $cli was already on your user PATH." }
Write-Host 'Open a new terminal (or restart the app that launches agents) to pick up PATH.'
