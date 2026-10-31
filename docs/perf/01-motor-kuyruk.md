# 01 - Motor ve Kuyruk: Ağır Yük Performans Analizi (teorik, yalnızca kod okuma)

Kapsam: `src/TDownloader.Core/Downloads/*`, `Queue/DownloadManager.cs`, `Settings/*`, `Util/*`, `JsonDefaults.cs`. Hiçbir şey çalıştırılmadı; kod değiştirilmedi.
Etiketler: **[KESİN]** = koddan doğrudan görülüyor; **[TAHMİN]** = .NET/Windows/NTFS davranışına dayalı çıkarım, ölçülmedi.
Kısaltmalar: DE = `Downloads/DownloadEngine.cs`, DM = `Queue/DownloadManager.cs`, ET = `Downloads/EngineTypes.cs`, RL = `Downloads/RateLimiter.cs`, JD = `JsonDefaults.cs`.

## Özet tablo

| # | Bulgu | Önem | Tür |
|---|-------|------|-----|
| 1 | Tek bağlantının 5 art arda hatası TÜM indirmeyi Failed yapar (DE:284-289, 401) | Yüksek | KESİN |
| 2 | MaxConnectionsPerServer=64 + bağlantı bekleme süresi 30 sn ReadTimeout'a sayılır (ET:117, DE:349-351) | Yüksek | KESİN (mekanizma), TAHMİN (tetiklenme) |
| 3 | Hız sınırlayıcı beklemesi ReadTimeout zamanlayıcısını durdurmaz; düşük limit + çok bağlantı = her parçada yeniden bağlanma (DE:364-386, RL:34) | Yüksek | KESİN |
| 4 | Ön tahsis `SetLength` ile; NTFS geçerli-veri-uzunluğu (VDL) yüzünden uzak ofsete ilk yazma arayı sıfırlar (DE:199-203) | Yüksek | TAHMİN (NTFS davranışı) |
| 5 | `RandomAccess.Write` senkron, pool thread'inde bloklar; HDD/yavaş diskte thread-pool açlığı (DE:386) | Yüksek | KESİN (senkron), TAHMİN (şiddet) |
| 6 | Parça dosyası `%APPDATA%\...\DwnlData`'da; hedef başka sürücüyse bitişte kopyala+sil, ilerleme yok, çift disk alanı (DE:541, AppPaths) | Orta-Yüksek | KESİN |
| 7 | 429/503: Retry-After yok, jitter yok, bağlantı azaltma yok; 403 anında kalıcı hata (DE:414-430) | Orta-Yüksek | KESİN |
| 8 | Duraklat yarışı: `_cts` RunAsync başlamadan Pause çağrılırsa komut kaybolur (DE:79-82, 89; DM:312, 324) | Orta | KESİN |
| 9 | Olay işleyicisi istisna fırlatırsa `ProcessQueue()` çağrılmaz, kuyruk durur (DM:317-322) | Orta | KESİN |
| 10 | downloads.json: her kayıtta tüm liste, girintili, Cookie dahil; 0,5 sn debounce (DM:109-131) | Orta | KESİN |
| 11 | state.json: saniyede 1, aktif indirme başına tmp+move, veri flush'ı yok (DE:459-463, JD:299-306) | Orta-Düşük | KESİN |
| 12 | HTTPS'te okumalar ~16 KB parçalı olabilir: tur başına 3 kilit + CancelAfter + küçük pwrite (DE:361-388) | Orta-Düşük | TAHMİN |
| 13 | Range yanıtı doğrulanmıyor (Content-Range başlangıcı, If-Range/ETag) (DE:352-356) | Orta (güvenilirlik) | KESİN |
| 14 | `Add` içinde kilit altında `File.Exists` + `_items.Any`, O(n) (DM:149-153, 206) | Düşük | KESİN |
| 15 | Manager'da O(n) taramalar (`Get`, `ProcessQueue`, `Items` kopyası) | Düşük | KESİN |
| 16 | RateLimiter: ayar değişince uyuyan bekleyenler uyanmaz; Task.Delay ~15 ms çözünürlük | Düşük | KESİN/TAHMİN |
| 17 | İstisna maliyeti; Cookie başlığının yönlendirmede başka host'a gitmesi | Düşük | KESİN |

---

## Bulgular (ayrıntı)

