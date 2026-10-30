<#
.SYNOPSIS
  install-host.ps1'in yaptiklarini geri alir (HKCU kayitlari + host manifest JSON).
  -DryRun veya -WhatIf ile hicbir sey silinmez.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param([switch]$DryRun)

$ErrorActionPreference = 'Stop'
$HostName = 'com.tdownloader.host'
$destDir = Join-Path $env:APPDATA 'T-Downloader\native-host'
$destJson = Join-Path $destDir "$HostName.json"
$dry = $DryRun.IsPresent -or $WhatIfPreference
$keys = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$HostName",
    "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\$HostName"
)

foreach ($k in $keys) {
    $exists = Test-Path $k
    if ($dry) { Write-Host "[DryRun] Registry sil: $k  (mevcut: $exists)"; continue }
    if ($exists -and $PSCmdlet.ShouldProcess($k, 'Anahtari sil')) {
        Remove-Item -Path $k -Force
        Write-Host "Silindi: $k"
    }
}
if ($dry) { Write-Host "[DryRun] Dosya sil: $destJson  (mevcut: $(Test-Path $destJson))"; return }
if (Test-Path $destJson) {
    Remove-Item -Path $destJson -Force
    Write-Host "Silindi: $destJson"
    if (-not (Get-ChildItem $destDir -Force)) { Remove-Item $destDir -Force }
}
