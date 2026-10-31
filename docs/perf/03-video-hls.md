# 03 – Video / HLS / yt-dlp: ağır yük performans analizi (teorik, yalnızca kod okuma)

Kapsam: `Core\Video\*`, `Core\Queue\ExternalRunner.cs` (yalnızca arayüz) + `DownloadManager.LaunchExternal`, `App\Services\VideoOverlayMediaSink.cs` (kalite çözme).
Hiçbir şey çalıştırılmadı; sayılar koddan çıkarılmış TAHMİNDİR. Etiketler: **[KESİN]** koddan doğrudan okunan, **[TAHMİN]** varsayıma dayanan.
Varsayımlar: 3 saatlik film ~6 GB (≈6 Mb/sn), ~3000 segment (≈2 MB/segment). `MaxConcurrentDownloads` varsayılanı 3 (`AppSettings.cs:10`); 5–10 senaryosu ayarın yükseltildiğini varsayar.

## Özet tablo

| # | Bulgu | Önem |
|---|---|---|
| 1 | Geçici disk tepe değeri ~3x (parçalar + merged.v + remux çıktısı), `+faststart` ikinci tam geçiş | Yüksek |
| 2 | Tek parça 5 sn'lik hatada tüm indirme düşüyor; VideoRunner bunu yt-dlp ile BAŞTAN indirmeye çeviriyor | Yüksek |
| 3 | Kalite çözmede `_info` sözlüğü iş parçacığı güvenli değil (UI + threadpool eşzamanlı) | Yüksek |
| 4 | Kalite isteklerinde eşzamanlılık sınırı ve iptal yok: yt-dlp -J süreçleri yetim kalabiliyor | Yüksek |
| 5 | İmzalı variant URL'si ExternalFormat içinde sabit; süresi dolunca devam imkânsız | Yüksek (tahmin) |
| 6 | Birleştirme + remux aşaması ilerleme bildirmiyor; her devamda baştan birleştirme | Orta |
| 7 | Parçalar tamamen bellekte (LOH), ~2-3 kopya/parça | Orta |
| 8 | Paylaşılan HttpClient 64 bağlantı sınırı + 60 sn zaman aşımı kuyruk bekleme süresini de kapsıyor | Orta |
| 9 | yt-dlp: PyInstaller `_MEI` geçici klasörleri öldürülünce sızar; Job Object yok (çökmede yetim süreç) | Orta (tahmin) |
| 10 | Ses izi hatası sessizce yutuluyor (sessiz video üretilebilir) | Orta |
| 11 | HLS ve yt-dlp global hız sınırlayıcıyı kullanmıyor | Orta |
| 12 | Hata durumunda `DwnlData\<id>\hls` ve yt-dlp `.part` dosyaları temizlenmiyor | Orta |
| 13+ | Düşük: Range yok sayılırsa tüm dosya, parça boyut sınırı yok, AES fazlalıkları, Kill senkron, stderr sınırsız | Düşük |

## Ayrıntılı bulgular

### 1. Disk tepe kullanımı ~3x ve fazla G/Ç — Yüksek [mekanizma KESİN, boyut TAHMİN]
- `HlsClient.cs:437` parçalar `NNNNN.seg` olarak yazılır (S). `:455-462` hepsi `merged.v` içine kopyalanır (+S). `:259-265` ffmpeg `-c copy ... +faststart` ile `outBase.mp4` üretir (+S). Temizlik yalnız `finally` içinde (`:279-287`): üçü aynı anda diskte.
- 6 GB film: DwnlData sürücüsünde (genelde C:) 12 GB, hedef klasörde 6 GB; aynı sürücüdeyse **~18 GB tepe**. Ayrı ses varsa +%5-10 (`a-*.seg`, `merged.a`).
- G/Ç: 6 GB yazma (parçalar) + 6 oku/6 yaz (birleştirme) + 6 oku/6 yaz (ffmpeg) + `+faststart` (`:264`) moov'u başa taşımak için ikinci tam geçiş (6 oku/6 yaz) ≈ 24 GB yazma, 18 GB okuma [TAHMİN]. HDD'de birleştirme ve remux ayrı ayrı dakikalar; SSD'de her biri ~1 dk. Defender 3000 `.seg` + `.tmp` dosyasını da tarar (`WriteAtomicAsync`: tmp yaz + Move, `HlsStateStore.cs:116-133`).
- Boş alan ön kontrolü yok. Disk dolarsa `IOException` -> `VideoRunner.cs:41` yakalar -> yt-dlp ile yeniden indirme dener (daha fazla disk ister).
- ffmpeg `-c copy` (yeniden kodlama yok) doğru; `-bsf:a aac_adtstoasc` yalnız TS/ayrı ses durumunda (`:262`).
- Öneri: (a) `merged.v` ara dosyasını atlayıp parçaları ffmpeg stdin'ine/concat listesine vermek (2x); (b) birleştirirken eklenen parçayı silmek; (c) `+faststart`'ı yavaş disklerde isteğe bağlı yapmak; (d) başlamadan boş alan kontrolü (≥2.2x tahmini boyut).

