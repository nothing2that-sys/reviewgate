<#
.SYNOPSIS
  Removes the per-user ReviewGate install made by scripts\install.ps1.
  Settings, keys and history in %USERPROFILE%\.reviewgate are kept.
#>
param([string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\ReviewGate'))
$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
$cli = Join-Path $Destination 'cli'

$envKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
$raw = [string]$envKey.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
$kept = @($raw -split ';' | Where-Object { $_ -ne '' -and $_.TrimEnd('\') -ine $cli.TrimEnd('\') })
$envKey.SetValue('Path', ($kept -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString)
$envKey.Close()
[Environment]::SetEnvironmentVariable('REVIEWGATE_CLI', $null, 'User')

$link = Join-Path ([Environment]::GetFolderPath('Programs')) 'ReviewGate.lnk'
if (Test-Path $link) { Remove-Item $link -Force }
if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
Write-Host "Removed ReviewGate from $Destination (settings in $env:USERPROFILE\.reviewgate kept)."
