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
  [Parameter(Position = 1)][string]$Notes = '',
  [string]$MinVersion,
  [string[]]$BlockedVersions = @(),
  [string]$KeyPath,
  [string]$ApplicationId,
  [ValidateSet('stable', 'beta')][string]$Channel = 'stable',
  [string]$OutputDirectory,
  [string]$ReleaseRepo = $env:TSUITE_RELEASE_REPO,
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
$SuiteRoot  = if ($env:TSuiteRoot) { $env:TSuiteRoot } else { 'C:\Users\hp\Projects\t-suite' }
$GhOwner    = 'taha-sa'
# =============================================================

if ($ApplicationId) { $AppId = $ApplicationId }
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Surum bicimi X.Y.Z olmali: $Version" }
$repo   = if ($ReleaseRepo) { $ReleaseRepo } else { "$GhOwner/$AppId-releases" }
$out    = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot 'dist-release' }
$pub    = Join-Path $out 'pub'
$keyPem = if ($KeyPath) { (Resolve-Path -LiteralPath $KeyPath).Path } else { Join-Path $env:USERPROFILE ".t-suite\keys\$AppId.pem" }
if (-not (Test-Path $keyPem)) { throw "Ozel anahtar yok: $keyPem (once: dotnet run --project $SuiteRoot\tools\keygen -- gen $AppId)" }

if ($OutputDirectory -and (Test-Path -LiteralPath $out)) { throw 'OutputDirectory already exists; choose a fresh directory.' }
if (-not $OutputDirectory) {
    $expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist-release'))
    if ([IO.Path]::GetFullPath($out) -ne $expected) { throw 'Invalid release output path' }
    Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory $pub -Force | Out-Null

Write-Host "==> publish App + Host $Version"
$asm = ($Version -replace '-.*$', '') + '.0'
foreach ($proj in @($AppProject, $HostProject)) {
  dotnet publish (Join-Path $PSScriptRoot $proj) -c Release -r $Rid --self-contained false `
    -p:Version=$Version -p:AssemblyVersion=$asm "-p:TSuiteRoot=$SuiteRoot" -o $pub
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish basarisiz: $proj" }
}
foreach ($must in @('TDownloader.exe', 'TDownloader.Host.exe', 'TSuite.Updater.exe', 'TSuite.Updater.dll', 'TSuite.Updater.runtimeconfig.json', 'TSuite.Updater.deps.json')) {
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
$signArgs = @('sign', $AppId, $Version, $sha, $keyPem, '--channel', $Channel)
if ($MinVersion) { $signArgs += @('--min-version', $MinVersion) }
if ($BlockedVersions.Count) { $signArgs += @('--blocked-versions', ($BlockedVersions -join ',')) }
$signatureOutput = dotnet run --project $keygenProj -c Release -v q -- @signArgs
if ($LASTEXITCODE -ne 0) { throw 'imza uretilemedi' }
$sig = [string]($signatureOutput | Select-Object -Last 1)
$signatures = $sig.Trim() | ConvertFrom-Json
$sig = $signatures.signature
if (-not $sig) { throw 'imza uretilemedi' }

$url = "https://github.com/$repo/releases/download/v$Version/$ArtifactAs"
$latest = [ordered]@{
  version   = $Version
  notes     = $Notes
  pub_date  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
  url       = $url
  sha256    = $sha
  signature = $sig
  signature2 = $signatures.signature2
  channel   = $Channel
}
if ($MinVersion) { $latest.min_version = $MinVersion }
if ($BlockedVersions.Count) { $latest.blocked_versions = @($BlockedVersions) }
$latestPath = Join-Path $out 'latest-beta.json'
[IO.File]::WriteAllText($latestPath, ($latest | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
$manifestPaths = @($latestPath)
if ($Channel -eq 'stable') {
  $stablePath = Join-Path $out 'latest.json'
  Copy-Item -LiteralPath $latestPath -Destination $stablePath
  $manifestPaths += $stablePath
}
Write-Host "==> $latestPath yazildi"

if ($Publish -and -not $NoPublish) {
  Write-Host "==> gh release create v$Version --repo $repo"
  $releaseArgs = @('release', 'create', "v$Version", $artifact) + $manifestPaths + @( '--repo', $repo, '--title', "$AppId v$Version")
  if ($Notes) { $releaseArgs += @('--notes', $Notes) } else { $releaseArgs += '--generate-notes' }
  if ($Channel -eq 'beta') { $releaseArgs += '--prerelease' }
  gh @releaseArgs
  if ($LASTEXITCODE -ne 0) { throw 'gh release create basarisiz' }
  if ($Channel -eq 'beta') {
    # /releases/latest excludes prereleases: put the beta pointer beside the stable manifest.
    $stableTag = gh api "repos/$repo/releases/latest" --jq '.tag_name'
    if ($LASTEXITCODE -ne 0) { throw 'Publish a stable baseline before beta releases' }
    gh release upload $stableTag $latestPath --repo $repo --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Beta pointer upload failed' }
  }
} else {
  Write-Host "==> YAYINLANMADI (-Publish verilmedi). Dosyalar: $out"
}
