param([switch]$Check)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$catalog = [ordered]@{}
foreach ($language in @('en', 'tr', 'de')) {
    $suffix = if ($language -eq 'en') { '' } else { '.' + $language }
    [xml]$resource = Get-Content -LiteralPath (Join-Path $repoRoot "src/TDownloader.Core/Localization/Strings$suffix.resx") -Encoding UTF8 -Raw
    $values = [ordered]@{}
    foreach ($entry in $resource.root.data) {
        if ($entry.name.StartsWith('Ext')) { $values[$entry.name] = [string]$entry.value }
    }
    $catalog[$language] = $values
}
$json = ConvertTo-Json -InputObject $catalog -Depth 5 -Compress
$runtime = @'
// Generated from Strings*.resx by tools/Export-ExtensionStrings.ps1.
(() => {
  const catalog = __CATALOG__;
  let locale = (globalThis.navigator?.language || 'en').toLowerCase();
  globalThis.TDownloaderLocale = {
    get language() { return locale; },
    set(value) { locale = (value || globalThis.navigator?.language || 'en').toLowerCase(); },
    t(key, ...args) {
      const language = locale.split('-')[0];
      return (catalog[language]?.[key] ?? catalog.en[key] ?? key).replace(/\{(\d+)\}/g, (_, i) => String(args[+i] ?? ''));
    }
  };
})();
'@
$runtime = $runtime.Replace('__CATALOG__', $json).Replace("`r`n", "`n") + "`n"
$utf8 = New-Object System.Text.UTF8Encoding($false)
function WriteOrCheck([string]$relative, [string]$content) {
    $path = Join-Path $repoRoot $relative
    if ($Check) {
        if (-not (Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -cne $content) { throw "Generated localization file is out of date: $relative" }
    } else {
        [IO.Directory]::CreateDirectory((Split-Path $path -Parent)) | Out-Null
        [IO.File]::WriteAllText($path, $content, $utf8)
    }
}
WriteOrCheck 'extension/localization.js' $runtime
foreach ($language in @('en', 'tr', 'de')) {
    $description = [ordered]@{ description = [ordered]@{ message = $catalog[$language]['ExtDescription'] } }
    WriteOrCheck "extension/_locales/$language/messages.json" ((ConvertTo-Json $description -Depth 4 -Compress) + "`n")
}
