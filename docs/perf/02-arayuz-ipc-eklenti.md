# 02 - Arayüz, IPC ve Tarayıcı Eklentisi: Ağır Yük Altında Performans (teorik analiz)

Kapsam: `src/TDownloader.App` (Forms, Ui, Services), `src/TDownloader.Core/Ipc`, `src/TDownloader.Host`, `extension/*`.
Yöntem: yalnızca kod okuma. Hiçbir şey çalıştırılmadı, ölçüm yok; sayılar büyüklük mertebesi tahminidir.
Güven etiketi: **[Kesin]** = kodda doğrudan görülen davranış (etkisi yine de tahmin olabilir), **[Tahmini]** = çalışma zamanı davranışı veya WinForms/Chrome iç işleyişi varsayımına dayanır.
Satır numaraları ilgili dosyanın kendi satır numarasıdır.

---

## 0. Özet tablo

| # | Önem | Başlık | Senaryo |
|---|------|--------|---------|
| K1 | Kritik | Onay penceresi açıkken IPC bağlantı döngüsü tıkanıyor + eklenti 5 sn zaman aşımı -> çift indirme / yığılma | 4 |
| Y1 | Yüksek | `media` mesajı artımlı değil: her seferinde tüm öğeler + her öğede çerez; 1 MB Host sınırı aşılırsa mesaj sessizce düşer | 3 |
| Y2 | Yüksek | `onResponseStarted` tüm kaynak türlerinde (resim/script/css dahil) çalışıyor; service worker fiilen hiç uyumuyor | 3 |
| Y3 | Yüksek | Uygulama tarafında `_tabs`/`Seen` hiç temizlenmiyor (günlerce çalışmada sınırsız büyüme) | 3, 5 |
| O1 | Orta | `UpdateStatus()` O(n); `SelectedIndexChanged` ve satır silme başına çağrılıyor -> Ctrl+A / toplu silmede O(n^2) | 1 |
| O2 | Orta | `DownloadManager.Get(id)` O(n) + `Items` her çağrıda kopya | 1, 2 |
| O3 | Orta | `_progress` sözlüğü biten indirmeleri silinene kadar tutuyor | 5 |
| O4 | Orta | Owner-draw: `SmallImageList.Images[i]` her hücre çiziminde yeni Bitmap üretir (tahmini) | 1, 2 |
| O5 | Orta | content.js: yerel düğme kapalıyken bile her çerçevede MutationObserver(subtree+attributes), 1.5 sn interval, mousemove/scroll dinleyicileri | 3 |
| O6 | Orta | button.js: her `mousemove`'da tüm `<video>`lar taranıyor; rAF döngüsünde `elementFromPoint` + `getComputedStyle` zinciri | 3 |
| O7 | Orta | Host: App yokken her mesajda 300 ms bağlanma denemesi (seri stdin döngüsü), günlük sınırsız büyüyebilir | 4, 5 |
| O8 | Orta | Pano dinleyici: UI thread'inde senkron `Clipboard.GetText()` (kilitli panoda ~1 sn+ donma) | 5 |
| O9 | Orta | Kabul edilen her indirme için ayrı `ProgressDialog` (50 eşzamanlı = 50 pencere, her 400 ms güncelleme) | 2, 4 |
| O10 | Orta | RebuildList her filtre değişiminde tüm ListViewItem'ları yeniden üretir | 1 |
| D1-D15 | Düşük | Çeşitli (bölüm 3) | |

---

## 1. Mimari özet (okunan sözleşme)

- Eklenti (MV3 SW) <-> Host (native messaging, 4 bayt uzunluk + JSON) <-> App (named pipe, satır-JSON).
- Host, tarayıcıdan gelen her JSON'u `IpcMessage`'a çözer ve pipe'a yeniden serileştirir (`HostRunner.cs:58, 66, 172`).
- App tarafında her bağlantı için **tek bir** `ClientLoopAsync` döngüsü var; mesajlar o bağlantıda **seri** işlenir (`IpcPipe.cs:103-117`).
- Ana pencere: tek `System.Windows.Forms.Timer` (400 ms) ile `Flush()`; engine olayları yalnızca "dirty" kümesine yazar (iyi tasarım).

---

## 2. Bulgular (önem sırasıyla)

### K1 [Kritik][Kesin] Onay penceresi bağlantı döngüsünü kilitliyor; 5 sn zaman aşımı çift indirmeye yol açıyor
Dosyalar: `IpcPipe.cs:103-117`, `IpcCoordinator.cs:33-39`, `MainForm.cs:620-634`, `extension/lib/capture.js:6, 29-45`, `HostRunner.cs:46, 66`.