### 1. Tek bağlantı hatası tüm indirmeyi öldürür - Yüksek [KESİN]
- DE:284-289 `WorkerAsync` catch: herhangi bir işçinin yeniden denemesi tükenince `_fatal` set edilip `_cts.Cancel()` çağrılır; DE:119 `Fail(_fatal)`.
- DE:401 `if (++attempt > MaxRetries) throw;` MaxRetries=5; `attempt` yalnızca ilerleme olunca sıfırlanır (DE:399). Toplam bekleme ~1+2+4+8+16 = 31 sn.
- Etki (Senaryo 4): 8 bağlantıdan biri art arda 5 kez 503/429/zaman aşımı alırsa, diğer 7'si sağlıklı ilerlerken tüm indirme Failed olur. 50 GB'lık dosyada kullanıcı "ilerleme var ama başarısız" görür. Kalıcı 4xx (403 vb., DE:420) ilk karşılaşmada tüm indirmeyi öldürür; sunucuların IP başına bağlantı sınırı genelde 403/429/503 verir.
- Düzeltme: işçi başarısızlığında yalnızca o işçiyi emekli et (segmenti havuza geri bırak), kalanlarla devam et; hiç işçi ilerleyemiyorsa başarısız say. Hata alınca bağlantı sayısını otomatik düşür.

### 2. MaxConnectionsPerServer=64 ve bağlantı kuyruğu zaman aşımı - Yüksek [mekanizma KESİN, tetiklenme TAHMİN]
- ET:117 `MaxConnectionsPerServer = 64`; tek paylaşılan HttpClient (ET:104-108), havuz host:port başına.
- DE:349-351: `cts.CancelAfter(ReadTimeout=30 sn)` SendAsync'ten ÖNCE kuruluyor; SendAsync'in havuzda boş bağlantı beklemesi de bu 30 sn'ye dahil. Uzun ömürlü büyük indirmeler soketleri bırakmadığı için bekleyen işçi 30 sn sonra `OperationCanceledException` alır; `IsRetryable` bunu yeniden denenebilir sayar (DE:422) → 5 deneme (~31 sn bekleme + 6x30 sn) ≈ 3,5 dk sonra Bulgu 1 uyarınca tüm indirme Failed.
- Senaryo 2: aynı host/CDN'den 10-20 indirme x 8 = 80-160 bağlantı > 64. Fazlası aç kalır; indirmeler "Downloading" görünüp ilerlemez veya hata verir. Farklı host'lar için sorun yok (sınır host başına).
- Düzeltme: sınırı yükselt/kaldır ya da host başına semafor ile toplam bağlantıyı planla; zaman aşımı yalnızca başlık/okuma için olsun, havuz beklemesini kapsamasın.

### 3. Hız sınırlayıcı beklemesi ReadTimeout'u tüketir - Yüksek [KESİN]
- DE:364 `cts.CancelAfter(30 sn)` → DE:366 okuma → DE:377 `_limiter.AcquireAsync(n, ct)` (cts DEĞİL ct ile bekler) → yazma → döngü başı. Zamanlayıcı bekleme boyunca işler; süre dolarsa sonraki `ReadAsync(cts.Token)` hemen iptal olur, "zaman aşımı" sayılır ve yeniden bağlanılır (yanıt kapanır, soket atılır, yeni TLS el sıkışması).
- Bekleme süresi ≈ (eşzamanlı bağlantı sayısı x 64 KB) / limit. Örnek: 80 bağlantı, 100 KB/s → ~51 sn > 30 sn; 160 bağlantı, 1 MB/s → ~10 sn (sorun yok). Eşik: bağlantı x 64 KB / limit > 30 sn.
- Her tur ilerleme sağladığı için `attempt` sıfırlanır (DE:399) → hata vermeyen sonsuz yeniden bağlanma döngüsü: her ~64 KB için yeni bağlantı; bant genişliği, CPU ve sunucuda bağlantı fırtınası.
- Düzeltme: zaman aşımı yalnızca ağ okuma/başlık beklemesini kapsasın (limiter beklemesinden sonra yeniden kur); düşük limitte okuma boyutunu küçült. Beklerken sunucu boşta zaman aşımı riski de var (TAHMİN).

