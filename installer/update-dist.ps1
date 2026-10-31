# dist-new\ içeriğini dist\ üzerine kopyalar. Tarayıcı yardımcı programı hemen yeniden açabildiği için
# süreçleri kapatıp kopyalamayı birkaç kez dener.
$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $root 'dist-new'
$dst = Join-Path $root 'dist'
for ($i = 1; $i -le 10; $i++) {
    Get-Process TDownloader, TDownloader.Host -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($dst, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force
    try {
        Copy-Item (Join-Path $src '*') $dst -Force -ErrorAction Stop
        Write-Output "Guncellendi (deneme $i)."
        exit 0
    } catch {
        Start-Sleep -Milliseconds 300
    }
}
Write-Output 'Guncellenemedi: dosyalar hala kilitli.'
exit 1
