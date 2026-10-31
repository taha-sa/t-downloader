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
- Host, YALNIZCA `download`/`qualities` için ve pipe'a bağlanamazsa kendi klasöründeki `TDownloader.exe`'yi `--background` ile başlatır, en fazla 10 sn bağlanmayı dener. Diğer mesajlar App'i başlatmaz (aşağıda: A5).

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

### Medya mesajı (artımlı) ve çerezler
- `media.append` (bool, isteğe bağlı, varsayılan false): `true` ise `items` yalnızca YENİ öğelerdir ve App çerçevenin mevcut listesine ekler
  (aynı URL atlanır, çerçeve başına en fazla 100); `false`/yok ise App çerçeve listesini DEĞİŞTİRİR (boş liste = temizle; sayfa değişince eklenti böyle gönderir).
- `media.items[].cookies` artık GÖNDERİLMEZ (eski eklenti gönderirse App yine kullanır). Çerez yalnızca düğme tıklanınca,
  `qualities.cookies` = `{ "<origin>": "a=b; c=d" }` (en fazla 12 origin) ile gelir; App bunları sekmedeki çerezsiz öğelere origin'ine göre uygular.
- Eklenti, 600 KB'ı aşan `media` mesajını birden çok `append:true` mesajına böler (Host sınırı 1 MB). Host → tarayıcı yönünde 1 MB'ı aşan
  `qualitiesResult` kırpılarak (seçenek listesi yarılanır) gönderilir ve `host.log`'a yazılır; kırpılamayan mesaj da log'lanır.
- Parça (HLS/DASH segmenti) adresleri (`.ts`, `.m4s`, `video/mp2t`, manifest görülen sekmede `seg-N`/`chunk_N`...) medya listesine girmez.
  `webRequest` yalnızca `media`, `xmlhttprequest`, `other`, `sub_frame` kaynak türlerinde dinlenir; statik kaynaklar (resim/css/js/yazı tipi) başlıklara bakılmadan elenir.
  Sekme başına öğe üst sınırı 120, çerçeve başına 50, toplam grup 200.

### Kalite istekleri
- `{ "type":"qualities", ..., "cookies":{...} }` (yukarıya bakın). `requestId` düğmeden gelebilir.
- `{ "type":"qualitiesCancel", "requestId" }` (eklenti → App): menü kapandı/sekme değişti/sekme kapandı; App ilgili yt-dlp sürecini öldürür
  (başka bekleyen yoksa). App kalite bilgisini kilitli, en çok 50 kayıtlı ve 10 dk ömürlü önbellekte tutar (anahtar imzasız URL),
  aynı kaynak için devam eden işi paylaşır ve aynı anda en fazla 2 `yt-dlp -J` çalıştırır.
- `{ "type":"tabClosed", "tabId" }` (eklenti → App): sekme kapandı; App o sekmenin medya durumunu ve bekleyen kalite isteklerini temizler.


### Host ve eklenti CPU
- Host: App'e ulaşılamadıysa (başlatma başarısız) cooldown (30 sn) bağlanma denemesinden ÖNCE denetlenir; cooldown'da yalnızca tek ucuz (1 ms) bağlanma
  denemesi yapılır (App elle açıldıysa bulunur), yeniden başlatma/uzun bekleme yoktur. `host.log` boyutla döndürülür (1 MB → `host.log.old`).