### 4. Ön tahsis ve NTFS sıfırlama - Yüksek [TAHMİN, NTFS davranışı]
- DE:199-203: `FileStream(Create)` + `SetLength(total)`. EOF uzar ve kümeler ayrılır (disk doluysa hemen IOException: iyi), ancak geçerli-veri-uzunluğu (VDL) 0 kalır. NTFS, VDL ötesine ilk yazmada aradaki aralığı sıfırla doldurur.
- Senaryo 1: 50 GB x 32 bağlantı; parçalar ~1,5 GB aralıklı başlar (DE:186-194). En yüksek ofsete (~48 GB) gelen ilk yazma 0..48 GB aralığını sıfırlamaya çalışır: HDD'de dakikalar, NVMe'de onlarca sn. Yazma çağrısı bloklu; diğer işçilerin yazmaları da etkilenebilir; soketler okunmaz, sunucu boşta zaman aşımı yapabilir (Bulgu 1/3 zincirine girer). Toplam yazma hacmi ~2x.
- Düzeltme: gerçek davranışı ölç; seçenekler: seyrek (sparse) dosya, `SetFileValidData` (ayrıcalık ve güvenlik uyarısı gerekir) veya yazmaları sıralı ilerleyecek biçimde düzenlemek.
- FAT32 hedefte 4 GB üstü SetLength hatası Fail verir (mesaj ham IO mesajı).

### 5. Senkron disk yazımı, thread-pool açlığı - Yüksek [senkron KESİN, şiddet TAHMİN]
- DE:386 `RandomAccess.Write(_handle!, ...)` async döngü içinde senkron; handle `FileOptions.Asynchronous` olmadan açılmış (DE:108). Her yazma sırasında bir pool thread'i bloklanır.
- Senaryo 2 + 5: ağ (1 Gbps) disk (HDD ~100 MB/s) hızını aşınca Windows önbellek yazma kısıtlaması yazıcıları bloklar; 80-160 işçi aynı anda bloklu thread demektir. Pool min thread = çekirdek sayısı, sonrası ~1-2 thread/sn: ProgressLoop (PeriodicTimer), Task.Delay (backoff/limiter), CancelAfter geri çağrıları, IPC ve UI'nin pool işleri gecikir → ilerleme donar, yanlış zaman aşımları, yeniden bağlanmalar.
- DE:571 SaveState, DE:541 File.Move, DM:115 Save de senkron I/O; ağır yükte pool'u meşgul eder.
- Düzeltme: indirme başına sınırlı yazma kanalı (Channel) ve az sayıda ayrılmış yazıcı thread'i; ya da asenkron handle + `RandomAccess.WriteAsync`; birikim tamponu (bkz. 12).

### 6. Parça dizini ve hedef farklı sürücüde - Orta-Yüksek [KESİN]
- AppPaths.Default: `%APPDATA%\T-Downloader\DwnlData` (genelde C:). DE:541 `File.Move(partPath, SavePath)`: farklı birimde kopyala+sil olur; 50 GB için HDD'de ~6-8 dk. Bu sürede durum "Downloading" %100, hız 0, iptal fark edilmez (token kullanılmıyor), olay yok, pool thread'i bloklu.
- C:'de indirme boyutu kadar boş alan gerekir (SetLength ile ayrılır): D:'ye 50 GB indiren kullanıcı C:'de de 50 GB bulmalı. Disk dolu ise PrepareAsync IOException'ı DE:138 catch'ine düşer → Failed.
- Düzeltme: parça dosyasını hedef klasörde (`.tdpart`) tut; veya birim farklıysa ilerleme bildiren kopya.

### 7. 429/503 yeniden deneme davranışı - Orta-Yüksek [KESİN]
- DE:414-430: `Retry-After` okunmuyor; üstel gecikme jitter'sız (1,2,4,8,16 sn; sınır 30). 20 indirme x 8 işçi aynı anda 503 alırsa senkron dalgalar halinde 160 istek sunucuya gider; 429'da sunucu cezayı uzatır.
- Hata sonrası bağlantı sayısı azaltılmıyor. Sunucu IP başına 4-6 bağlantıya izin veriyorsa 8-32 bağlantı sürekli 429/403 üretir.
- ProbeAsync (DE:227-263) her başlat/devam'da ek bir GET (Range 0-0) atar; 160 indirme aynı anda başlarken 160 ek istek.
- Düzeltme: Retry-After'a uy, jitter ekle, host başına ortak "soğuma", 429/503'te Connections'ı yarıya indir.