Ne oluyor:
1. `ClientLoopAsync` bir mesajı `await h(msg, conn)` ile işler; sonraki mesaj ancak işleyici dönünce okunur. `download` işleyicisi kullanıcı "Dosya İndirme Bilgisi" penceresini kapatana kadar bekler (`_dialogGate` + `_promptGate` + modal `ShowDialog`).
2. Bu süre boyunca **aynı Host bağlantısındaki** her şey (media, ping, qualities, qualitySelect, diğer download'lar) pipe tamponunda bekler. Pipe tamponu küçük (varsayılan, KB mertebesi); çerezli download mesajları birkaç tanede tamponu doldurur, Host'un `SendAsync`'i bloklanır, Host'un seri stdin döngüsü (`HostRunner.cs:46`) durur, Chrome tarafında kuyruk büyür.
3. Eklenti `native.request(..., 5000)` ile yalnızca 5 sn bekler (`capture.js:6, 41`). Kullanıcı 5 sn içinde onaylamazsa `accepted=false` sayılır -> `chrome.downloads.resume` ile **tarayıcı kendi indirmesini sürdürür**. App tarafı ise pencereyi göstermeye ve kullanıcı onaylarsa indirmeyi eklemeye devam eder -> aynı dosya iki kez iner. Geç gelen `downloadResult` eklentide eşleşen bekleyen olmadığı için atılır.
4. 50 eşzamanlı yakalamada: 50 istek de t=5 sn'de zaman aşımına düşer; 49'u (ve kullanıcı geç kalırsa 50'si) tarayıcıda iner; App ise sırayla 50 pencere açar (her biri tarayıcının zaten indirdiği dosya için). `TryClaimPrompt` (`MainForm.cs:568-579`) 15 sn penceresi yalnızca **aynı URL**'yi eler; kuyruk bekleme süresi bu pencereyi aşar.
5. Kullanıcı pencerede "İptal" derse `accepted=false` döner ve eklenti yedek olarak tarayıcı indirmesini sürdürür/başlatır (`capture.js:52-54`, `menus.js:24-25`); "iptal" beklenen sonucu vermez.
6. Aynı kilit `getStatus`/ping'i (2 sn zaman aşımı, `background.js:84-91`) ve sayfa içi düğmenin `qualities` isteğini (100 sn) de pencere açıkken bekletir; popup "App yok" gösterebilir.
7. "Tüm linkleri indir" (`menus.js:47`, `for (const u of urls) await queueUrl(u)`): 500 linke kadar **seri**, her biri en çok 5 sn bekler; zaman aşımına düşenler tek tek tarayıcıya yönlendirilir.

Etki: tek indirmede bile kullanıcı 5 sn içinde onay vermezse çift indirme; 50 eşzamanlıda pratikte kullanılamaz davranış ve App'te 50 sıralı pencere. UI maliyeti küçük, doğruluk/kaynak israfı yüksek.

Öneri (kod yazmadan):
- Onayı IPC işleyicisinden ayır: işleyici pencereyi beklemeden dönsün (qualities'teki gibi `Task.Run`), sonuç ayrı bir `downloadResult` ile gelsin.
- Eklentide zaman aşımını "App isteği aldı (ack)" ve "kullanıcı karar verdi" olarak ikiye böl; ack gelince uzun (dakikalar) bekle, ack yoksa 5 sn.
- Bekleyen onayları tek pencerede liste olarak topla (toplu onay); eklenti zaman aşımına düştüyse App'te isteği düşür.
- "İptal" ile "tarayıcı indirsin"i ayır (`accepted:false, reason:"cancelled"`).

### Y1 [Yüksek][Kesin yapı / Tahmini etki] `media` mesajı artımlı değil, çerez öğe başına kopya, Host 1 MB sınırı
Dosyalar: `extension/lib/media.js:57-69, 64-67`, `protocol.js:36-51`, `NativeMessaging.cs:9, 16-30`, `HostRunner.cs:43`.

- Her `flush` o grubun (sekme:çerçeve) **tüm** öğelerini (<=50) yeniden gönderir; yeni URL geldikçe toplam bayt O(n^2) (50 öğe için 1275 öğe kopyası). Her öğe kendi `cookies` dizgisini (tüm `Cookie` başlığı) ve `userAgent`'i taşır; aynı host için aynı çerez 50 kez tekrarlanır.
- Çerez başlığı 4-16 KB olabilen sitelerde 50 öğe x çerez > 1 MB olabilir. Host `MaxMessageBytes` = 1 MB; aşan mesaj okunup atılır ("Gelen mesaj reddedildi", `HostRunner.cs:43`) -> o sekmenin medya listesi App'e hiç ulaşmaz, kullanıcıya belirti yok. [Tahmini: çerez boyutuna bağlı]
- Debounce (400 ms) yeni öğe gelmeye devam ederse flush'ı ertelemeye devam eder (`media.js:53-54`); üst sınır yok.
- Çerez ilk görülmede alınır, yenilenmez (bayat token olabilir). `flush` ile `clearTab` arasında yarış var: `clearTab` boş liste gönderdikten sonra uçuştaki `flush` eski öğeleri yeniden gönderebilir (`media.js:57-69` vs `80-86`).
- Aynı mesaj Host'ta çözülüp yeniden serileştirilir (D3), App'te bir kez daha çözülür: 3 JSON geçişi.

Öneri: yalnızca yeni/değişen öğeleri gönder (delta); çerezi öğe başına değil mesaj/host başına bir kez ver veya yalnızca indirme anında eklentiden iste; toplam boyutu 1 MB altında tutacak üst sınır ve aşılınca çerezsiz gönder.

### Y2 [Yüksek][Kesin yapı / Tahmini etki] webRequest dinleyicileri çok geniş; service worker her zaman uyanık
Dosyalar: `extension/lib/media.js:88-93`, `manifest.json:8-20`, `background.js:42`.

- `onResponseStarted` filtresi `{urls:["<all_urls>"]}` ve **tür filtresi yok**: sayfadaki her resim/script/css/font isteği SW'ye `responseHeaders` ile taşınıyor. `onSendHeaders` ise `xmlhttprequest/media/other` ve `extraHeaders` ile; HLS/DASH segment istekleri (XHR/fetch) her segmentte **iki olay** üretir.
- Her olayda `header()` üç kez tüm başlıkları küçük harfe çevirerek tarıyor (`media.js:8-9, 34-36`), `classifyMedia` üç regex + `split` (`protocol.js:105-118`).
- `chrome.runtime.connectNative` açık olduğu sürece SW sonlandırılmaz; Host, App kapansa bile yaşıyor (`HostRunner.cs:35-54`), yani SW fiilen hiç uyumaz; olay işleme sürekli CPU/pil tüketir. 30 sekme + birkaç yayın: tahminen saniyede yüzlerce-binlerce olay -> SW tarafında bir çekirdeğin %5-20'si mertebesi [Tahmini].
- fMP4 segmentlerini `video/mp4` ile sunan sitelerde her segment "file" sınıflanır (`protocol.js:111-115`; yalnızca `video/mp2t` elenir, :114) ve 50 öğelik grup hızla dolar; sonra gerçek medya `g.items.size >= maxItems` yüzünden **sessizce atılır** (`media.js:46`). SPA gezinmesinde `onUpdated` yalnızca `loading+url` görünce temizler (`background.js:47-49`), listede bayat öğeler kalır.

Öneri: `onResponseStarted` için `types: ["xmlhttprequest","media","other","sub_frame","main_frame"]` (resim/script/css/font/ping/websocket dışarıda); segment benzeri URL'leri (`.ts`, `.m4s`, `seg`/`chunk`) erken ele; grup dolunca en eskiyi çıkar (LRU) veya manifest/m3u8 öğelerine ayrı kota ver; SPA için `webNavigation.onHistoryStateUpdated` ile temizle.

### Y3 [Yüksek][Kesin] App tarafında sekme durumu hiç temizlenmiyor
Dosya: `VideoOverlayMediaSink.cs:32, 67-74, 107-111`.

- `_tabs` yalnızca eklenir; sekme kapanınca/gezinince `TabState` silinmez. Eklentinin `clearTab` mesajı yalnızca `Frames[frame] = []` yapar; `Seen` (url -> sıra, her yeni URL için eklenir, satır 71) **hiç küçülmez**. Tokenlı HLS/segment URL'leri (300-1000 karakter) sekme ömrü boyunca birikir; kapanan sekmelerin kimliği tekrar kullanılmadığı için girdiler ölü ağırlık.
- Günlerce çalışmada tahmini bellek: binlerce sekme x yüzlerce URL x ~0.5 KB -> onlarca MB [Tahmini]; yavaş ama sınırsız.

Öneri: `media` mesajı `items.Count==0 && pageUrl==""` (clear sinyali) gelince `TabState`'i sil/sıfırla; `tabRemoved` mesajı ekle veya LRU (en çok 100-200 sekme); `Seen` için üst sınır (ör. 500) ve sayfa değişince sıfırlama.

### O1 [Orta][Kesin yapı / Tahmini süre] `UpdateStatus()` O(n) ve çok sık çağrılıyor
Dosya: `MainForm.cs:327, 328, 386, 401, 463, 466-477`; `DownloadManager.cs:63-66`.

- `UpdateStatus` her çağrıda `_mgr.Items` (kilit altında `ToList()` kopyası) + iki LINQ geçişi yapar (n=5000: ~100-150 µs).
- Ctrl+A (`MainForm.cs:328`) her öğe için `i.Selected = true` yapar; her biri senkron `SelectedIndexChanged` -> `UpdateStatus`: 5000 öğede 5000 x ~150 µs ~ 0.5-1 sn UI donması (`BeginUpdate` de yok). Shift+tık aralık seçimi benzer olabilir [doğrulanmadı].
- `DeleteSelected`/`DeleteCompleted` (`:672-686`) her öğeyi seri `await RemoveAsync` ile siler; her `ItemRemoved` -> `UiPost(RemoveRow)` -> `UpdateStatus` + `li.Remove()` (olası O(n) kaydırma, `BeginUpdate` yok) -> 5000 silmede O(n^2) ve tekrarlı yeniden çizim.

Öneri: `UpdateStatus`'u zamanlayıcıya bağla (dirty bayrağı); `SelectedIndexChanged` yalnızca seçim etiketini güncellesin; sayaçlar artımlı tutulsun; Ctrl+A'da `BeginUpdate/EndUpdate` + olay bastırma; toplu silmede tek `BeginUpdate` ve `UpdateStatus` sona.

### O2 [Orta][Kesin] `Get(id)` doğrusal, `Items` her seferinde kopya
Dosya: `DownloadManager.cs:63-71`; kullanım `MainForm.cs:447`, `ProgressDialog.cs:125, 166`.

- `Flush` her kirli kimlik için `_mgr.Get(id)` (O(n), dizge karşılaştırması) çağırır. 20 aktif x 5000 öğe = 100 bin karşılaştırma/400 ms (önemsiz); fakat `StartAll`/`PauseAll` gibi toplu işlemlerde 5000 kirli kimlik x 5000 = 25 milyon karşılaştırma ~ 100-250 ms'lik tek seferlik UI duraklaması [Tahmini]. Aynı `_gate` kilidi motor tarafından da kullanıldığı için UI thread kilit beklemesi mümkün.

Öneri: kimlik -> öğe sözlüğü; `Flush`'ta doğrudan `_rows[id].Tag` içindeki `DownloadItem` kullanılabilir; `Items` için kopya yerine sürüm sayacıyla önbelleklenen anlık görüntü.

### O3 [Orta][Kesin] `_progress` sözlüğü sızıntısı
Dosya: `MainForm.cs:46, 102, 399`.
`_progress[p.Id] = p` her ilerleme olayında yazılır; yalnızca `RemoveRow`'da silinir (`:399`). Biten/başarısız/duran indirmelerin son `DownloadProgress`'i (içinde `Connections`, <=32 bağlantı) liste silinene kadar kalır. Günlerce açık, binlerce indirme tamamlanan oturumda onlarca MB'a varabilir (sınırsız ama yavaş).
Öneri: `OnCompleted`/durum `Downloading` dışına geçince `_progress.TryRemove` (`FillRow` yalnızca aktif satırda `p` kullanıyor).

### O4 [Orta][Tahmini] Owner-draw'da `ImageList.Images[i]` her çizimde yeni Bitmap
Dosya: `Ui/Theme.cs:329-334` (`OnDrawSubItem`, sütun 0).
WinForms `ImageList.Images[int]` her çağrıda yeni bir `Bitmap` kopyası üretir (büyük olasılıkla; ölçülmedi). Burada `Dispose` edilmiyor: görünür her satırın her yeniden çiziminde (400 ms'lik hücre güncellemesi, kaydırma, fare üstü) 1 KB'lık GDI+ bitmap üretilip finalizer'a bırakılıyor. 30 görünür satır x 2.5 Hz ~ 75/sn; kaydırmada kat kat fazla. GC baskısı + finalizer yükü; GDI tutamacı sızıntısı değil, ama yerel bellek dalgalanması.
Öneri: `SmallImageList.Draw(g, point, index)` kullan veya ikonları bir kez `Bitmap[]` olarak önbelleğe al.

### O5 [Orta][Kesin yapı / Tahmini etki] content.js, varsayılanda kapalı olan geometri altyapısını yine de çalıştırıyor
Dosya: `extension/content.js:7, 118-123, 135-138, 144, 151-166`; `manifest.json:25-37`.
`nativeGeo` varsayılan false (`:7`), `flush()` boşa dönüyor (`:119`) ama her çerçevede (`all_frames`, `match_about_blank`, `document_start`):
- `MutationObserver(documentElement, subtree+attributes[style,class,hidden])` kurulur (`:159-164`): dinamik/animasyonlu sayfalarda her stil/sınıf değişimi için `MutationRecord` üretir,
- `setInterval(1500)` görünür sekmelerde `schedule()` -> `setTimeout` -> boş `flush` (`:166`),
- `mousemove` / `scroll`(capture) / `resize` / media olayları dinleyicileri (`:151-157`); `blur` her çerçevede `alt(0)` mesajı gönderiyor (`:144`).
Her çerçeve ayrıca `cfg` için SW'ye `sendMessage` atar (`content.js:8` ve `button.js:15` -> çerçeve başına 2 mesaj; 30 sekme x çok çerçeve açılışta yüzlerce mesaj).
Öneri: geometri dinleyicilerini/observer'ı yalnızca `nativeButton=true` olunca kur (`applyCfg` içinde); Alt tuşu ve link toplama kısmını ayrı küçük script yap; `cfg`'yi tek mesajla (yalnızca üst çerçeve) iste veya `storage.onChanged` kullan.

### O6 [Orta][Kesin yapı / Tahmini etki] button.js: mousemove taraması ve kare başına pahalı okuma zinciri
Dosya: `extension/button.js:25-34, 118-140, 142-143, 156-166, 167, 169-180`.
- `mousemove` (`:159-166`) her olayda `[...document.querySelectorAll("video")].find(getBoundingClientRect)` çalıştırır (üst sınır yok; 100 videolu akışta olay başına 100 `getBoundingClientRect`), sonra `qualifies` (closest + gBCR + `getComputedStyle`), `mount`, `place`, `setTimeout/clearTimeout`, `btn.matches(":hover")`. Fare olayları kare hızında geldiği için ~60 Hz x bu zincir; sayfa kendi rAF'ında stil yazıyorsa her okuma zorunlu layout tetikler (layout thrashing).
- rAF döngüsü (`:142`) hedef varken **her karede**: `qualifies` (gBCR + `getComputedStyle`), ikinci `getBoundingClientRect`, `btn.offsetWidth/offsetHeight`, `elementFromPoint` ve en çok 10 atada `getComputedStyle(n).position` (`:118-128`); ardından `btn.style.left/top` yazılır. Kare başına en az 1 zorunlu layout + 1 hit-test + birden çok `CSSStyleDeclaration` ayrılması. Fare video üzerinde oynadıkça döngü +3.5 sn sürüyor (`:164`); izleme sırasında fareyi oynatınca tek çekirdeğin ~%2-5'i ve boşta durumu engeli [Tahmini]. Scroll dinleyicisi (`:167`, capture:true) her kaydırılabilir elemanın scroll'unda `place()` çağırır (rAF ile çift çalışma).
- `setInterval(250)` (`:169-180`) görünür her çerçevede `querySelectorAll("video")` + en çok 6 gBCR (6 sınırı iyi); çok reklam iframe'li sayfada saniyede onlarca uyanma.
- Dar durum: fare düğmenin üstünde 3.5 sn park edilirse `hide()` atlanır; ardından video dışına tek hamlede çıkılırsa `hideT` yeniden kurulmaz, `target` kalır ve rAF döngüsü görünür sekmede sürebilir [Tahmini, nadir].
- Her çerçeve (reklam iframe'leri dahil) `document_idle`'da host div + shadow root + `innerHTML` stil + `<img src=logo>` oluşturur (`:72-96`); `<img>` yüklemesi ağaca bağlı olmasa da başlayabilir -> çerçeve başına `web_accessible_resource` isteği [Tahmini].
Öneri: `mousemove`'u rAF ile throttle et ve aday videoları önbellekten kullan (`IntersectionObserver`/`pointerover`); `place()` okumalarını tek geçişte topla, `coveredAt` kontrolünü 4-8 karede bir veya yalnızca konum değişince yap; rAF yerine `ResizeObserver`+`IntersectionObserver`+scroll ile yalnızca değişimde güncelle; shadow DOM'u ve `<img>`'i ilk hover'a kadar oluşturma (lazy); `hideT` yeniden kurulumunu `mouseleave`'e bağla.

### O7 [Orta][Kesin] Host: App yokken mesaj başına 300 ms; seri döngü; günlük büyümesi
Dosya: `HostRunner.cs:46, 62-72, 87-117 (özellikle 96, 100)`; `HostLog.cs:19, 28`.
- `EnsureConnectedAsync` cooldown kontrolünden **önce** her mesajda 300 ms `ConnectAsync` dener (`:96`). App kapalı ve `RetryCooldown` içindeyken (30 sn) her `media`/`ping`/`download` mesajı ~300-400 ms harcar; stdin döngüsü seri olduğundan saniyede ~3 mesaj işlenir, kalanı Chrome'un kuyruğunda birikir. Çok `media` flush'ı olan sayfada kuyruk büyür; eski (anlamsız) mesajlar sonra işlenir.
- Düşürülen her mesaj için `host.log`'a `File.AppendAllText` (aç-yaz-kapat) yapılır (`:83`). Döndürme (rotation) yalnızca Host başlarken (`HostLog.cs:19`); Host tarayıcı kapanana kadar (günlerce) yaşadığından dosya sınırsız büyüyebilir.
- App başlatma: `TryLaunchApp` + 10 sn bekleme sırasında stdin okunmaz; eklenti zaman aşımı 5 sn olduğundan ilk download kesin tarayıcıya düşer (beklenen), ama ilk mesajda gecikme 10 sn'ye kadar.
- Not: Host yalnızca ilk mesajda pipe'a bağlanıyor (tarayıcı açılışında App'i tetiklemez); eklenti açılışta mesaj göndermez, ilk `media` flush'ı App'i başlatır (tasarım tercihi).
Öneri: cooldown içindeyse bağlanma denemesi yapma (hemen null); `media`/`ping` gibi önemsiz mesajları cooldown boyunca at; günlüğe hız sınırı + çalışırken boyut kontrolü; stdin okuma ile pipe yazmayı ayır (okuyucu görev + sınırlı kanal, taşmada eskiyi at).

### O8 [Orta][Kesin yapı / Tahmini etki] Pano izleme UI thread'inde senkron
Dosya: `MainForm.cs:858-887`.
`WM_CLIPBOARDUPDATE` -> `CheckClipboard` -> `Clipboard.ContainsText()` + `Clipboard.GetText()` UI thread'inde. Pano başka süreç (Office, pano yöneticisi, uzak masaüstü) tarafından tutuluyorsa WinForms 10 x 100 ms yeniden dener (~1 sn donma); gecikmeli işlenen biçimlerde kaynak uygulama yanıt vermiyorsa beklenir. Olay her pano değişiminde (resim, dosya listesi dahil) tetiklenir. `_clipBusy` (`:56, 875, 881-886`) senkron olduğundan işlevsiz.
Öneri: önce ucuz biçim kontrolü (`CF_UNICODETEXT` var mı), okumayı ~200-300 ms debounce et; kısa zaman aşımıyla arka plan STA'da oku. (Dinleyicinin yalnızca ClipboardMonitor açıkken kayıtlı olması iyi.)

### O9 [Orta][Kesin] Her kabul için ayrı ProgressDialog
Dosya: `MainForm.cs:589-590, 646, 656-665, 455-459`; `ProgressDialog.cs:59-199`.
`ShowFileInfoOnCapture=false` veya "Başlat" seçildiğinde her indirme için `ProgressDialog` açılır. 50 eşzamanlı = 50 üst düzey pencere (her biri ~25 kontrol + ListView + owner-draw `DarkGroup`/`SegmentBar`/`DarkProgress`; USER nesnesi tahmini 1000-1500; 10 bin sınırının altında). Her `Flush`'ta açık her pencere için `ApplyProgress` (`:164-199`): 6-8 `Label.Text` (aynı metin atamaları WinForms'ta kısa devre), `ListView.BeginUpdate/EndUpdate` (tüm liste geçersiz), `SegmentBar.Invalidate()` (tüm çubuk; `OnPaint` her renk kümesi için fırça üretir). Görünürlük/simge durumu denetimi yok. Tahmini UI yükü 50 pencerede 5-15 ms/400 ms + boyama; kabul edilebilir ama gereksiz. Asıl maliyet pencere oluşturma (50 x ~10-20 ms) ve odak çalma.
Öneri: tek birleşik "aktif indirmeler" paneli veya yalnızca ilk N pencere; simge durumunda/gizliyken `ApplyProgress`'i atla; `ListView` güncellemesini yalnızca değişen hücrede yap.

### O10 [Orta][Kesin yapı / Tahmini süre] RebuildList her filtre/kategori tıklamasında tüm satırları yeniden üretir
Dosya: `MainForm.cs:310, 366-394, 521-527`.
Her `AfterSelect` -> `Items.Clear()` + n x (`ListViewItem` + 9 alt öğe + `string[]` + `Fmt.Date` x2 + `Fmt.Size`) + `Sort`. 5000 öğede yüzbinlerce nesne; tahminen 100-300 ms UI donması (+ Sort'ta `string.Compare(..., CurrentCultureIgnoreCase)` ~60 bin çağrı ~30 ms). `Match` içinde `f.StartsWith("cat:")` kültüre duyarlı ve `f[4..]` her öğede dize ayırır (`:372`).
Başlangıçta ctor'da (`:97`) tutamaç yokken çalışması iyi (yerel ekleme tutamaç oluşunca toplu); açılış ~100-300 ms tahmini. İlk satır kurulumunda `ShellIcons.IndexFor` her **yeni uzantı** için `SHGetFileInfo` (USEFILEATTRIBUTES -> disk erişimi yok; ~1-10 ms/uzantı; tipik <30 uzantı).
Öneri: `VirtualMode` + filtrelenmiş `List<DownloadItem>` görünümü (owner-draw zaten özel); en azından `ListViewItem` nesnelerini yeniden kullan ve `AddRange` kullan; `StartsWith(..., StringComparison.Ordinal)`.

---

## 3. Düşük önemli bulgular

- **D1 [Düşük][Kesin] Flush gizliyken de çalışıyor** (`MainForm.cs:42, 105, 438-464`): tepsi modunda 400 ms zamanlayıcı ve 20 aktif satırın hücre güncellemeleri boşa gidiyor. Gizliyken yalnızca sayaçları güncelle; görününce senkronla.
- **D2 [Düşük][Kesin] `RunPromptAsync` içinde `TaskCompletionSource` `RunContinuationsAsynchronously` olmadan** (`MainForm.cs:625-631`): `tcs.SetResult` UI thread'inde (modal pencere dönüşünde) devamları senkron çalıştırır; `IpcCoordinator` -> `ClientLoopAsync` sonraki mesajın JSON çözümü, `Media.OnMedia` vb. UI thread'inde işlenebilir [Tahmini]. Ayrıca `UiPost` tutamaç yok/yok edilmişse eylemi **sessizce** atar (`:146-150`); `tcs` hiç tamamlanmaz ve `_promptGate` kalıcı kilitlenir (yalnızca kapanış yarışında; nadir). Öneri: `RunContinuationsAsynchronously`; `UiPost` başarısızlığında `tcs.TrySetResult(false)`.
- **D3 [Düşük-Orta][Kesin] Host JSON'u gereksiz yere çözüp yeniden serileştiriyor** (`HostRunner.cs:58, 66, 155-157, 172`): ham baytı tür alanına bakıp aktarmak yeterli; ayrıca bilinmeyen alanlar sessizce düşer (ileri uyumluluk).
- **D4 [Düşük][Kesin] `BroadcastAsync` seri ve başı-bekleme (head-of-line)** (`IpcPipe.cs:131-139`): bir Host'un stdout'u tıkanırsa (Chrome okumuyor) `_writeLock` yüzünden sonraki istemcilere `settings`/`hideButton` gitmez. Öneri: istemci başına giden kuyruk + zaman aşımı.
- **D5 [Düşük][Kesin] IPC satır uzunluğu sınırı yok; boru erişim denetimi yok** (`IpcPipe.cs:18, 42, 91`): `ReadLineAsync` sınırsız bellek ayırabilir; `NamedPipeServerStream` varsayılan ACL (aynı bilgisayardaki başka süreç `download` göndererek pencere açtırabilir). Öneri: `PipeOptions.CurrentUserOnly` + azami satır uzunluğu (ör. 4 MB).
- **D6 [Düşük][Kesin] Küçük GDI+/finalizer kalıntıları**: `ShellIcons.cs:38, 41, 43` (`ic.ToBitmap()` ve `new Bitmap(16,16)` dispose edilmiyor; uzantı sayısıyla sınırlı); `FileInfoDialog.cs:167-169` (son PictureBox görüntüsü kapanışta dispose edilmiyor); `Theme.UiFont()` form başına yeni `Font` (`Theme.cs:34`, `DluForm`), `VideoOverlayMediaSink.cs:295, 396` menü/öğe başına `new Font`; `Icons.cs:32-33, 90, 154` dispose edilmeyen fırçalar (ikon başına bir kez). Hepsi finalizer ile toplanır; kalıcı GDI tutamaç sızıntısı bulunmadı (bölüm 5).
- **D7 [Düşük][Kesin] `Cmp` bütünlüğü** (`MainForm.cs:493-512`): "eta"/"added" varsayılan `AddedAt`; "speed" motor thread'inin değiştirdiği alanı okur (sıralama sırasında tutarsız karşılaştırma olası). Sıralama `Flush` sonrası korunmuyor (işlevsel).
- **D8 [Düşük][Kesin] `async void DeleteSelected/DeleteCompleted`** (`MainForm.cs:672-686`): `RemoveAsync` istisnası UI SynchronizationContext'e düşer.
- **D9 [Düşük][Kesin] FileInfoDialog**: boyut yoklaması HEAD+GET'te en çok 2 x 8 sn (`FileInfoDialog.cs:172-209`); `Foreground.Force` (TopMost + AttachThreadInput + gerekirse sahte Alt tuşu, `Foreground.cs:369-390`) her pencerede odak çalar; 50 sıralı pencerede kullanıcı yazarken kesinti (UX). `Settings.Save` UI thread'inde senkron (`:147, 228`).
- **D10 [Düşük][Kesin] Yerel düğme yolu (varsayılan kapalı)**: `BrowserWindows.Locate` her geometri/500 ms tick'te `EnumWindows` + pencere başına `StringBuilder` ayırır (`BrowserWindows.cs:32-53`, `VideoOverlayMediaSink.cs:55-64, 164`). Varsayılan akışta çalışmaz.
- **D11 [Düşük][Kesin] `GetQualitiesAsync` her tıklamada ~60-130 küçük dosya işlemi** (`VideoOverlayMediaSink.cs:465-469`; `MediaLog.Write` her satırda `FileInfo`+`AppendAllText`); thread-pool'da ve 200 KB ile sınırlı; kabul edilebilir.
- **D12 [Düşük][Kesin] `manifest.json:63-72` `web_accessible_resources` = `<all_urls>`**: her sitenin eklentiyi tespit etmesine izin verir (sabit `key` ile sabit kimlik) ve (O6) çerçeve başına resim isteği üretebilir. `capture.js:15` `bypass` kümesinin girdileri `downloads.onCreated` gelmezse silinmez (küçük sızıntı); `capture.js:27-33` ile `media.js:20-25` iki özdeş çerez fonksiyonu.
- **D13 [Düşük][Kesin] `onDownloadCreated`** (`capture.js:81-105`): uzantıdan bağımsız **her** tarayıcı indirmesi önce `pause` edilir, dosya adı için en çok 1.2 sn yoklanır (`nameWait`), sonra karar verilir; yakalanmayan indirmelerde de yüzlerce ms'ye kadar gecikme. Öneri: URL/uzantıdan ön eleme, ad belirmeden pause etme.
- **D14 [Düşük][Kesin] `send()` her çağrıda `connect()` deniyor** (`native.js:51-55`): Host kayıtlı değilse (veya hemen kapanıyorsa) her `media` flush'ı yeni `connectNative` denemesi tetikler; `scheduleReconnect` üstel beklemesi (1 sn..5 dk, `:45-49`) atlanıyor. Öneri: `send` bekleme süresindeyken denemesin.
- **D15 [Düşük][Kesin] `broadcastCfg` tüm sekmelere** (`background.js:32-38`): her `settings` iletisinde (Host yeniden bağlanınca `IpcCoordinator.cs:17-21` ve her ayar değişiminde) `tabs.query({})` + her sekmenin tüm çerçevelerine `sendMessage`; `chrome.storage.local.set` her seferinde (`settings.js:26`). Değişmediyse atla.

---

## 4. Senaryo bazlı değerlendirme

### Senaryo 1 - 1000-5000 öğelik liste
- Açılış: `RebuildList` ctor'da tutamaçsız (hızlı yol), tahmin 100-300 ms (5000); `SHGetFileInfo` uzantı başına bir kez ve önbellekli (iyi). `manager.Load()` `Program.cs:316-317` senkron (Core kapsamı dışı).
- Kategori/filtre değişimi: tam yeniden kurulum (O10), ~100-300 ms donma; `VirtualMode` önerilir.
- Sıralama: yerel `LVM_SORTITEMSEX` + yönetilen karşılaştırıcı; ~30-60 ms; kültür duyarlı karşılaştırma yerine `OrdinalIgnoreCase` yeterli olabilir. Sıralama ilerleme güncellemesinde korunmuyor.
- Owner-draw: satır başına 1 fırça + 9 `DrawText` + `Images[i]` Bitmap (O4); görünür ~30-40 satır, tam yeniden çizimde ~300-400 `TextRenderer.DrawText`, kabul edilebilir. Çift tamponlama (DoubleBuffered + `LVS_EX_DOUBLEBUFFER`) var.
- Ctrl+A / toplu silme: O1 (O(n^2)).
- Shell ikonu: `IndexFor` yalnızca ilk görülen uzantıda `SHGetFileInfo`; `FillRow` her çağrıda `Path.GetExtension` + sözlük araması (ucuz).

### Senaryo 2 - 20 aktif indirme
- Engine olayları `ConcurrentDictionary` + `HashSet` kilidi ile birleştiriliyor; UI thread'e olay başına `BeginInvoke` **yok** (iyi). Sel riski yok. 400 ms'de en çok 20 kirli satır: `Get` (O2), `FillRow` (yalnızca değişen hücre atanır), `ProgressDialog.ApplyProgress`. Tahmini UI yükü: ana liste <1-2 ms/tick; açık ilerleme pencereleri başına ~0.2-0.5 ms + boyama.
- İlerleme penceresi açıkken bağlantı listesi: `BeginUpdate/EndUpdate` ile her tick'te tüm liste geçersiz (küçük, <=32 satır) ve `SegmentBar` tüm çubuk (`ProgressDialog.cs:30, 198`). 20 pencere x 2.5 Hz = 50 yeniden boyama/sn; kabul edilebilir.
- `SegmentBar` hücreleri hiç sıfırlanmıyor (`ProgressDialog.cs:16-31`); yeniden bölme/yeniden başlatmada eski renkler kalır (işlevsel).

### Senaryo 3 - 30+ sekme, ağır video siteleri
- webRequest maliyeti (Y2), media trafiği (Y1), content.js (O5), button.js (O6).
- Sekme başına bellek: SW'de `groups` <=50 öğe x çerçeve; öğe başına çerez kopyası (Y1) -> grup başına yüzlerce KB'a kadar; `sentHeaders` <=400 (iyi). App'te `Seen` sınırsız (Y3).
- Native mesaj trafiği: grup başına en çok ~2.5 mesaj/sn (400 ms debounce, iyi); pratikte düşük, kötü durumda onlarca/sn x yüzlerce KB.
- Sayfa başına kaç video: rAF yalnızca fare altındaki tek hedef için; `setInterval` ilk 6 video; `mousemove` taraması sınırsız (O6).
- Pil/CPU: content.js 1.5 sn interval + observer, button.js 250 ms interval her görünür çerçevede; SW'nin sürekli uyanık kalması; rAF hover süresince. Arka plan sekmelerde `document.hidden` korumaları iyi.
- App tarafı `OnMedia` her mesaj için `BeginInvoke` (birleştirme yok, `VideoOverlayMediaSink.cs:67-74`); iş hafif, mesaj sayısı debounce ile sınırlı.

### Senaryo 4 - 50 eşzamanlı yakalama
K1 (kilitlenme + 5 sn + çift indirme), O7 (Host), O9 (50 pencere), D9 (odak çalma), D13 (pause/yoklama). Pipe tamponu: App okumayı bıraktığı için Host `SendAsync` birkaç mesaj sonra bloklanır; Host'un stdin döngüsü durur; Chrome tarafında kuyruk. Host yeniden başlatma: Host sürekli yaşar; App yokken 30 sn cooldown + her mesajda 300 ms (O7); App başlatıldıktan sonra pipe 10 sn içinde açılmazsa `RetryCooldown`. Pipe sunucusu çoklu eşzamanlı örnek kabul eder (iyi).

### Senaryo 5 - günlerce çalışma
- Sızıntı adayları: `_tabs`/`Seen` (Y3, sınırsız), `_progress` (O3, sınırsız), `host.log` (O7, Host ömrü boyunca), `_recentPrompts` (15 sn'de budanıyor ama yalnızca yeni çağrıda; önemsiz), `_info` (10 dk TTL, yalnızca erişimde budanıyor; önemsiz), `_selections` (<=20, iyi), `capture.js seen` (<=500), `sentHeaders` (<=400), `bypass` (nadir sızıntı), `pending` (zaman aşımıyla temizleniyor), `geometry.hidden/last/frames` (sekme kapanınca/yüklemede temizleniyor).
- Olay abonelikleri: MainForm/AppServices abonelikleri uygulama ömrü kadar tek sefer; `ProgressDialog` dışarıya abonelik bırakmıyor (`FormClosed` yerel). Sızıntı yok.
- Zamanlayıcılar: `MainForm._timer` (400 ms), `SchedulerService._timer` (15 sn, yalnızca izleme açıkken iş yapar), `VideoOverlayMediaSink._tick` (500 ms, `_raw` yokken boş), `OnCompleted` tek seferlik `Timer`'ı kendini dispose ediyor. İyi.
- GDI: sıcak yollarda `using` ile kalem/fırça; `DestroyIcon`, `ReleaseDC`, `DeleteObject` doğru; kalıcı GDI tutamaç sızıntısı bulunmadı (D6'daki finalizer'a kalanlar hariç).
- Service worker: açık `connectNative` portu SW'yi canlı tutar (Chrome 105+); SW yeniden başlarsa (eklenti yenileme/çökme) üst düzey `native.connect()` ve dinleyici kayıtları yeniden çalışır, `groups`/`sentHeaders`/`seen` sıfırlanır (App tarafında `Seen`/`Frames` kalır); bekleyen istekler `onDisconnect`'te `null` ile çözülüyor (iyi). SW yeniden başlarsa yeni Host süreci doğar ve App `ClientConnected` ile `settings` yeniden gönderir (D15 maliyeti).

---

## 5. İyi yapılmış olanlar

- Dirty-set + tek 400 ms zamanlayıcı ile ilerleme olayı birleştirme; olay başına UI'a `BeginInvoke` yok (`MainForm.cs:47, 102, 152, 438-464`).
- Hücre atamadan önce eşitlik denetimi; `Flush`'ta `BeginUpdate` kullanmama (titreme önleme); `RebuildList`'te `BeginUpdate/EndUpdate`; ListView çift tamponlama.
- `ShellIcons` uzantı başına tek `SHGetFileInfo` + önbellek + `DestroyIcon`; `Icons.Get` önbelleği; `AppIcon` paylaşımlı.
- Geometri için `LatestCoalescer` (anahtar başına son değer): UI thread'inde birikme yok; yerel düğme varsayılan kapalı ve `nativeButton=false` iken eklenti `videoGeometry` trafiği üretmiyor.
- IPC: yazma kilidi, bağlantı başına görev, `Qualities` işleyicisi `Task.Run` ile döngüyü bloklamıyor, `ClientConnected`'ta ayar gönderimi.
- Native messaging: boyut sınırı, aşan gövdenin okunup atılması (akış senkron kalır), ortak `JsonSerializerOptions` önbelleği.
- Eklenti: 400 ms debounce; `maxItems`/`maxHeaders`/`seen` üst sınırları; üstel yeniden bağlanma; `players.js` içinde sıkı sınırlar (40 kaynak, 30 script, 300 KB metin); `collectLinks` 500 sınırı; `document.hidden` korumaları; `WeakMap` ile izleyiciler; rAF yalnızca hedef varken; 400 ms kararlılık izleyicisi ile titreme önleme; `children` Map <=20.
- Tek örnek mutex, `MediaLog` boyut sınırlı döngü, `HostLog` açılışta döndürme, yinelenen istemleri eleme (`TryClaimPrompt`).

---

## 6. Emin olunamayan / doğrulanması gereken noktalar

1. `ImageList.Images[i]` her çağrıda yeni Bitmap mi (O4): WinForms iç davranışı, ölçülmeli.
2. `SelectedIndexChanged` toplu seçimde öğe başına mı yoksa birleştirilmiş mi tetikleniyor (O1; Ctrl+A kod döngüsünde öğe başına olduğu düşünülüyor).
3. `ListViewItem.Remove()` maliyeti ve 5000 satırda toplu silme süresi (O1).
4. webRequest olay hızı ve SW CPU yüzdesi (Y2): tamamen trafiğe bağlı; ölçüm gerekir. `connectNative`'in SW'yi tamamen canlı tuttuğu Chrome belgelerine dayanılarak varsayıldı.
5. `<img src=logo>`'nun bağlı olmayan shadow ağacında yükleme başlatıp başlatmadığı (O6/D12).
6. Çerez boyutlarının 1 MB sınırına gerçekten varıp varmadığı (Y1): site/profil bağımlı.
7. Named pipe varsayılan tampon boyutu ve Host'un kaç mesaj sonra bloklandığı (K1): Windows varsayılanlarına bağlı.
8. Engine `ProgressChanged` periyodu bu kapsamda incelenmedi (Core/Downloads, başka agent); UI tarafı olay hızından bağımsız birleştiriyor.
9. Core tarafındaki `downloads.json` kayıt maliyeti (5000 öğede `RequestSave` -> tüm dosya) bu kapsamda değerlendirilmedi.
