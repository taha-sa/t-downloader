<#
.SYNOPSIS
  T-Downloader native messaging host kaydini kurar (HKCU, yonetici gerekmez).
.DESCRIPTION
  1) Host manifest JSON'unu %APPDATA%\T-Downloader\native-host\ altina yazar.
  2) Chrome ve Edge icin HKCU\Software\...\NativeMessagingHosts\com.tdownloader.host
     anahtarinin varsayilan degerine bu JSON'un yolunu yazar.
  -DryRun veya -WhatIf ile hicbir sey yazilmaz, yapilacaklar gosterilir.
.PARAMETER HostExe
  TDownloader.Host.exe yolu. Verilmezse src\TDownloader.Host\bin\<Config>\net10.0\ altinda aranir.
  Host, kendi klasorundeki TDownloader.exe'yi baslatir; App ayni klasore kopyalanmis olmali.
.PARAMETER ExtensionId
  Eklenti kimligi. Varsayilan: manifest.json'daki sabit key'den turetilen kimlik.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$HostExe,
    [string]$ExtensionId = 'lgceenlgaaekpbeifoookmmkjipmcboc',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$HostName = 'com.tdownloader.host'
$repo = Split-Path -Parent $PSScriptRoot
$template = Join-Path $PSScriptRoot "$HostName.json"

if (-not $HostExe) {
    foreach ($c in @($Configuration, 'Release', 'Debug')) {
        $cand = Join-Path $repo "src\TDownloader.Host\bin\$c\net10.0\TDownloader.Host.exe"
        if (Test-Path $cand) { $HostExe = $cand; break }
    }
}
if (-not $HostExe) { throw "TDownloader.Host.exe bulunamadi. Once derleyin veya -HostExe verin." }
$HostExe = [System.IO.Path]::GetFullPath($HostExe)
$dry = $DryRun.IsPresent -or $WhatIfPreference

if (-not (Test-Path $HostExe)) {
    if ($dry) { Write-Warning "Host exe yok (DryRun, devam): $HostExe" }
    else { throw "Host exe yok: $HostExe" }
}
if (-not (Test-Path (Join-Path (Split-Path $HostExe) 'TDownloader.exe'))) {
    Write-Warning "Host klasorunde TDownloader.exe yok; App kapaliyken otomatik baslatilamaz."
}

$destDir = Join-Path $env:APPDATA 'T-Downloader\native-host'
$destJson = Join-Path $destDir "$HostName.json"
# JSON'da ters bolu cift olmali; ConvertTo-Json ile guvenli uretim.
$manifest = [ordered]@{
    name            = $HostName
    description     = 'T-Downloader native messaging host'
    path            = $HostExe
    type            = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
}
$jsonText = $manifest | ConvertTo-Json -Depth 4

$keys = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$HostName"
)

if ($dry) {
    Write-Host "[DryRun] Hicbir sey yazilmayacak. Yapilacaklar:"
    Write-Host "[DryRun] Dosya yaz : $destJson"
    $jsonText -split "`r?`n" | ForEach-Object { Write-Host "[DryRun]     $_" }
    foreach ($k in $keys) { Write-Host "[DryRun] Registry  : $k  (Default) = $destJson" }
    return
}

New-Item -ItemType Directory -Force -Path $destDir | Out-Null
[System.IO.File]::WriteAllText($destJson, $jsonText, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Yazildi: $destJson"
foreach ($k in $keys) {
    if ($PSCmdlet.ShouldProcess($k, "Varsayilan degeri $destJson yap")) {
        New-Item -Path $k -Force | Out-Null
        Set-ItemProperty -Path $k -Name '(default)' -Value $destJson
        Write-Host "Kayit: $k"
    }
}
Write-Host "Tamam. Tarayiciyi yeniden baslatmaya gerek yok; eklentiyi yeniden yukleyin."
