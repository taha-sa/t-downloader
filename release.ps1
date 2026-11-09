<#
  T-Downloader surum betigi (T-Suite sablonundan uyarlandi: C:\Users\hp\Projects\t-suite\tools\release-template\release.ps1).

  Kullanim:  .\release.ps1 1.2.0 "Surum notlari"            # varsayilan: yerel build + imza, YAYINLAMAZ
             .\release.ps1 1.2.0 "Notlar" -Publish          # gh release create (kullanici onayi gerekir!)

  Cikti: dist-release\T-Downloader.zip + dist-release\latest.json
  Zip icerigi: TDownloader.exe, TDownloader.Host.exe (+ dll'ler), TSuite.Updater.*, extension\ (eklenti dosyalari).
  Guncelleme, zip'i uygulamanin klasorune ACAR (dosya dosya, eskisi .bak): TDownloader.Host.exe'nin YOLU DEGISMEZ,
  yani Chrome native messaging kaydi (installer\install-host.ps1) bozulmaz. Eklenti manifest'i degistiyse kullanici
  chrome://extensions sayfasinda "Yenile" demelidir (eklenti kimligi sabit key ile ayni kalir).
  Ozel anahtar: C:\Users\hp\.t-suite\keys\t-downloader.pem (repo DISINDA; asla commit edilmez)
#>
param(
  [Parameter(Mandatory = $true, Position = 0)][string]$Version,
  [Parameter(Mandatory = $true, Position = 1)][string]$Notes,
  [switch]$Publish,
  [switch]$NoPublish
)
$ErrorActionPreference = 'Stop'

# ===================== UYGULAMA AYARLARI =====================
$AppId      = 't-downloader'
$AppProject = 'src\TDownloader.App\TDownloader.App.csproj'
$HostProject = 'src\TDownloader.Host\TDownloader.Host.csproj'
$ArtifactAs = 'T-Downloader.zip'
$Rid        = 'win-x64'
$SuiteRoot  = 'C:\Users\hp\Projects\t-suite'
$GhOwner    = 'taha-sa'
# =============================================================

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Surum bicimi X.Y.Z olmali: $Version" }
$repo   = "$GhOwner/$AppId-releases"
$out    = Join-Path $PSScriptRoot 'dist-release'
$pub    = Join-Path $out 'pub'
$keyPem = Join-Path $env:USERPROFILE ".t-suite\keys\$AppId.pem"
if (-not (Test-Path $keyPem)) { throw "Ozel anahtar yok: $keyPem (once: dotnet run --project $SuiteRoot\tools\keygen -- gen $AppId)" }

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $pub -Force | Out-Null

Write-Host "==> publish App + Host $Version"
$asm = ($Version -replace '-.*$', '') + '.0'
foreach ($proj in @($AppProject, $HostProject)) {
  dotnet publish (Join-Path $PSScriptRoot $proj) -c Release -r $Rid --self-contained false `
    -p:Version=$Version -p:AssemblyVersion=$asm -o $pub
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish basarisiz: $proj" }
}
foreach ($must in @('TDownloader.exe', 'TDownloader.Host.exe', 'TSuite.Updater.exe')) {
  if (-not (Test-Path (Join-Path $pub $must))) { throw "Yayin ciktisinda eksik: $must" }
}

# Eklenti dosyalari (testler / node paketi haric)
$extDest = Join-Path $pub 'extension'
New-Item -ItemType Directory $extDest -Force | Out-Null
robocopy (Join-Path $PSScriptRoot 'extension') $extDest /E /XD tests node_modules /XF package.json /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy basarisiz ($LASTEXITCODE)" }
$extManifest = (Get-Content (Join-Path $PSScriptRoot 'extension\manifest.json') -Raw | ConvertFrom-Json).version
if ($extManifest -ne $Version) { throw "Eklenti manifest surumu ($extManifest) $Version ile eslesmiyor" }

$artifact = Join-Path $out $ArtifactAs
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $artifact -Force

$sha = (Get-FileHash $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "==> sha256 $sha"

$keygenProj = Join-Path $SuiteRoot 'tools\keygen\keygen.csproj'
$sig = (dotnet run --project $keygenProj -c Release -- sign $AppId $Version $sha | Select-Object -Last 1).Trim()
if (-not $sig) { throw 'imza uretilemedi' }

$url = "https://github.com/$repo/releases/download/v$Version/$ArtifactAs"
$latest = [ordered]@{
  version   = $Version
  notes     = $Notes
  pub_date  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
  url       = $url
  sha256    = $sha
  signature = $sig
}
$latestPath = Join-Path $out 'latest.json'
[IO.File]::WriteAllText($latestPath, ($latest | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
Write-Host "==> $latestPath yazildi"

if ($Publish -and -not $NoPublish) {
  Write-Host "==> gh release create v$Version --repo $repo"
  gh release create "v$Version" $artifact $latestPath --repo $repo --title "$AppId v$Version" --notes $Notes
  if ($LASTEXITCODE -ne 0) { throw 'gh release create basarisiz' }
} else {
  Write-Host "==> YAYINLANMADI (-Publish verilmedi). Dosyalar: $out"
}
