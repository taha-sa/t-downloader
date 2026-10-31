# T-Downloader — Mimari Sözleşme

Tüm alt agentlar bu dosyaya uyar. Bu dosyayı yalnızca arayüz/IPC/eklenti sahibi (Agent B) düzenler; diğerleri gerekiyorsa raporda önerir.

## Amaç
IDM benzeri, Windows için indirme yöneticisi. Referans analizler: `C:\Users\hp\idm-analiz\01-ana-program.md`,
`02-entegrasyon.md`, `03-video-dugmesi.md`. Davranış ve ölçüler bunlara göre; **IDM kodu, görselleri, adı kopyalanmaz**
(`btn-assets` yalnızca ölçü referansı, dağıtılmaz/gömülmez). Arayüz dili Türkçe.

## Teknoloji
.NET 10 (SDK kurulu), C#, WinForms. Ek NuGet yalnızca gerçekten gerekiyorsa (test için xunit serbest).

## Klasör yapısı (`C:\Users\hp\T-Downloader`)
- `TDownloader.sln`
- `src/TDownloader.Core` — sınıf kütüphanesi (`net10.0`): indirme motoru, kuyruk, ayarlar, kalıcılık, IPC mesaj modelleri. UI bağımlılığı yok.
- `src/TDownloader.App` — WinForms (`net10.0-windows`), çıktı adı `TDownloader.exe`: ana pencere, tepsi simgesi, tek örnek, IPC sunucusu, video düğmesi penceresi.
- `src/TDownloader.Host` — konsol exe, çıktı adı `TDownloader.Host.exe`: Chrome native messaging host.
- `extension/` — Chrome/Edge MV3 eklentisi.
- `tests/TDownloader.Core.Tests` — xunit.
- `docs/` — bu dosya ve notlar.

