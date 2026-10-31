# 01 — Core katmanı hata avı (kod incelemesi)

Kapsam: `src\TDownloader.Core\` (Downloads, Queue, Settings, Util, Video, Ipc) ve `tests\TDownloader.Core.Tests`.
Yöntem: yalnızca kod okuma + `dotnet test` (1 kez). Kod değiştirilmedi, uygulama çalıştırılmadı.
"Kesin" = kod yolu satır satır izlendi, davranış koddan çıkıyor. "Tahmin" = tetikleyici/ortam (ör. .NET veya yt-dlp davranışı, kullanıcı etkileşimi) doğrulanmadı.

## Test sonucu
`dotnet test tests/TDownloader.Core.Tests` -> **152 başarılı, 0 başarısız, 0 atlanan, 9 sn** (tek çalıştırma; flaky görülmedi).
Kararsızlık riski taşıyan yerler (tek çalıştırmada tetiklenmedi): bkz. C24.

## Özet tablo

| Önem | Adet | Kimlikler |
|---|---|---|
| Kritik | 0 | - |
| Yüksek | 2 | C1, C2 |
| Orta | 12 | C3-C14 |
| Düşük | 14 | C15-C28 |
| **Toplam** | **28** | |

---

## YÜKSEK

### C1 — HLS: "iptal edilmeyen" OperationCanceledException hata sayılmıyor, eksik parçalarla birleştirmeye geçiliyor
- **Durum:** Düzeltildi — 4dbd2b8 (iç iptal/zaman aşımı hata sayılır; kaçan iptalde birleştirmeye geçilmez). Test: HlsCoreFixTests.C1_*
- **Yer:** `Video/HlsClient.cs:626-664` (Worker + sonrası), `:519-538` (RefreshAsync), `:672-682` (birleştirme)
- **Sorun:** Worker `catch (Exception ex)` bloğu `OperationCanceledException`'ı `firstError`'a yazmıyor, yalnızca `linked.Cancel()` yapıyor (`:657-658`). `WhenAll`'dan sonra `ct.ThrowIfCancellationRequested()` yalnızca DIŞ token'ı denetliyor (`:663`); `linked` iptal edilmiş ama `firstError == null` ise hiçbir şey fırlatılmıyor ve kod birleştirmeye düşüyor. Dış token iptal edilmeden OCE üretebilen yol: `RefreshAsync` -> `LoadMediaAsync` -> `GetTextAsync` içindeki 20 sn zaman aşımı `TaskCanceledException` (`:59,62`); `RefreshAsync` yalnızca `HlsException` ve `HttpRequestException` yakalıyor (`:534-535`).
- **Tetikleme:** İmzalı parça adresi süresi dolar (403) -> playlist yenilenirken playlist sunucusu 20 sn'den yavaş yanıt verir. Tüm işçiler durur, hata kaydedilmez, kod birleştirmeye girer; ilk eksik parçada `CopyFileAsync` `FileNotFoundException` fırlatır (`:698`). O ana kadar kopyalanan parçalar `TryDelete(sp)` ile ZATEN silinmiştir (`:679`) -> 1500 parçalık videoda 1400 parça tekrar indirilir. Kullanıcıya gelen mesaj da yanıltıcı ("dosya bulunamadı").
- **Önem:** Yüksek
- **Düzeltme:** `WhenAll` sonrası `if (linked.IsCancellationRequested && firstError == null) throw new HlsException(...)` (veya dış-olmayan OCE'yi hata say); `RefreshAsync`'te OCE'yi de yakala.
- **Kesin** (kod yolu); tetikleyici koşulu (yavaş playlist) gerçekçi ama dar.

### C2 — HLS: birleştirme sırasında duraklat/hata = daha önce birleştirilmiş parçaların kaybı
- **Durum:** Düzeltildi — 714b4e3 (+5f6ab98, 4aa35a1). Birleştirme işaretçisi (`merged.v.merge`: son parça sayısı + uzunluk, 8 parçada bir diske zorlanıp atomik yazılır); parçalar işaretçiden sonra silinir; devamda birleşik dosya kırpılıp kalan parçalarla sürer. Test: C2_*
- **Yer:** `Video/HlsClient.cs:672-686`
- **Sorun:** Parçalar kopyalanır kopyalanmaz silinir (`:678-679`), ama "birleştirme tamam" işaretçisi (`.ok`) ancak döngü sonunda yazılır (`:686`). Döngü ortasında iptal/IOException olursa `merged.v` yarım kalır (sonraki çalışmada `FileMode.Create` ile silinir), kopyalanıp silinen parçalar diskte yoktur.
- **Tetikleme:** Kullanıcı "Birleştiriliyor %60" aşamasında Duraklat'a basar (veya uygulama çöker / disk dolar). Devam edince `missing` listesi = silinmiş %60'lık parçalar; hepsi yeniden indirilir. `:685` yorumu yalnızca ffmpeg aşamasını koruyor, birleştirme aşamasını değil.
- **Önem:** Yüksek (devam/duraklat senaryosunda veri/bant genişliği kaybı; imzalı adres süresi dolmuşsa indirilemeyebilir)
- **Düzeltme:** Birleştirme sırasında silmeyi ertele (birleştirme bitince sil) veya ilerleme ofsetini kalıcı yazıp `merged.v`'ye eklemeye devam et; en azından iptalde silme yapma.
- **Kesin**

---

## ORTA

### C3 — ffmpeg iptal edilince yarım `outBase.mp4` hedef klasörde kalıyor
- **Durum:** Düzeltildi — a891b50 (ffmpeg `.part.mp4` yazar, başarıda taşınır, iptalde silinir). Test: C3_*
- **Yer:** `Video/HlsClient.cs:313-326, 367-399`
- **Sorun:** ffmpeg doğrudan nihai yola (`outBase + ".mp4"`) yazar. İptal (Duraklat/Sil) `WaitForExitAsync(ct)` ile OCE fırlatır (`:394`); `TryDelete(final)` yalnızca ffmpeg başarısızlığında çalışır (`:325`), iptal yolunda atlanır.
- **Tetikleme:** "Dönüştürülüyor %40" iken Sil veya uygulamayı kapat. Hedef klasörde tamamlanmış gibi görünen bozuk `.mp4` kalır. `RemoveAsync` yalnızca `.part/.ytdl` temizler (`ExternalCleanup.cs:9`), `deleteFile=false` ise dosyaya dokunmaz.
- **Önem:** Orta
- **Düzeltme:** ffmpeg çıktısını geçici adrese (`final + ".tmp.mp4"`) yazıp başarıda taşı; iptalde sil.
- **Kesin**

### C4 — HLS/yt-dlp çıktı adı, `UniquePath`'in baktığı yoldan farklı -> başka dosyanın üzerine yazma
- **Durum:** Düzeltildi — a891b50 (HLS: nihai ad ffmpeg/ham taşımadan ÖNCE UniqueName ile seçilir, mevcut dosya silinmez) + 6f2a1d5 (yt-dlp: çıktı kökü, tamamlanmış herhangi bir `ad.*` varsa `ad (1)`; .part/.ytdl/fN artıkları çakışma sayılmaz). yt-dlp "zaten indirilmiş" davranışı doğrulanmadı, savunma amaçlı. Test: C3C4_*, C4_*
- **Yer:** `Video/HlsClient.cs:304 (ffmpeg -y), 330-332 (File.Delete(raw)); Video/VideoRunner.cs:436; Video/YtDlpClient.cs:350; Queue/DownloadManager.cs:220-234`
- **Sorun:** `UniquePath` yalnızca `ad.<SavePath uzantısı>` serbest mi diye bakar. HLS çıktısı `stem + ".mp4"` / `stem + ".ts"`, yt-dlp `stem.%(ext)s` olur; uzantı farklıysa (ör. SavePath `x.m4a`, `x.webm`) başka bir `x.mp4`/`x.ts` varsa ffmpeg `-y` ile ezer, ham yolda `File.Delete(raw)` siler (`:331`), yt-dlp ise var olan dosyayı "zaten inmiş" sayıp o yolu `FinalPath` diye döndürebilir (Completed + yanlış dosya).
- **Tetikleme:** Klasörde kullanıcının `Film.mp4` dosyası var; aynı adla ses (m4a) veya `.ts` çıktılı HLS indirilir.
- **Önem:** Orta (kullanıcı dosyası kaybı)
- **Düzeltme:** Çıktı yolunu indirme başlamadan UniquePath ile ÇIKTI uzantısı için de çöz; `File.Delete(raw)` yerine benzersiz ad.
- **Kesin** (ffmpeg/File.Delete kısmı); yt-dlp "zaten indirilmiş" davranışı **tahmin**

### C5 — yt-dlp: `report` geri çağrısı istisna atarsa stdout okunmaz -> süreç pipe dolunca kilitlenir
- **Durum:** Düzeltildi — 6f2a1d5 (report try/catch; stdout okuma sürer). Test: YtDlpRunTests.C5 (düzeltmesiz sürümde 60 sn zaman aşımına düştüğü görüldü)
- **Yer:** `Video/YtDlpClient.cs:363-372, 378-379`; `Queue/DownloadManager.cs:392-404` (ProgressChanged doğrudan çağrılıyor, `Raise` değil)
- **Sorun:** `outTask` döngüsü `report(agg.Add(pr))` içinde fırlatılan istisnayla ölür, stdout artık tüketilmez; yt-dlp 64 KB pipe dolunca yazamayıp bloke olur, `p.WaitForExitAsync()` (`:378`, ct'siz) hiç bitmez. İndirme "İndiriliyor" kalır, kuyruk yuvası dolu.
- **Tetikleme:** `ProgressChanged` abonesi (UI) bir kez istisna atar (ör. kapanmış form).
- **Önem:** Orta
- **Düzeltme:** `report` çağrısını try/catch ile sar; stdout okuma döngüsünü her koşulda sürdür.
- **Tahmin** (abone istisnası ortama bağlı; kod yolu kesin)

### C6 — HTTP motoru: olay aboneleri istisna atarsa indirme Failed oluyor / ilerleme döngüsü ölüyor
- **Durum:** Düzeltildi — a817562 (motor olayları abone başına korumalı: Notify; Manager köprüsü Raise; ProgressLoop tur başına catch). Test: EngineFixTests.C6
- **Yer:** `Downloads/DownloadEngine.cs:105, 142-144, 500-535, 553-575, 577-581`; `Queue/DownloadManager.cs:351-352`
- **Sorun:** `SetStatus`/`PublishProgress` olayları doğrudan çağrılıyor. (a) `SetStatus(Downloading)` try içinde (`:105`) -> abone istisnası `Fail`'e götürür; `SetStatus(Paused)` catch içinde (`:143`) -> istisna RunAsync'ten kaçar. (b) `ProgressLoopAsync` yalnızca OCE yakalar (`:534`); abone istisnası döngüyü öldürür, hata ancak `await progressTask` (`:125`) ile indirme BİTTİKTEN sonra yüzeye çıkar ve tamamlanmış indirme Failed olur. `Manager.Raise` bu korumayı yalnızca tamamlanma olaylarında sağlıyor (`:334-342`), motor olayları köprüsünde yok.
- **Tetikleme:** UI'nın `ItemChanged`/`ProgressChanged` aboneliği bir kez fırlatır.
- **Önem:** Orta
- **Düzeltme:** Motor olaylarını abone başına try/catch ile çağır (Manager'da `Raise` kullan); ProgressLoop'ta genel catch + log.
- **Tahmin** (abone davranışı); kod yolu kesin

### C7 — Ağ zaman aşımı "Duraklatıldı" olarak görünüyor (hata mesajı yok)
- **Durum:** Düzeltildi — 4dbd2b8 (GetTextAsync iç zaman aşımı -> HlsException "zaman aşımı"; VideoRunner yalnızca dış iptali yeniden fırlatır). Test: C7_*
- **Yer:** `Video/HlsClient.cs:58-62, 463`; `Video/VideoRunner.cs:448`; `Queue/DownloadManager.cs:406, 410`
- **Sorun:** `GetTextAsync` kendi 20 sn zaman aşımında `TaskCanceledException` (OCE alt sınıfı) atar; ilk `LoadMediaAsync(ct)` çağrısı (`:463`) bunu sarmalamaz. `VideoRunner` `catch (OperationCanceledException) { throw; }` ile yeniden fırlatır, Manager bunu `canceled = true` sayıp `Paused` yapar.
- **Tetikleme:** Playlist sunucusu 20 sn yanıt vermez. Kullanıcı hata görmez, öğe "Duraklatıldı" olur; Başlat'a basınca tekrar eder.
- **Önem:** Orta
- **Düzeltme:** `GetTextAsync`'te `when (!ct.IsCancellationRequested)` ile OCE'yi `HlsException("zaman aşımı")`'na çevir.
- **Kesin**

### C8 — Çerez yt-dlp komut satırında (süreç listesinde görünür)
- **Durum:** Düzeltildi — 48a66e2 (çerez %TEMP% altında kullanıcıya özel geçici Netscape dosyası, `--cookies`; iş bitince silinir; CookieHost yoksa hiç verilmez) + 6f2a1d5 (test). Test: YtDlpRunTests.C8_*, CookieScopeTests
- **Yer:** `Video/YtDlpClient.cs:270` (`--add-header Cookie:...`), `:267-271`
- **Sorun:** Oturum çerezleri argv'de düz metin; aynı kullanıcıdaki her süreç (Görev Yöneticisi > Komut satırı, WMI) okuyabilir. Ayrıca `--add-header` yt-dlp'nin isteklerine (yönlendirilen başka host'lar dahil) gidebilir; HLS yolundaki "çerez yalnızca aynı host" koruması (`MediaContext.Apply`, `MediaContext.cs:222`) yt-dlp yolunda yok.
- **Önem:** Orta
- **Düzeltme:** Çerezi geçici bir Netscape cookie dosyasına (kullanıcıya özel, bitince silinen) yazıp `--cookies` ile ver.
- **Kesin** (argv'de görünürlük); yt-dlp'nin başka host'a iletmesi sürüme bağlı **tahmin**

### C9 — `Cookie` başlığı HTTP yönlendirmesinde başka host'a taşınabilir
- **Durum:** Düzeltildi (savunma) — a817562 (AllowAutoRedirect=false + elle izleme; çerez yalnızca özgün host'a, HLS'te CookieHost kuralı). .NET'in Cookie başlığını taşıdığı ayrıca deneyle doğrulanmadı. Test: EngineFixTests.C9
- **Yer:** `Downloads/DownloadEngine.cs:238-239`, `Downloads/EngineTypes.cs:108-109` (`AllowAutoRedirect = true`, `UseCookies = false`); `Video/HlsClient.cs:714, 716`
- **Sorun:** Çerez elle `Cookie` başlığına konuyor; .NET yönlendirmede `Authorization`'ı host değişince siler ama özel `Cookie` başlığını silmez. İndirme URL'si başka domain'e (CDN/üçüncü taraf) 302 verirse oturum çerezi o host'a da gider. Motor ayrıca bağlantı tavanını yönlendirme hedefine değil özgün URL'nin host'una uyguluyor (`:169, 224`).
- **Önem:** Orta
- **Düzeltme:** `AllowAutoRedirect=false` + elle izleme (host değişirse `Cookie`/`Referer`'ı at).
- **Tahmin** (.NET yönlendirme başlık davranışı doğrulanmadı)

### C10 — Çerezsiz kalıcılık: yeniden başlatma sonrası kimlik doğrulamalı indirme devam edemez
- **Durum:** Düzeltildi — a817562 (çerezsiz 401/403 hata mesajına "oturum çerezi bulunmuyor olabilir" ipucu). Test: C10
- **Yer:** `Downloads/DownloadItem.cs:26-27`; `Queue/DownloadManager.cs:106-133` (Load); `Downloads/DownloadEngine.cs:254, 270`
- **Sorun:** `Cookie` `[JsonIgnore]`; uygulama yeniden açılınca öğe `Paused` yüklenir, Başlat -> çerezsiz probe 401/403 -> kalıcı hata (`IsRetryable` false) -> Failed. `.part` verisi duruyor ama kullanıcıya çerez gerektiği söylenmiyor; HLS/yt-dlp için de aynı (`VideoRunner.ContextOf`).
- **Önem:** Orta (güvenlik kazanımının bedeli; kullanıcıya görünmüyor)
- **Düzeltme:** Çerezsiz kalıp 401/403 alınca anlaşılır mesaj ("oturum çerezi yok; tarayıcıdan yeniden ekleyin").
- **Kesin** (davranış); tasarım kararı

### C11 — ASCII dışı başlık değeri (çerez/User-Agent) tüm indirmeyi 5 deneme sonra başarısız yapıyor
- **Durum:** Düzeltildi — a817562 (başlıklar UTF-8 kodlanır; CR/LF/denetim karakteri -> yeniden denemesiz açık hata). Test: C11_*
- **Yer:** `Downloads/DownloadEngine.cs:229-241, 273-278`
- **Sorun:** `TryAddWithoutValidation("Cookie"/"User-Agent", ...)` ile eklenen değer ASCII dışı karakter içeriyorsa SocketsHttpHandler gönderimde `HttpRequestException` atar; bu `StatusCode`'suz olduğundan `IsRetryable` true -> boşuna ~30 sn üstel bekleme, sonra Failed.
- **Tetikleme:** Çerez değerinde Türkçe/UTF-8 karakter içeren siteler.
- **Önem:** Orta
- **Düzeltme:** Gönderimden önce ASCII/CRLF doğrula; geçersizse açık hata ver veya başlığı at.
- **Tahmin** (.NET istisna türü/davranışı doğrulanmadı)

### C12 — Durum dosyası diskteki veriden ileri olabilir (FlushToDisk yok) -> sessiz bozuk dosya
- **Durum:** Düzeltildi (savunma) — a817562 (state.json yazımından önce RandomAccess.FlushToDisk). Güç kaybı simüle edilemediği için doğrudan test yok; yol her indirmede çalışır.
- **Yer:** `Downloads/DownloadEngine.cs:116, 450-451, 526-530, 625-642`
- **Sorun:** `RandomAccess.WriteAsync` sonrası veri OS önbelleğinde; `Position` ilerler ve `state.json` 1 sn'de bir yazılır. İşletim sistemi çökmesi/elektrik kesintisinde state "X bayt indi" der, dosyada ise ön-tahsisli sıfırlar kalır; devam edilince o aralık yeniden indirilmez, tamamlanan dosya bozuk çıkar (hash doğrulaması yok).
- **Önem:** Orta
- **Düzeltme:** `SaveState` öncesi `RandomAccess.FlushToDisk(_handle)`; en azından duraklat/çıkışta.
- **Tahmin** (yalnızca OS çökmesi/güç kaybında)

### C13 — Segment isteklerinde If-Range ve Content-Range doğrulaması yok
- **Durum:** Düzeltildi — a817562 (strong ETag ile If-Range; 200 dönerse "uzak dosya değişti"; Content-Range başlangıcı uyuşmazsa hata). Test: C13_*
- **Yer:** `Downloads/DownloadEngine.cs:409-417, 420-428`
- **Sorun:** 206 yanıtının `Content-Range` başlangıcı istenenle karşılaştırılmıyor; parça istekleri `If-Range`/ETag taşımıyor. İndirme sırasında uzak dosya değişirse (veya araya giren proxy farklı aralık dönerse) parçalar farklı sürümlerden karışır ve dosya bozulur; ETag yalnızca devam kararında kullanılıyor (`:177`).
- **Önem:** Orta
- **Düzeltme:** 206'da `ContentRange.From == startPos` doğrula; istekte `If-Range: <etag>` gönder, 200 gelirse hata.
- **Tahmin** (tetikleyici sunucu davranışı)

### C14 — HLS parça belleğe sınırsız okunuyor; Content-Length ile dev ayırma
- **Durum:** Düzeltildi — a817562 (parça/anahtar/init başına 256 MB sınırı; Content-Length ve akış). Test: C14
- **Yer:** `Video/HlsClient.cs:724, 730-731`
- **Sorun:** `new MemoryStream((int)cl)` — `cl` sunucudan; `int.MaxValue`'a kadar tek seferde ayrılır. 6 işçi x 2 GB. Parça/anahtar/init için üst sınır yok (`ReadAsByteArrayAsync` `:724`).
- **Tetikleme:** Kötü niyetli/bozuk playlist bir parçayı çok büyük dosyaya yönlendirir -> OutOfMemory (yakalanmaz, süreç çökebilir).
- **Önem:** Orta
- **Düzeltme:** Parça başına üst sınır (ör. 256 MB) ve `cl` için üst sınır; aşarsa HlsException.
- **Kesin** (kod); tetikleyici **tahmin**

---

## DÜŞÜK

### C15 — Kalite önbellek anahtarı genel parametreleri atıyor (`e`, `s`, `t`, `h`, `r`, `st`) -> yanlış video listesi
- **Durum:** Atlandı — Ipc kapsam dışı (App/Ipc ajanı).
- **Yer:** `Ipc/AppSupport.cs:12-14, 22-27`
- **Sorun:** `?e=5` (bölüm), `?s=2` (sezon), `?t=...` gibi içerik ayırıcı parametreler "imza" sayılıp atılıyor; farklı bölümlerin m3u8 adresleri aynı anahtara düşebilir ve önbellekten yanlış bölümün kalite/format (varyant URL) listesi dönebilir -> yanlış video indirilir.
- **Önem:** Düşük-Orta (dar)
- **Düzeltme:** Yalnızca tanınmış imza adlarını (X-Amz-*, X-Goog-*, token, sig, expires...) at; tek harfli adları çıkar.
- **Kesin** (kod); tetikleyici **tahmin**

### C16 — Yakalama: `accepted:true` sonrası onay penceresi hatası/uygulama kapanması indirmeyi sessizce kaybettirir
- **Durum:** Atlandı — Ipc kapsam dışı; tasarım ödünleşimi.
- **Yer:** `Ipc/DownloadCaptureGate.cs:263-269`
- **Sorun:** Tarayıcı kendi indirmesini bıraktığı için, `_showPrompt` istisna atarsa (yalnızca log, `:266`) veya App bekleyen en çok 500 istekle kapanırsa indirme hiçbir yerde başlamaz.
- **Önem:** Düşük-Orta (tasarım ödünleşimi)
- **Düzeltme:** İstisnada isteği doğrudan `DownloadManager.Add`'e ver veya kullanıcıya bildir; kapanışta bekleyenleri kuyruğa yaz.
- **Tahmin**

### C17 — Pause→Start tıklaması kaybolur; RemoveAsync × Start yarışı
- **Durum:** Atlandı — risksiz 1-2 satır değil (Start/Remove yeniden tasarımı gerekir).
- **Yer:** `Queue/DownloadManager.cs:278-290 (Start), 251-273 (RemoveAsync), 360`
- **Sorun:** Motor `Paused` durumuna geçtikten sonra `_active` temizlenmeden önceki pencerede `Start`, `_active.ContainsKey` yüzünden sessizce döner (kullanıcı ikinci kez tıklamalı). `RemoveAsync`'te `act.Task` bittikten sonra `_items.Remove`'dan önce yapılan `Start` yeni bir motor başlatır; ardından klasör silinir/öğe kalkar ama motor sürebilir ve `SavePath`'e dosya bırakabilir.
- **Önem:** Düşük
- **Düzeltme:** `Start`'ı `_active` temizlenene kadar "yeniden başlat" bayrağıyla kuyruğa al; Remove'da öğeyi önce `_items`'tan çıkar.
- **Kesin** (Start kaybı); Remove yarışı **tahmin**

### C18 — Sessiz hata yutma: `downloads.json` yazılamazsa uyarı yok; `settings.json` geçici okunamazsa varsayılanlar sonra dosyayı ezer
- **Durum:** Kısmen düzeltildi — 8999384 (Load null öğeleri filtreler). Save hatası bildirimi ve settings.json ezme: atlandı (kapsamlı).
- **Yer:** `Queue/DownloadManager.cs:142-144, 127-129`; `Settings/AppSettings.cs:319-320`; `Downloads/DownloadEngine.cs:640-641`
- **Sorun:** `Save()` IO/yetki hatalarını yutar (disk dolu -> liste kalıcılığı sessizce durur). `AppSettings.Load` HER istisnada (kilitli dosya dahil) varsayılan döner; sonraki Save gerçek ayarları (kategori klasörleri, düğme ofsetleri) ezer, settings için `.bak` yok. `Load()` içinde JSON `[null]` elemanı `i.Status`'ta NRE verir.
- **Önem:** Düşük
- **Düzeltme:** Save hatasını olay/LoadWarning ile bildir; settings okuma IO hatasında yeniden dene ve ezme; null elemanı filtrele.
- **Kesin**

### C19 — Windows ad kısıtları: ayrılmış aygıt adları ve uzun yollar
- **Durum:** Atlandı — Tahmin; doğrulanmadı, 1-2 satır değil.
- **Yer:** `Queue/DownloadManager.cs:243-248`; `Util/ContentDispositionParser.cs:35-37`
- **Sorun:** `SanitizeFileName` geçersiz karakterleri ve sondaki noktayı temizler, ama `CON/NUL/AUX/COM1/LPT1` (uzantılı da), sondaki boşluk ve uzunluk sınırı yok. Ayrılmış adlı dosya aygıta yazılır/başarısız olur; çok uzun yollarda ffmpeg/yt-dlp hata verebilir.
- **Önem:** Düşük
- **Düzeltme:** Ayrılmış adların başına `_`, ad uzunluğunu ~150 ile sınırla.
- **Tahmin** (Windows aygıt adı çözümü doğrulanmadı)

### C20 — `File.Move(overwrite:true)` ile Ekleme ile Tamamlama arasında oluşan dosya ezilir
- **Durum:** Atlandı — Tahmin; tamamlama akışı değişikliği gerekir (HLS tarafı C4 ile kapandı).
- **Yer:** `Downloads/DownloadEngine.cs:608`, `Queue/DownloadManager.cs:178, 233`
- **Sorun:** `UniquePath` yalnızca Add anında bakar; kuyrukta saatlerce bekleyen indirme tamamlanırken aynı adlı kullanıcı dosyası (sonradan oluşmuş) sessizce ezilir.
- **Önem:** Düşük
- **Düzeltme:** Tamamlamada hedef varsa yeni benzersiz ad seç (`overwrite:false` + yeniden dene).
- **Kesin** (kod); tetikleyici **tahmin**

### C21 — `ExternalCleanup` aynı kökenli başka indirmenin `.part` dosyasını silebilir
- **Durum:** Atlandı — Tahmin; eşleşme daraltması test gerektirir.
- **Yer:** `Util/ExternalCleanup.cs:21-27`
- **Sorun:** Anahtar yalnızca `stem + "."`. `video.mp4` silinirken, aynı klasörde `video.mkv` indirmesinin `video.mkv.part` dosyası da eşleşir ve silinir.
- **Önem:** Düşük
- **Düzeltme:** Eşleşmeyi tam `SavePath + ".part"` ve yt-dlp parça kalıplarına daralt.
- **Tahmin**

### C22 — `DownloadItem.Id` doğrulanmadan yol bileşeni
- **Durum:** Düzeltildi — 8999384 (Load yalnızca harf/rakam/-/_ kimlikleri kabul eder). Test: PersistenceTests.Load_SkipsNullEntries_*
- **Yer:** `Downloads/DownloadEngine.cs:74-76`, `Queue/DownloadManager.cs:262`, `Video/VideoRunner.cs:439`
- **Sorun:** `Id` `downloads.json`'dan geldiği gibi `Path.Combine(DwnlData, id)`; `..\..` içeren bir `Id` ile `RemoveAsync` `Directory.Delete(d, true)` keyfi klasörü siler. Dosyayı değiştirebilen biri zaten yerel yetkiliyse kazanç sınırlı.
- **Önem:** Düşük
- **Düzeltme:** `Id`'yi `^[0-9a-f]{32}$` ile doğrula (Load'da).
- **Kesin** (kod); istismar **tahmin**

### C23 — IPC: sunucu kaynak kısıtları ve dayanıklılık
- **Durum:** Atlandı — Ipc kapsam dışı (Ipc ajanı e26ecf6 ile kısmen ele almış).
- **Yer:** `Ipc/IpcPipe.cs:42, 93-104`
- **Sorun:** (a) `ReadLineAsync` satır uzunluğu sınırsız; yerel bir istemci yeni satır göndermeden bellek şişirebilir. (b) `AcceptLoopAsync` yalnızca `IOException` yakalıyor; `NamedPipeServerStream` `UnauthorizedAccessException` (aynı adlı pipe başka kullanıcı/yükseltilmiş süreçte) atarsa döngü sessizce ölür, tarayıcı eklentisi bir daha bağlanamaz. (c) `PipeSecurity` ve `FirstPipeInstance` yok: aynı kullanıcıdaki başka bir süreç pipe adını önce açıp eklentinin gönderdiği çerezleri alabilir.
- **Önem:** Düşük
- **Düzeltme:** Satır uzunluğu sınırı; accept döngüsünde genel catch + gecikmeli yeniden deneme; `FirstPipeInstance` ve kullanıcıya özel ACL.
- **Tahmin**

### C24 — Test kararsızlığı riski: statik `HostGate.RestoreAfter` paylaşımı
- **Durum:** Düzeltildi — 5237308 (HostGate geri yükleme süresi örnek bazlı; statik RestoreAfter testlerde değiştirilmiyor). Süre tabanlı diğer testler dokunulmadı; 3 koşuda kararlı.
- **Yer:** `tests/.../EngineResilienceTests.cs:177` (statik değeri 200 ms yapıyor), `:169-170` (429 testi `gate.Limit == 4` bekliyor)
- **Sorun:** xUnit sınıfları paralel koşar; `HostGate_QueuesFifo...` testi çalışırken `RestoreAfter=200ms` iken 429 testinin gate'i erkenden geri yüklenir -> `Limit` 4 yerine 8 olabilir. Ayrıca süre tabanlı doğrulamalar var: `EngineResilienceTests.cs:81 (<4 sn), 103 (>=900 ms)`, `EngineTests.cs:130 (2-12 sn)`, `HlsTests.cs:205 (<3 sn)`.
- **Önem:** Düşük
- **Düzeltme:** HostGate testlerini `[Collection]` ile seri yap veya `RestoreAfter`'ı örnek özelliğine çevir.
- **Tahmin** (tek çalıştırmada görülmedi)

### C25 — Yanlış/eski kullanıcı mesajları ve yanlış sınıflandırma
- **Durum:** Düzeltildi — 8999384 (DRM tam kelime eşleşmesi; yt-dlp oturum mesajı güncellendi). Test: MapError_DrmMatchesWholeWordOnly
- **Yer:** `Video/YtDlpParsing.cs:117, 121-122`
- **Sorun:** `lower.Contains("drm")` herhangi bir alt dizgeye ("...drm...") "DRM korumalı" der; "oturum açmak gerekiyor (çerezler bu sürümde yt-dlp'ye aktarılmıyor)" mesajı artık yanlış (çerezler `--add-header` ile aktarılıyor).
- **Önem:** Düşük
- **Düzeltme:** Kelime sınırlı eşleşme; mesajı güncelle.
- **Kesin**

### C26 — HLS ilerleme: ses aşamasında güncelleme yok, tahmini boyut erken %100 gösterebilir
- **Durum:** Atlandı — 1-2 satır değil (ilerleme modeli değişikliği).
- **Yer:** `Video/HlsClient.cs:233-241, 253-260`
- **Sorun:** `countTotals:false` (ses izi) için `AddLive` `Emit` çağırmaz; video bitince ses indirilirken çubuk/hız donar. `est < _shown` ise `est = _shown` yapıldığından (`:258`), VBR içerikte erken parçalar küçükse yüzde parçalar bitmeden %100'e dayanır. `Emit` kilit altında `_report` (UI) çağırıyor (`:260`), yavaş abone işçileri bloklar.
- **Önem:** Düşük
- **Düzeltme:** Ses için de periyodik Emit; yüzdeyi parça sayısından türet; geri çağrıyı kilit dışında yap.
- **Kesin**

### C27 — Ham `Decrypt` yedeği ve yakalanmayan istisna türleri
- **Durum:** Kısmen düzeltildi — 4dbd2b8 (VideoRunner yakalama listesine CryptographicException/ArgumentException/OperationCanceledException(iç) eklendi). PaddingMode.None yedeği değişmedi.
- **Yer:** `Video/HlsClient.cs:764-770`; `Video/VideoRunner.cs:450`
- **Sorun:** PKCS7 hatasında `PaddingMode.None` ile sessizce çöp döndürülür; uzunluk 16'nın katı değilse `ArgumentException` (yakalanmaz) -> `VideoRunner` `catch` listesinde yok (`HlsException/HttpRequestException/IOException/UriFormatException`), yt-dlp yedeğine düşmeden doğrudan Failed.
- **Önem:** Düşük
- **Düzeltme:** Hatalı dolgu = HlsException; `VideoRunner`'da `CryptographicException/ArgumentException/NotSupportedException` ekle.
- **Kesin**

### C28 — Gizlilik: tanı günlüğü ve çalışma klasörü
- **Durum:** Atlandı — 1-2 satır değil (günlük/çalışma klasörü politikası).
- **Yer:** `Util/MediaLog.cs:362, 376`; `Video/HlsStateStore.cs:29-30, 183`; `Video/HlsClient.cs:546-551`
- **Sorun:** `MediaLog.Sanitize` yalnızca 32+ karakterlik yol bölümlerini maskeler; kısa jetonlar/oturum kimlikleri loga girer (sorgu ve çerez yazılmıyor, doğrulandı). `hls-state.json` imzalı playlist/anahtar URL'lerini, `key-*.key` AES anahtarını düz dosya olarak `DwnlData\<id>\hls` altında tutar (başarıda/silmede temizlenir; çökme sonrası kalır). `downloads.json` `Url/ExternalFormat/Referrer` içindeki imzalı adresleri kalıcı yazar (çerez yazılmıyor, doğrulandı: `DownloadItem.cs:27`).
- **Önem:** Düşük
- **Düzeltme:** Günlükte yol bölümlerini kısaltma eşiğini düşür; çalışma klasörü için başlangıçta yetim temizliği.
- **Kesin** (kod); etki düşük

---

## Doğrulanıp sorun bulunmayanlar (kapsam kanıtı)
- Yol geçişi: `SanitizeFileName` (`DownloadManager.cs:243`) `\ / :` karakterlerini `_` yapar, `..` -> `indirme`; `ContentDispositionParser` yüzde-çözümlemeden SONRA `GetFileName` uygular (`:35`). Geçiş bulunmadı.
- yt-dlp/ffmpeg argüman enjeksiyonu: tüm argümanlar `ArgumentList` ile ayrı belirteç; URL `--` sonrası (`YtDlpClient.cs:278, 295`); ffmpeg girdi yolları `DwnlData` altında mutlak. Enjeksiyon bulunmadı (değerin `-` ile başlaması yalnızca hataya yol açar).
- `HostGate` iptal/pump yarışları, `RateLimiter`, `SlidingSpeed`, `LatestCoalescer`, `ProcessJob` yapı düzeni, `Pause` Dekker kapısı (`DownloadEngine.cs:82-98`) incelendi; hata bulunmadı.
- `_retiredWorkers`: Manager her Launch'ta yeni motor kurduğu için sayaç sıfırlama sorunu yok.
- Çerez diske yazımı: `DownloadItem.Cookie` `[JsonIgnore]`; Core'da çerez/token loglayan satır bulunmadı.

## Dosya dağılımı (özet)
HlsClient: C1, C2, C3, C4, C7, C14, C26, C27 · DownloadEngine: C6, C9, C11, C12, C13 · DownloadManager: C17, C18, C19, C20, C22 · YtDlpClient/Parsing: C5, C8, C25 · Ipc: C15, C16, C23 · Util/Diğer: C10, C21, C24, C28