### 8. Pause yarışı - Orta [KESİN]
- DM:324 `Stop = engine.Pause`; DM:312 `Task.Run(...RunAsync...)`. `Pause()` (DE:79-82) `_cts?.Cancel()` yapar; `_cts` RunAsync içinde (DE:89) oluşturulur. Task.Run henüz çalışmadan (havuz doluyken, Bulgu 5 ile gecikir) gelen Pause sessizce kaybolur; indirme kullanıcı durdurduğu halde devam eder. ShutdownAsync (DM:392) de etkilenebilir.
- Düzeltme: motorda `_pauseRequested` bayrağı; RunAsync başında kontrol et.

### 9. Olay istisnası kuyruğu durdurur - Orta [KESİN]
- DM:317-322: `_active.Remove`, `RequestSave`, `DownloadCompleted?.Invoke`, ardından `ProcessQueue()`. Bir abone istisna fırlatırsa `ProcessQueue` çalışmaz ve görev istisnası gözlenmez → kuyruktaki dosyalar yeni bir olay gelene kadar bekler. Benzer şekilde DE:552 `SetStatus(Completed)` içinde fırlayan abone `Complete()`'i catch'e (DE:138) taşır: dosya taşınmış ve ara dizin silinmişken durum Failed olur.
- Düzeltme: olayları try/catch ile sar; `ProcessQueue()` finally'de çağrılsın.

### 10. downloads.json - Orta [KESİN]
- DM:109-119: her kayıt tüm listeyi `Indented` serileştirip WriteAllText + Move yapar. 1000 öğe x (URL + Cookie 1-4 KB + alanlar) ≈ 0,5-4 MB; girinti boyutu şişirir. Cookie'ler düz metin ve her yazımda tekrar yazılır.
- Sıklık: RequestSave (DM:121-131) yalnızca ekleme/silme/duraklatma/tamamlama/hata olaylarında çağrılır, ilerlemede değil (iyi). 500 ms debounce: saniyede en fazla ~2 yazma. 1000 öğeyi yapıştırmak tek yazma; ama 200 küçük dosya art arda bitince ~2 yazma/sn x MB boyutu HDD ve antivirüs taramasına ek baskı yapar (TAHMİN). Pool thread'i bloklar.
- Dayanıklılık: fsync yok; elektrik kesintisinde sıfır uzunluklu dosya olabilir; `Load` (DM:97) bozuk dosyayı sessizce yutar → liste boş görünür, sonraki Save üzerine yazar; yedek yok.
- Düzeltme: girintisiz yaz; Cookie'yi ayrı sakla/şifrele; önceki sürümü `.bak` tut; sıklığı 2-5 sn'ye çek.

### 11. state.json yazımı - Orta-Düşük [KESİN]
- DE:459-463: saniyede bir, indirme başına. 20 aktif indirme = 20 tmp oluştur+yaz+yeniden adlandır / sn (JD:299-306); her biri antivirüs/indeksleyici tarafından taranabilir. Boyut küçük (32 parça ≈ 2 KB). Biten segmentler hiç silinmiyor (DE:317, 475) ama pratikte yüzlerle sınırlı.
- Tutarlılık: veri `FlushToDisk` edilmeden state "yazıldı" der. Süreç ölümünde sorun yok; ani güç kesintisinde dosyada sıfır boşluk kalıp state "var" diyebilir → bozuk dosya sessizce tamamlanabilir (doğrulama yok). TAHMİN (olasılık düşük, etki sessiz bozulma).
- Düzeltme: state yazımını 2-5 sn'ye çıkar; kayıttan önce veriyi flush et veya Position'ı güvenli geriye çek.

