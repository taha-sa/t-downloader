# T-Downloader

IDM benzeri Windows indirme yöneticisi (C# WinForms, .NET 10) + Chrome/Edge MV3 eklentisi + native messaging host. Sürüm 1.1.0.

## Derleme
T-Suite SDK'ya (`TSuite.Link`, `TSuite.Update`, `TSuite.Updater`) **ProjectReference** ile bağımlıdır. Kardeş depo
`C:\Users\hp\Projects\t-suite` konumunda olmalıdır; `TDownloader.App.csproj` bunu `$(MSBuildThisFileDirectory)..\..\..\Projects\t-suite`
olarak çözer. Başka konum: `dotnet build -p:TSuiteRoot=D:\yol\t-suite`.

```
dotnet build
dotnet test                       # xunit (Core + Host)
cd extension && node tests/run.mjs
```

## Kurulum notları
- `installer\install-host.ps1` native messaging host'u kaydeder. Eklenti 1.1.0 yeni izin (`downloads.ui`) ister: `chrome://extensions` > T-Downloader > **Yenile**.
- Mimari/sözleşme: `docs/CONTRACT.md`. Performans: `docs/performans.md`.
- Sürüm: `release.ps1 <sürüm> "notlar"` (yerel build + SHA-256 + ECDSA imza + `latest.json`; `-Publish` verilmedikçe yayınlamaz).