- Eklenti: `nativeButton=false` iken `content.js` geometri altyapısını (MutationObserver, interval, scroll/resize/mousemove dinleyicileri) hiç kurmaz; ayar
  açılınca kurar, kapanınca söker. `button.js` videoları IntersectionObserver ile izler (mousemove'da `elementsFromPoint`, tüm `<video>` taraması yok);
  250 ms kararlılık zamanlayıcısı yalnızca görünür alanda video varken, rAF döngüsü yalnızca düğme/menü gerekliyken ve video görünürken çalışır.

## Güvenlik ve dayanıklılık düzeltmeleri (docs/review/02-app-ipc-eklenti.md)

### Çerez sınırı (A1)
- `qualities.cookies` = `{ "<origin>": "a=b" }` artık YALNIZCA şu kuralla üretilir: eklenti her origin için çerezi o origin'in kendi adresiyle
  (`chrome.cookies.getAll({url})`, tarayıcının kendi kapsamı) okur. Sayfanın verdiği oynatıcı adresleri (`players`) için çerez OKUNMAZ; sayfa başka sitenin çerezini isteyemez.
- Yeni EK alan `qualities.cookieHosts` = `{ "<origin>": "<host>" }`: çerezin hangi host için okunduğu. App bu host'u `MediaContext.CookieHost` alanına koyar
  (`MediaContexts.ContextOf`); Core çerezi yalnızca bu host'a (ve alt alan adlarına) gönderir. Host'u bildirilmemiş/uyuşmayan origin'e çerez uygulanmaz.
  `qualities.cookieHosts` yoksa (eski eklenti) yalnızca origin eşleşmesi aranır; çerezli bağlamda `CookieHost` boşsa Core çerezi hiçbir isteğe eklemez.
- App, kalite/indirme başlatırken bağlam çerezini kaynak adresinin host'una uygulanamıyorsa atar (`MediaContexts.ForSource`).

### Sayfa içi düğme güven sınırı (A2, A17)
- Shadow root `closed`; düğme/menü/Esc/mousedown/mousemove işleyicileri ve `content.js` Alt tuşu işleyicileri `event.isTrusted` ister:
  sayfa betiği sentetik olayla menü açamaz, `qualities`/`qualitySelect` tetikleyemez, tarayıcının yakalamasını kapatamaz.
- Video düğmesinden gelen indirmeler (`qualitySelect` -> doğrudan dosya) `ShowFileInfoOnCapture` kapalı olsa bile HER ZAMAN "Dosya İndirme Bilgisi" penceresini gösterir.
- `qualitySelect` yanıtı: eklenti içi `sendResponse({ ok })`; host'a yazılamadıysa düğme hata gösterir ("✓ gönderildi" değil).

### Host: App'i ne zaman başlatır (A5, A6)
- Host App'i YALNIZCA `download` ve `qualities` (kullanıcı eylemi) için başlatır. `ping`, `media`, `tabClosed`, `videoGeometry`, `qualitiesCancel`, `qualitySelect`
  App'i ASLA başlatmaz (tepsiden Çıkış sonrası geri gelmez); bağlıysa iletir, değilse 50 ms'lik tek bağlanma denemesinden sonra düşürür.
- App'e ulaşılamazsa `qualities` mesajına HEMEN `{ "type":"qualitiesResult", "requestId", "ok":false, "error":"T-Downloader çalışmıyor...", "errorCode":"error" }` döner
  (menü 100 sn beklemez); `download` için eskisi gibi `accepted:false`.

### IPC pipe (A7)
- Pipe `PipeOptions.CurrentUserOnly` ile açılır/bağlanılır (başka kullanıcı oturumu bağlanamaz, sunucuyu taklit edemez). Kabul döngüsü hiçbir istisnada ölmez
  (günlüğe yazar, 500 ms sonra yeniden dener). Pipe adı değişmedi (`TDownloader.Ipc`).

### Medya durumu tutarlılığı (A8, A9, A12, A15)
- Eklenti `media` gönderemediyse (bağlantı yok) öğeleri "gönderilmiş" saymaz. App bağlandığında ilk mesaj `settings`'tir; eklenti `settings` alınca açık sekmelerin
  tüm medya listesini `media` (`append:false`, devamı `append:true`) ile yeniden gönderir.
- Sekme yeni belgeye gidince/yenilenince (`tabs.onUpdated status=loading`, adres değişmese de) eklenti her zaman `tabClosed { tabId }` gönderir (service worker uyuyup
  grup belleği kaybolsa bile): App o sekmenin medya durumunu ve bekleyen kalite isteklerini temizler. `tabClosed` artık "sıfırla" anlamını da taşır.
- Gizli pencere (`incognito`) indirmeleri yakalanmaz (tarayıcıda kalır).
- `qualities` isteği kimliği `await`'ten önce izlenir; App `qualitiesCancel`'i istek kaydı oluşmadan önce de saklar ve istek başında uygular.

### Onay penceresi tekilleştirme (A4, A13)
- Aynı URL için bekleyen/açık pencere varken tekrar gelen istek yeni pencere açmaz (zaman penceresiyle değil durumla). Pencere "İptal" ile kapanınca talep hemen
  bırakılır: aynı bağlantıya tekrar tıklanınca yeniden sorulur. Yalnızca eklenen indirme 15 sn hatırlanır (tarayıcı çift gönderimi).
- Kullanıcı "Çıkış" derken onay bekleyen pencere varsa App uyarır.

### Diskten Sil (A3)
- Dosyalar `SHFileOperation(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING)` ile Geri Dönüşüm Kutusu'na gönderilir. Kutuya gönderilemeyen dosya için kabuk
  AYRICA "kalıcı silinsin mi?" diye sorar; reddedilirse dosya silinmez ve kullanıcıya bildirilir. Sessiz kalıcı silme yoktur.