### 12. Okuma/yazma parçalama ve kilit sayısı - Orta-Düşük [TAHMİN]
- DE:361-388: tur başına 3 `lock(_gate)`, 1 limiter kilidi (rate=0 olsa bile kilide girer: RL:24-26), 1 `CancelAfter` ve 1 pwrite. Senaryo 1'de 1 Gbps ≈ 125 MB/s: 64 KB tamponla ~1900 tur/sn; HTTPS'te SslStream tek TLS kaydı (≤16 KB) döndürdüğü için ~7600 tur/sn olabilir. Bu düşük (kilit çekişmesi küçük); darboğaz TLS şifre çözme ve disk.
- Asıl etki: 16 KB'lık yazma HDD'de rastgele erişimi artırır (32 akış x farklı ofset). İşçi başına 1-4 MB birikim tamponu daha sıralı yazma sağlar.
- Kilit içinde I/O yok (iyi). `SumDownloaded`/`PublishProgress` her 250 ms'de tüm segmentleri kilit altında gezer, küçük.
- Düzeltme: tamponu biriktirip büyük yazma; `CancelAfter`'ı her turda değil aralıklı kur; limiter'de rate=0 hızlı yolunu kilitsiz yap.

### 13. Range doğrulaması yok - Orta (güvenilirlik) [KESİN]
- DE:352-356 yalnızca 206 kontrol eder; Content-Range başlangıcı ve toplam doğrulanmaz, segment isteklerinde If-Range/ETag yok. Dosya sunucuda değişirse veya yönlendirme sonrası farklı CDN düğümü farklı sürüm verirse sessiz bozulma olur (ETag yalnızca probe'ta state ile karşılaştırılır, DE:167).
- Düzeltme: her yanıtta Content-Range'i doğrula; `If-Range: etag` ekle.

### 14-15. Manager taramaları - Düşük [KESİN]
- DM:149-153, 206-207: `Add` içinde `_gate` altında `File.Exists` (disk/ağ sürücüsünde yavaş olabilir, tüm manager çağrılarını bloklar) ve `_items.Any` (O(n)); 1000 öğe toplu ekleme O(n²) ≈ 10^6 karşılaştırma, ihmal edilebilir; asıl risk kilit altında dosya sistemi çağrısı.
- `Items` (DM:63-66) her çağrıda listeyi kopyalar; App MainForm.cs:380, 468 çağırıyor (sıklık UI ajanının kapsamı). `ProcessQueue` (DM:287-301) her tamamlanmada tüm listeyi gezer (1000 öğe için mikro-saniye). `StartAll` (DM:262-265) O(n²) ve n adet `ItemChanged` olayı üretir (UI `_dirty` kümesiyle birleştiriyor).

### 16. RateLimiter - Düşük
- Doğruluk iyi: borçlanmalı belirteç kovası, kilit kısa, bekleyenler adil. 250 ms patlama sınırı makul. Task.Delay Windows'ta ~15,6 ms çözünürlüğe yuvarlanır; 1 ms eşiği (RL:34) küçük beklemeleri atlayıp borcu biriktirir → ortalama doğru, kısa vadede dalgalı.
- Zayıf nokta: `BytesPerSecond` değişince (RL:18) mevcut bekleyenler eski hızla hesaplanmış sürelerini bitirir (limiti kaldırmak anında etkili olmaz; bkz. 3'teki uzun beklemeler).

### 17. İstisna maliyeti ve diğer - Düşük
- Duraklat/iptal: 160 işçi x OCE (işçi başına birkaç yüz µs); sorun değil. Normal çalışmada yeniden deneme nadir; Bulgu 3 döngüsünde saniyede onlarca istisna.
- DE:223: Cookie başlığı manuel eklendiği için `AllowAutoRedirect` ile başka host'a yönlendirmede de gider (gizlilik).
- HttpClient: `Timeout=Infinite` + işlem başına CTS doğru. HTTP/1.1 olduğundan işçi başına ayrı soket (h2 çoğullama yok: iyi). `PooledConnectionLifetime` 5 dk aktif bağlantıyı kesmez. `ResponseDrainTimeout`/`MaxResponseDrainSize` varsayılan (1 MB): dinamik bölmede eski işçinin yanıtı fazla veriyle biter, bağlantı atılır; yeni parça yeni TLS bağlantısı ister (bölme başına ~1 RTT, ihmal edilebilir).

---

## Senaryo bazında değerlendirme

**1. 50 GB x 32 bağlantı, 1 Gbps.** CPU/kilit/bellek sorun değil (32 x 64 KB = 2 MB havuz tamponu, ArrayPool doğru). Sorunlar: (a) SetLength sonrası VDL sıfırlama (4) başlangıçta büyük duraklama; (b) disk < 125 MB/s ise bloklu yazıcılar (5); (c) 32 bağlantıda sunucunun 403/429'u (1, 7); (d) hedef başka sürücüyse bitişte uzun kopyalama (6). Segment tabanlı devam (state.json 1 sn) ve atomik JSON iyi.

**2. 200 dosya, 10-20 aktif x 8.** Aynı host'ta 64 soket tavanı + 30 sn zaman aşımı (2): ilerlemeyen veya Failed indirmeler. Farklı host'larda sağlıklı. 160 işçi async olduğundan thread gerekmez; yazma bloklaması (5) olmazsa sorun yok. 20 x 4 Hz = 80 olay/sn, UI tarafında birleştiriliyor. Kuyruk mantığı (ProcessQueue/Launch) kilit altında ve `_active` yarışsız.

**3. 1000+ öğe.** downloads.json 0,5-4 MB, en çok ~2 yazma/sn (10). Bellek ihmal edilebilir. Yükleme tek seferlik. Asıl risk bozulma/sessiz sıfırlanma.

**4. Kararsız sunucu.** Bulgu 1 + 7: tek işçi 5 hata → tüm indirme Failed; jitter ve Retry-After yok; manager düzeyinde otomatik yeniden deneme yok (Failed kalır, elle başlatılır). İlerleme olduğu sürece sonsuz yeniden bağlanma.

**5. HDD / disk dolu.** Prealloc: disk doluluğu başlangıçta yakalanır (iyi); ancak NTFS sıfırlama (4), bloklu yazıcılar (5), küçük rastgele yazmalar (12), C:'de çift alan (6). Disk ortada dolarsa `IOException` yeniden denenebilir sayılır (DE:422) → 5 kez yeniden bağlanıp aynı veriyi indirir, sonra Failed; "disk dolu" ayırt edilmiyor.

**6. Uzun süre çalışma.** Sızıntı bulgusu zayıf: CTS'ler `using`/finally ile dispose ediliyor (DE:91, 149-150, 236, 349; DM:330/377), PeriodicTimer `using`, motor olayları motor çöpe gidince serbest, `_active` kaydı kaldırılıyor. Büyüyen koleksiyon: `_items` tamamlanan/hatalı öğeleri kullanıcı silene dek tutar (1000+ için kabul edilebilir); `MainForm._progress` kayıtları siliniyor (MainForm.cs:399). Kalanlar: `RequestSave` ateşle-unut Task'ı IO dışı istisnada gözlenmez (zararsız); Bulgu 9'daki durma riski; `DownloadItem` alanlarına kilitsiz çok thread'li erişim (JSON serileştirme sırasında yarış, zararsız).

## İyi yapılmış kısımlar
- ArrayPool ile işçi başına tek 64 KB tampon; okuma başına yeni byte[] yok.
- `RandomAccess.Write` + ortak handle, ofsete doğrudan yazma: birleştirme ve Seek kilidi yok.
- Kilit altında yalnızca bellek işlemi; olaylar kilit dışında tetiklenir.
- İlerleme olayı 4 Hz, tek PeriodicTimer; UI tarafında birleştirme (dirty kümesi, `LatestCoalescer`).
- Paylaşılan tek HttpClient, `Timeout=Infinite`, işlem başına CTS; çerez/otomatik açma kapalı; `Accept-Encoding: identity` (Range ile uyumlu).
- Atomik JSON yazma (tmp+move); devam doğrulaması (URL, boyut, ETag, dosya uzunluğu).
- Dinamik bölme Reserved/Position ayrımıyla veri yarışsız; bölme sonrası eski işçi daralan sınırda durur.
- Borçlanmalı küresel hız sınırlayıcı adil; manager `_active` ve ProcessQueue yarışsız.

## Emin olunamayan noktalar
- NTFS VDL sıfırlamasının gerçek süresi ve diğer işçileri ne kadar bloklayacağı (4): ölçülmeli.
- SslStream'in gerçek okuma boyutu (16 KB varsayımı) ve HDD'de yazma birleştirme etkisi (12, 5).
- Hedef sunucuların IP başına bağlantı sınırı ve 429/403 davranışı (1, 7).
- UI tarafındaki `Items` çağrı sıklığı ve olay işleyicilerinin istisna güvenliği (9, 15) yalnızca satır düzeyinde incelendi.