## Veri konumları
- Ayarlar: `%APPDATA%\T-Downloader\settings.json`
- İndirme listesi: `%APPDATA%\T-Downloader\downloads.json`
- Parça/geçici veriler: `%APPDATA%\T-Downloader\DwnlData\<downloadId>\`
- Varsayılan kayıt klasörü: `%USERPROFILE%\Downloads\T-Downloader\<Kategori>\`
  Kategoriler: Sıkıştırılmış, Belgeler, Müzik, Programlar, Video, Genel (uzantıya göre).

## İndirme motoru (Core) gereksinimleri
- HTTP/HTTPS (HttpClient), `Range` ile çok bağlantılı indirme; varsayılan 8 bağlantı (ayarlanabilir 1–32).
- Dinamik bölme: biten bağlantı, kalan en büyük parçayı ikiye bölüp yarısını alır (min parça ~1 MB).
- Sunucu Range desteklemiyorsa tek bağlantıya düş.
- Duraklat/devam, uygulama kapanıp açılınca devam (parça durumu diske).
- Hız sınırı (global, KB/s), kuyruk (aynı anda N indirme), ilerleme olayları (bağlantı başına durum dahil).
- Cookie, Referer, User-Agent başlıkları dışarıdan verilebilir.

## IPC
- Named pipe: `\\.\pipe\TDownloader.Ipc` — App sunucu, Host istemci.
- Çerçeve: her mesaj tek satır UTF-8 JSON + `\n`.
- Native messaging (eklenti ↔ Host): Chrome standardı (4 bayt little-endian uzunluk + UTF-8 JSON). Host adı: `com.tdownloader.host`.
- Host, pipe'a bağlanamazsa kendi klasöründeki `TDownloader.exe`'yi `--background` ile başlatır, en fazla 10 sn bağlanmayı dener.

### Mesajlar (alan adları camelCase)
Eklenti → App:
- `{ "type":"ping" }` → `{ "type":"pong", "version":"x.y.z" }`
- `{ "type":"download", "requestId", "url", "referrer", "cookies", "userAgent", "filename", "mime", "fileSize" }`
  → `{ "type":"downloadResult", "requestId", "accepted": true|false }`
- `{ "type":"media", "tabId", "frameId", "pageUrl", "pageTitle", "items":[{ "url","mime","kind":"hls|dash|file" }] }`
  - Geriye uyumlu EK alanlar (isteğe bağlı): mesajda `frameUrl` (isteği yapan iframe belgesi); her öğede `referer`, `origin`, `cookies`, `userAgent`
    (iframe oynatıcıdaki akışı CDN'in beklediği başlıklarla indirmek için; Referer = tarayıcının gönderdiği veya iframe belge adresi).
- `{ "type":"qualities", "requestId", "tabId", "frameId", "pageUrl", "pageTitle", "frameUrl", "src" }` — sayfa içi düğme kalite listesi ister
  (`src`: `<video>` http(s) adresi, blob ise boş). App yanıtı: `{ "type":"qualitiesResult", "requestId", "ok", "error", "errorCode":"no_ytdlp|no_media|error",
  "title", "selectionId", "options":[{ "label","kind":"video|audio|file","height","size","audioOnly","isDefault" }] }`.
  - Geriye uyumlu EK alanlar: `media.items[].size` (bilinen toplam bayt, yoksa -1); `qualities.players` = `[{ "url","label","height" }]` (düğme tıklanınca oynatıcı yapılandırmasından okunan kaynaklar, en fazla 40); `qualitiesResult.options[].kind` ayrıca `"other"` (Diğer dosyalar) olabilir.
- `{ "type":"qualitySelect", "selectionId", "index" }` — menüden seçim; App IDM tarzı "Dosya İndirme Bilgisi" penceresini açar.
- `{ "type":"videoGeometry", "tabId", "windowId", "visible", "fullscreen", "rect":{ "left","top","right","bottom" }, "devicePixelRatio", "viewport":{ "width","height" }, "browserWindow":{ "left","top","width","height" }, "pageUrl", "pageTitle" }`
App → Eklenti:
- `{ "type":"hideButton", "tabId" }`, `{ "type":"settings", "interceptEnabled", "minFileSizeKb", "excludedSites":[...], "fileTypes":[...] }`
  - `settings` EK alanları: `showVideoButton` (sayfa içi düğme açık/kapalı, varsayılan true), `nativeButton` (eski yerel düğme penceresi; varsayılan false).
    `nativeButton=false` iken eklenti `videoGeometry` göndermez, App de yok sayar.

### Video düğmesi (sayfa içi)
Düğme ve kalite menüsü eklentinin `button.js` içerik script'iyle sayfanın DOM'una (Shadow DOM) çizilir (iframe dahil, her çerçeve kendi videosu için).
Yerel düğme penceresi (`videoGeometry` akışı) yalnızca `UseNativeVideoButton` ayarıyla açılır.

## Sınırlar (tüm agentlar)
- DRM aşma, lisans/kayıt mantığı yok. IDM dosyalarını değiştirme/çalıştırma yok.
- Registry'ye yazma yalnızca native messaging host kaydı için (HKCU) ve yalnızca kullanıcı onaylı ayrı bir kurulum adımında; geliştirme sırasında yazılmaz.
- Commit atma serbest (yerel git), push yok.

## Performans/dayanıklılık düzeltmeleri (geriye uyumlu eklemeler)

### İndirme yakalama akışı (IDM tarzı)
- App `download` alınca yakalama kuralları uygunsa (http/https ve yakalama açık) **hemen** `downloadResult accepted:true` döner,
  onay penceresini sonra (sırayla, aynı anda tek pencere) gösterir. Pencerede "İptal" = hiçbir şey inmez; tarayıcıya geri bırakma YOK.
  Kural/kapalı ise hemen `accepted:false` (tarayıcı kendi indirir).
- `download.explicit` (bool, isteğe bağlı, varsayılan false): kullanıcı eylemi (sağ tık menüsü). `true` ise App "tarayıcı yakalama kapalı" ayarını yok sayar.
- IPC sunucusu bağlantı başına mesajları seri beklemez: yavaş işleyici (onay penceresi, yt-dlp) ping/media/qualities akışını tıkamaz;
  yanıtlar tamamlanma sırasıyla yazılır ve `requestId` ile eşleşir. Eşzamanlı tamamlanan mesajların (media, hideButton…) sırası korunur.
- Eklenti zaman aşımı 5 sn → 15 sn (yalnızca App soğuk başlangıcı için). Zaman aşımında tarayıcı indirmeyi sürdürür; App geç de olsa
  `accepted:true` dönerse eklenti tarayıcıdaki kopyayı iptal eder (çift indirme olmaz).
- Sağ tık "Tüm linkleri indir": en fazla 10 isteklik paralel gruplar (seri 5 sn beklemesi yok).

