# Düzeltme Planı (performans/dayanıklılık)

Kaynak: `docs/perf/karsilastirma.md` (48 bulgu, kodda doğrulanmış) ve `01/02/03-*.md`.
İki agent paralel çalışır; **dosya sahipliği ayrıdır**, başkasının dosyasına dokunulmaz.

## Ortak kurallar
- Önce `docs/CONTRACT.md` ve `docs/perf/karsilastirma.md` oku. Bulgu numaraları (#) bu tabloya göredir.
- Kullanıcının ekranında pencere açan test, gerçek indirme, gerçek site isteği YOK. Doğrulama: birim/entegrasyon testleri (yerel HttpListener, görünmez), `dotnet build` 0 hata 0 uyarı, `dotnet test`, `node extension/tests/run.mjs`.
- Kullanıcının çalışan `dist\TDownloader.exe`'sine, gerçek pipe'ına (`TDownloader.Ipc`), Brave profiline dokunma. Registry yok. `dist\`/`dist-new\`'e yayın yok (ana agent yapar). Push yok.
- Mevcut davranış korunur: IDM tarzı onay penceresi, sayfa içi video düğmesi, HLS parça bazlı devam, kaliteler yalnızca tıklamada, eski indirmelerin tekrar sorulmaması, sarı-siyah tema.
- Her düzeltme için en az bir test. Commit sonu: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; kimlik `-c user.name="T-Downloader Dev" -c user.email=sapraktaha1@gmail.com`; yalnızca kendi dosyalarını stage'le (`git add -A` yok). Konu başına ayrı commit.
- CONTRACT'a geriye uyumlu alan/mesaj eklemek serbest; değişikliği CONTRACT.md'ye yaz (yalnızca Agent B CONTRACT'ı düzenler; A gerekiyorsa raporda önerir).

## Agent A — Motor + Video (Core)
Sahip olduğu dosyalar: `src/TDownloader.Core/Downloads/*`, `Queue/*`, `Settings/*` (yalnızca motorla ilgili alanlar), `RateLimiter`, `Util/*` (Coalescer hariç), `Video/*`, `tests/TDownloader.Core.Tests/*` (yeni test dosyaları).

1. **Tek işçi hatası tüm indirmeyi düşürmesin (#2).** Bağlantı hatası → yalnızca o işçi biter; parçası kuyruğa döner, kalan işçiler devralır. İndirme ancak *tüm* işçiler aynı aralıkta art arda başarısızsa Failed. Kalıcı 401/403/404/410 → URL bir kez yenilenmeye çalışılmaz, doğrudan Failed (yalnızca bu durumda). 429/503 → `Retry-After` uyulur, üstel bekleme + jitter, host başına geçici bağlantı azaltma (ör. yarıya).
2. **Senkron disk yazımı (#3).** Dosya `FileOptions.Asynchronous` ile açılsın, `RandomAccess.WriteAsync` kullanılsın; işçi başına yazma ArrayPool tamponuyla.
3. **Hız sınırı × okuma zaman aşımı.** Hız sınırlayıcıda beklenen süre okuma zaman aşımına sayılmasın (zaman aşımı yalnızca soket okumasını ölçsün).
4. **Bağlantı tavanı (64).** Aynı host için toplam etkin bağlantı ayarlanabilir üst sınırı olsun (varsayılan 32); kuyruğa alınan işçiler zaman aşımına düşmesin, sırayla başlasın. `MaxConnectionsPerServer` ile tutarlı.
5. **HLS tek parça hatası (#4).** Parça başına 401/403'te playlist yenilenir (imzalı URL) ve yeniden denenir; parça başına 5 deneme + üstel bekleme. Başarısız kalırsa indirme *Duraklatıldı/Hata* olur, inmiş parçalar KORUNUR; otomatik yt-dlp'ye sıfırdan geçiş yalnızca hiç parça inmediyse.
6. **HLS disk 3x (#5).** Birleştirme sırasında parça dosyalarını birleştirdikçe sil (akışlı birleştirme + silme), remux sonrası ara dosyayı hemen sil. İndirme başlarken ve birleştirmeden önce hedef sürücüde boş alan kontrolü (tahmini boyut × 1.1); yetersizse anlaşılır hata. Birleştirme/remux sırasında ilerleme bildir.
7. **Ayrı ses izi hatası sessizce yutulmasın.** Ses izi inemezse indirme hata verir (sessiz film üretme).
8. **Temizlik.** Silinen indirmelerde `DwnlData\<id>` ve yt-dlp `.part/.ytdl` dosyaları silinsin. yt-dlp süreçleri Job Object ile bağlansın (App çökünce ölsün).
9. **downloads.json.** Girintisiz yaz, çerezleri kalıcı dosyaya YAZMA (yalnızca bellekte), atomik yazma (tmp+rename) ve bir önceki sürümün `.bak` yedeği; bozuk dosyada `.bak`'tan yükle.
10. **Olay abonesi istisnası kuyruğu durdurmasın** (`ProcessQueue` finally'de), Pause yarışı (`_cts` oluşmadan gelen Pause) korunsun.

## Agent B — Arayüz + IPC + Host + Eklenti
Sahip olduğu dosyalar: `src/TDownloader.App/*`, `src/TDownloader.Core/Ipc/*`, `src/TDownloader.Host/*`, `extension/*`, `docs/CONTRACT.md`, `tests/TDownloader.Host.Tests/*`, eklenti testleri.

1. **KRİTİK — Onay penceresi × 5 sn zaman aşımı (#1).** Yeni akış (IDM gibi):
   - App `download` mesajını alınca, yakalama kuralları uygunsa **hemen** `downloadResult accepted:true` döner, sonra onay penceresini gösterir. Pencerede "İptal" = hiçbir şey inmez (tarayıcıya geri bırakma YOK).
   - App yakalamayı reddederse (kural/kapalı) hemen `accepted:false`.
   - IPC sunucusu bağlantı başına mesajları **seri beklemesin**: her mesaj işleyicisi ayrı görev olarak çalışsın, yanıtlar `requestId` ile eşleşsin, yazma tek kilitle. Onay penceresi açıkken `media`, `ping`, `qualities` akmaya devam eder.
   - Eklentide zaman aşımı 5 sn → 15 sn (App soğuk başlangıcı için); zaman aşımında tarayıcıya bırakma kalır (App yoksa).
   - Çoklu yakalama: pencereler sırayla, aynı anda 50 indirmede çift indirme olmamalı (test: sahte App ile 50 eşzamanlı `download` → hepsi 15 sn içinde yanıtlanır).
   - Sağ tık "tüm linkleri indir": seri 5 sn bekleme yerine tek toplu mesaj veya paralel.
2. **webRequest filtresi (#6).** `onResponseStarted` yalnızca `types: ["media","xmlhttprequest","other","sub_frame"]` ve URL/mime ön elemesiyle; fMP4/TS segmentleri (m3u8 içeriği tarafından listelenen) medya listesine tek tek eklenmesin; sekme başına kota mantığı segmentlerle dolmasın.
3. **`media` mesajı artımlı.** Yalnızca yeni öğeler gönderilsin; çerez öğe başına değil, mesaj başına bir kez ve yalnızca tıklamada (`qualities`/indirme) gerekirse alınsın. Host 1 MB sınırına yaklaşan mesaj bölünsün veya kırpılsın, sessizce düşmesin (log).
4. **Kalite istekleri.** `_info` önbelleği kilitli (veya yalnızca UI thread) ve sınırlı (ör. son 50, LRU); anahtar imzasız URL. Aynı kaynak için devam eden istek paylaşılsın; menü kapanınca/sekme değişince iptal edilsin (yt-dlp süreci öldürülsün); aynı anda en fazla 2 yt-dlp -J.
5. **Sınırsız büyüyen durum.** `_tabs`/`Seen`, `_progress`, `_recentPrompts`: sekme kapanınca (eklentiden yeni `tabClosed` mesajı) ve tamamlanan indirmelerde temizlensin; üst sınır.
6. **Büyük liste.** `UpdateStatus` sayaçları artımlı tutsun (her çağrıda Items kopyası + LINQ yok); `Get(id)` sözlükle O(1) (Core'da `DownloadManager` A'nın dosyası — gerekiyorsa A'ya bırak, App tarafında kendi sözlüğünü tut); kategori değişiminde ListViewItem'ları yeniden üretmek yerine önbellekten filtrele; toplu seçim/silmede `BeginUpdate` + tek güncelleme.
7. **Host.** Cooldown kontrolü bağlanma denemesinden önce; `host.log` boyutla döndürülsün (ör. 1 MB).
8. **Pano takibi** UI thread'ini bloklamasın (kısa denemeli, arka planda).
9. **Eklenti CPU.** `nativeButton=false` iken geometri altyapısı (MutationObserver, interval, mousemove) hiç kurulmasın; button.js rAF döngüsü yalnızca düğme görünürken ve video görünür alandayken çalışsın; mousemove'da tüm `<video>`ları tarama yerine IntersectionObserver.