### 2. Tek parça hatası -> tüm indirme düşüyor + yt-dlp ile sıfırdan indirme — Yüksek [KESİN]
- `FetchBytesAsync` (`HlsClient.cs:472-502`): 401/403/404/410 anında `HlsException` (retry yok, `:489`); diğer hatalarda `Retries=4`, bekleme 0.5/1/1.5/2 sn = toplam ~5 sn (`:499`). Retry-After/429/503 özel işlenmiyor; jitter yok.
- Bir worker hata alırsa `linked.Cancel()` (`:445`) tüm 6 worker'ı durdurur ve indirme hata ile biter (`:451`). 3 saatlik indirmede 2500. parçada 5 sn'lik CDN kesintisi tüm işi düşürür (inen parçalar diskte kalır).
- `VideoRunner.cs:41-44`: `HlsException` (DRM hariç) -> yt-dlp varsa `:47-54` ile **aynı filmi sıfırdan** indirir; inmiş parçalar kullanılmaz. Sebep 403 (imza süresi) ise yt-dlp da büyük olasılıkla başarısız olur ya da 6 GB'ı tekrar çeker.
- Retry fırtınası: hata anında 6 worker × N indirme aynı gecikmeyle (senkron dalga) yeniden dener; 10 indirme = 60 eşzamanlı yeniden deneme [TAHMİN etki].
- Öneri: indirme düzeyinde toplam hata bütçesi + üstel geri çekilme (1-30 sn, jitter); 403/404'te bir kez playlist tazele; inmiş parça varsa otomatik yt-dlp tam yeniden indirmesi yerine "devam edilebilir" hata dön.

### 3. `_info` sözlüğü yarış durumu — Yüksek [KESİN]
- `VideoOverlayMediaSink.cs:43-44` `Dictionary` kilitsiz. `InfoFor` (`:262-283`) UI iş parçacığından (`ShowMenu`, `:345`) ve `GetQualitiesAsync` içinde `await tcs.Task.ConfigureAwait(false)` (`:462`) sonrası threadpool'dan (`:520`, `:537`); `Cached` (`:413-421`) de threadpool'dan (`:504`). Çok sekmede eşzamanlı tıklama = eşzamanlı Add/Remove/enumerate -> `InvalidOperationException`, bozuk iç durum, nadiren sonsuz döngü.
- Önbellek sınırı yok, yalnız 10 dk TTL; temizlik yalnız yeni bir istekte (`:267`, `:417`). Anahtar `"set|" + tüm URL'ler` (`:501`), imzalı URL'ler her tıklamada farklıysa isabet düşük, girişler (Task + VideoInfo, ≤25 seçenek) TTL boyunca bellekte: bellek riski düşük, **isabet oranı** sorunu orta. Hatalı Task'lar da TTL boyunca tutulur.
- Öneri: `ConcurrentDictionary` + `Lazy<Task>` ya da tek kilit; anahtarı imzasız normalize et; sayı üst sınırı (ör. 64).

### 4. Kalite isteklerinde eşzamanlılık ve iptal yok — Yüksek [KESİN]
- `IpcCoordinator.cs:46-55`: her `QualitiesMessage` için `Task.Run`, sınır/iptal yok. `GetQualitiesAsync`, `Cached`, `InfoFor` içine hiç `CancellationToken` geçilmiyor (`VideoOverlayMediaSink.cs:272`, `:279`: `default`).
- HLS yolu: `GetInfoSetAsync` en çok 10 aday paralel `GetTextAsync` (20 sn) + master probe 4 istek (6 sn) + üst varyant playlist (6 sn) = tıklama başına ≤ ~15 istek (`HlsQualityResolver.cs:26-27, 62, 147`). 5 sekmede art arda tıklama ~75 eşzamanlı istek.
- yt-dlp yolu: `YtDlpClient.GetInfoAsync` (`:89-112`) istek başına ayrı süreç, 90 sn zaman aşımı. Menü kapansa da süreç çalışır (`CloseMenu` yalnız `_lookupCts`'yi iptal eder, o da sadece bekleyen `WaitAsync` içindir, `:357-366`). Aynı `source` `_info` ile tekilleşir, farklı sekme/URL'lerde sınırsız paralel yt-dlp -J: her biri ~40-100 MB RSS ve 1-2 sn CPU [TAHMİN]; 10 sekme ≈ 0.5-1 GB ve CPU sıçraması, indirmelerle yarışır.
- `GetInfoAsync` içinde `ct` iptali -> `Kill(p)` (`:104`) doğru, ama çağıran hiç `ct` vermiyor.
- Öneri: kalite çözme için global `SemaphoreSlim(2)`, sekme başına "son istek kazanır" iptali (CTS zinciri `InfoFor/Cached/GetInfoSetAsync/GetInfoAsync`), IPC bağlantısı kopunca iptal.

### 5. İmzalı variant URL'si kalıcı formatta; devam yenileyemez — Yüksek [mekanizma KESİN, etki TAHMİN]
- `HlsClient.MakeFormat` (`:28`) variant/ses URL'sini `item.ExternalFormat` içine yazar; `VideoRunner.cs:21` devamda aynısını kullanır. Playlist her denemede yeniden çekilir (`HlsClient.cs:315`; iyi, eski segment URL'leri kullanılmaz), ama çekilen URL imzalı ve süresi dolmuşsa `GetTextAsync` 403 verir (`:54`) -> yt-dlp ile baştan indirme (bkz. 2). Filmi duraklatıp ertesi gün devam etmek token'lı CDN'lerde çalışmaz; durum dosyasındaki "URL değişmiş ama parçalar korunur" mantığı (`:349-350`) pratikte tetiklenemez, çünkü yeni URL'yi sağlayan bir yer yok.
- Devamda video için 1-2 + ses için 1-2 küçük GET (<1 sn); `Describe` 3000 URL için ~1 MB string + SHA256 (`HlsStateStore.cs:168-185`): ihmal edilebilir.
- Öneri: sayfa URL'si/referer saklanıyorsa 403'te kalite çözücüyü yeniden çalıştırıp aynı yükseklik için taze URL almak.

### 6. Birleştirme/remux ilerleme vermiyor, her devamda baştan — Orta [KESİN]
- Son parçadan sonra `merged.v` yazımı (`:455-462`) ve ffmpeg (`:293-309`; `-progress` yok) sırasında `report` çağrılmaz: arayüz %100'de, hız 0'da takılı görünür (6 GB için HDD'de dakikalar) [süre TAHMİN]. Kullanıcı duraklatırsa:
- ffmpeg iptal -> `Kill(true)` (`:304`), yarım `outBase.mp4` diskte kalır; devamda tüm parçalar yeniden birleştirilir (`FileMode.Create`, `:456`) ve ffmpeg baştan çalışır, playlist yeniden çekilir. Devam maliyeti: birleştirme + remux süresi (2-5 dk).
- Kopyalama arabelleği 64 KB, kaynak `useAsync:false` (`:468`): 6 GB / 64 KB ≈ 94 bin döngü, 3000 dosya açma; 1 MB arabellek küçük kazanç (Düşük).
- ffmpeg öncelik düşürülmemiş; eşzamanlı remuxlar diskte yarışır.
- Öneri: "birleştiriliyor %X" ilerlemesi (`-progress pipe:1` veya kopyalanan bayt); boyutu beklenenle eşleşen `merged.v` varsa yeniden üretmemek.

### 7. Bellek: parçalar tamamen bellekte — Orta [mekanizma KESİN, boyut TAHMİN]
- `FetchBytesAsync` `ReadAsByteArrayAsync` (`:492`) -> tüm parça `byte[]`; AES ise `Decrypt` yeni dizi (`:392`, `:511-517`), sahte başlıkta `d[i..]` bir kopya daha (`:529`). Parça ≥85 KB olduğundan **LOH**. 2 MB parça için ~2-3 kopya ≈ 4-6 MB/worker; 6 worker ≈ 25-40 MB/indirme; 10 indirme ≈ 250-400 MB LOH dalgalanması, gen2 GC baskısı, sızıntı yok. Parçalar anında diske yazılıp bırakıldığı için bütün film bellekte tutulmaz (iyi).
- 4K/yüksek bit hızı veya tek dosya byte-range akışlarında 10-30 MB parçalarla 5-10x.
- Öneri: akışlı yazma + `ArrayPool` / `CryptoStream` ile yerinde çözme.

### 8. Paylaşılan HttpClient ve zaman aşımı kapsamı — Orta [KESİN]
- `EngineHttp.Shared` (`EngineTypes.cs:68-86`): `MaxConnectionsPerServer=64`. 10 HLS × 6 = 60 aynı CDN host'una; normal motorun bağlantıları aynı istemciyi paylaşıyorsa havuzda bekleme başlar.
- `FetchBytesAsync` zaman aşımı (`:480-481`) `SendAsync`'ten önce başlar: havuz bekleme + başlık + gövde toplam 60 sn. Sınıra takılan veya yavaş CDN'de (2 MB / 60 sn < 35 KB/sn) istekler zaman aşımına düşer -> yeniden deneme -> yük artar.
- HTTP/1.1 (varsayılan): paralellik = bağlantı sayısı; HTTP/2 ile 6 akış tek bağlantıda verimli olurdu [TAHMİN]. `PooledConnectionLifetime` 5 dk — iyi.
- Task sayısı: indirme başına 6 `Task.Run` (`:448`); 10 indirme ≈ 70 görev, ihmal edilebilir. `Progress.Report` kilit içinde geri çağrı yapar (`:226`); UI yavaşlarsa 6 worker kilitte bekler (Düşük). İlerleme yalnız parça bitince bildirilir (`:439`): hız dalgalı.

### 9. yt-dlp süreç yönetimi — Orta [kodda KESİN, etki TAHMİN]
- Süreç başına: Python/PyInstaller açılışı ~1-3 sn (onefile `yt-dlp.exe` her açılışta `%TEMP%\_MEIxxxx`'e ~30-100 MB açar, Defender taraması), ~40-100 MB RSS. 10 süreç ≈ 0.5-1 GB RAM + açılışta CPU sıçraması. Her duraklat/devam = yeniden açılış + çıkarıcı (YouTube'da oynatıcı JS) ≈ 3-10 sn; ama taze imzalı URL aldığı için devam HLS istemcisinden *daha güvenilir*.
- Duraklat: `ct.Register(() => Kill(p))` (`YtDlpClient.cs:132`) `cts.Cancel()` çağıranın iş parçacığında SENKRON çalışır; `Kill(entireProcessTree:true)` süreç listesini tarar (~10-50 ms/süreç). `PauseAll` ile 10 süreç = 0.1-0.5 sn UI donması olabilir [TAHMİN].
- `Kill(true)` onefile önyükleyici + Python çocuğunu öldürür ama `_MEI` temizliği çalışmaz -> her öldürmede `%TEMP%` altında onlarca MB yetim klasör [TAHMİN, bilinen PyInstaller davranışı]; çok duraklat/devam GB'larca çöp.
- Zombi/yetim: Job Object kullanılmıyor (grep: yok). Uygulama çökerse/sonlandırılırsa yt-dlp ve ffmpeg çocukları çalışmaya devam eder; yeniden açılışta öğe Paused iken devam edilirse iki süreç aynı `.part` dosyasına yazabilir. Graceful kapanışta `ShutdownAsync` (`DownloadManager.cs:388-397`) doğru.
- Çocuk (ffmpeg) ağaç taramasından kaçarsa `ReadLineAsync` boru kapanana kadar bekler ve `WhenAll` (`:150`) takılır (düşük olasılık).
- `using var p` ve `using var reg` doğru sırada dispose — iyi. stderr `StringBuilder` sınırsız (`:147`), `--no-warnings` ile küçük (Düşük).
- stdout ayrıştırma: satır başına `Split('|')` + 4 `double.TryParse` (~1-2 µs). Satır sıklığı yt-dlp sürümüne bağlı (genelde ≤10/sn/süreç) -> 10 süreç ≤100 satır/sn, ihmal edilebilir CPU. Her satır `ProgressChanged` tetikler (`DownloadManager.cs:345-351`); MainForm `_progress` + `MarkDirty` ile birleştiriyor (`MainForm.cs:102`) — iyi.
- Hız sınırı yok; `--concurrent-fragments 4` × N süreç (`YtDlpClient.cs:66`).

### 10. Ses izi hatası sessizce yutuluyor — Orta [KESİN]
- `HlsClient.cs:251-252`: ayrı ses indirilemezse `HlsException` yakalanıp `audioRaw = null`; film sessiz mp4 olarak "başarıyla" biter. Kararsız CDN'de olasılık yüksek. FfmpegPath yoksa ses hiç alınmaz (`:249`) ve uyarı yok. Doğruluk/kullanıcı sürprizi riski.

### 11. Hız sınırlayıcı atlanıyor — Orta [KESİN]
- `DownloadManager.Launch` normal motora `Limiter` verir (`:309`); `LaunchExternal` yolu (`VideoRunner`/`HlsClient`/`YtDlpClient`) ona erişmez. Kullanıcı genel hız sınırı koyduysa video indirmeleri tam hızda çalışır, 10 HLS × 6 bağlantı bant genişliğini doldurur.

### 12. Temizlik/sızıntı denetimi — Orta/Düşük
- **Başarı**: `DwnlData\<id>\hls` `finally` ile silinir, `TryDeleteEmpty` üst klasörü siler — iyi (`HlsClient.cs:282-285`, `VideoRunner.cs:36`).
- **Hata** (HLS başarısız + yt-dlp yok/başarısız): kalıcı kipte klasör bilerek saklanır (`:282`); `VideoRunner.cs:47-60` yalnız yt-dlp başarısında siler. Başarısız öğe başına GB'larca parça, listeden kaldırılana kadar (`RemoveAsync`, `DownloadManager.cs:235` siler — iyi) diskte kalır; otomatik yaşlandırma yok.
- **yt-dlp**: çıktı doğrudan hedef klasöre (`.f137.mp4.part`, `.ytdl`) yazılır (`YtDlpClient.cs:123`). `RemoveAsync` yalnız `item.SavePath` ve `DwnlData\<id>`'yi siler; `.part`/`.ytdl`/video-ses ara dosyaları hedef klasörde kalır.
- **ffmpeg yarım çıktı**: iptalde `outBase.mp4` kalır (bkz. 6).
- CTS/olay: `LaunchExternal` `cts.Dispose()` yapıyor (`:377`) — iyi. `HlsClient` içinde `using var linked/cts` — iyi. `VideoOverlayMediaSink`: `_lookupCts` iptal edilip null'lanıyor, DISPOSE edilmiyor (`:227-228`, `:357`) — küçük (Düşük). `_selections` 20 ile sınırlı (`:591`) — iyi (`Keys.First()` "en eski" garantisi vermez).

### 13. Düşük önemli
- `HlsClient.cs:485-492`: `Range` gönderilmiş ama sunucu 200 + tüm dosyayı dönerse (Range yok sayılırsa) her byte-range parçası için TÜM dosya belleğe okunur ve parça diye yazılır (bozulma + N× trafik). 206 doğrulaması yok [nadir].
- `:492`: parça boyutu üst sınırı yok. `:55-56`: playlist 8 MB sınırı gövde okunduktan sonra denetlenir.
- `:511-517` `Decrypt`: parça başına `Aes.Create()` + gereksiz `aes.Key/IV`; PKCS7 hatalıysa `CryptographicException` + ikinci çözme. AES-128-CBC AES-NI ile ~1-3 GB/sn/çekirdek: 6 GB ≈ 2-6 sn CPU, ihmal edilebilir. Anahtar önbelleği tek kilit/tek istek — iyi (`:363-383`).
- `StripFakeHeader` normal parçalarda ilk bayt kontrolüyle döner (ucuz).
- `HlsStateStore.SegmentDone` (`:81-91`): eşik kontrolü ile `Save` arasında kilit yok, birkaç worker art arda yazabilir (en kötü 6 yazma/2 sn). Dosya yalnız meta (~1 KB, parça listesi YOK): yazma ≤0.5/sn/indirme. **İyi.**
- Kayıt ayrıntısı: devamda `_state.Tracks` boş başlar; video `Save()` ses izinin eski durumunu dosyadan siler (`HlsStateStore.cs:70-73`, `HlsClient.cs:357`) -> ses indirilirken çökme olursa ses parçaları "durum yok" diye silinip yeniden iner (küçük).
- Sync-over-async bulunmadı (`.Result/.Wait` yok); `VideoOverlayMediaSink.cs:348` `infoTask.Result` yalnız `IsCompletedSuccessfully` ise (güvenli). Normal akışta istisna maliyeti yok; hata dallarında `HttpRequestException` (`:490`) ~20-50 µs, ihmal edilebilir.
- `HlsQualityResolver`: aday 10, probe 4, kısa zaman aşımı — iyi; ama sınırlar tıklama başına, global değil (bkz. 4).

## İyi yapılmış kısımlar
- Sabit sayıda worker + `Interlocked` imleç, `linked` CTS ile ilk hatada hızlı durdurma (`HlsClient.cs:419-452`).
- Parçalar atomik (`.tmp` + `Move`), devam kaynak gerçeği = dosya varlığı + boyut>0; yarım parça sayılmaz; state yalnız meta.
- Her denemede playlist yeniden çekilip URL/süre özetiyle karşılaştırma; uyuşmazlıkta güvenli silme (`:337-357`).
- `-c copy` remux, ffmpeg stdin kapatılıyor, iptalde süreç ağacı öldürülüyor.
- Birleştirme akışla (`CopyToAsync`), tüm film belleğe alınmıyor.
- Process/CTS/HttpRequestMessage `using` ile; tek ortak `HttpClient`.
- HLS kalite menüsü yt-dlp'siz hızlı yol; sınırlı aday/probe ve kısa zaman aşımları.
- `MaxConcurrentDownloads` hem yt-dlp hem HLS aktif sayısını sınırlıyor (varsayılan 3).

## Emin olunamayan noktalar
- yt-dlp ilerleme satırı sıklığı, onefile dağıtımı ve `_MEI` sızıntısının gerçek boyutu sürüm/kuruluma bağlı (kod yalnız `yt-dlp.exe` arıyor, `ToolLocator.cs`).
- `Process.Kill(true)` süresi ve `Pause`'un hangi iş parçacığından çağrıldığı (UI kapsamı başka agentta); callback'in çağıran iş parçacığında senkron çalıştığı kesin.
- ffmpeg `+faststart` ikinci geçişinin tam maliyeti ve MPEG-TS girişte ek bayrak ihtiyacı çalıştırmadan doğrulanamaz.
- `DownloadManager._http` ile `EngineHttp.Shared`'ın aynı örnek olup olmadığı doğrulanmadı (64 bağlantı paylaşımı buna bağlı).
- Tüm sayılar (GB, MB, sn) varsayımsal film boyutundan türetildi; ölçüm yapılmadı.
