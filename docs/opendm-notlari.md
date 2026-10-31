# OpenDM incelemesi: T-Downloader'a alınan mantık

Kaynak: `Desktop\HemesTAHA\OpenDM` (kullanıcının kendi projesi; yalnızca okundu).

## OpenDM'de bulunanlar
- **Yakalama (extension/background.js):** `webRequest.onHeadersReceived` ile tüm sekme/çerçevelerde m3u8/mpd/mp4 sniff; medya türü + boyut süzgeci
  (küçük/segment atılır), her öğeye tarayıcı çerezi eklenir ve App'e iletilir. Referer olarak sekme adresi kullanılır.
- **Hedef seçimi (`resolveTarget`):** ana oynatıcı → sayfa adresi (yt-dlp çıkarıcı), `<video>` http dosyası → o dosya, kart → yakın bağlantı,
  sniff edilen son HLS/DASH, olmazsa sayfa adresi. Akışlar yt-dlp'ye `--referer`/çerez başlığıyla verilir (`_base_opts`: http_headers).
- **Düğme (content.js):** sayfa DOM'unda Shadow DOM; yalnızca ana video (boyut, oran, önizleme kartı değil); fare üstünde belirir, 3.5 sn sonra
  gizlenir; kaydırma/yeniden boyut/tam ekran (fullscreenElement içine takılır); tıklayınca menü ANINDA açılır, kesin kaliteler gelince değişir.
- **Motor:** Content-Disposition (UTF-8 dahil) dosya adı, geri çekilmeli yeniden deneme, `onDeterminingFilename` ile ad belirlendikten sonra devralma.
- OpenDM'de **yt-dlp'siz doğrudan m3u8 ayrıştırma yok** (her şey yt-dlp); T-Downloader bunu kendisi ekledi (aşağıda).

## T-Downloader'a uygulananlar
1. **iframe oynatıcı başlıkları:** `media.js` artık her çerçeve için `onSendHeaders` ile tarayıcının gerçekten gönderdiği Referer/Origin'i, yoksa isteği yapan
   iframe belge adresini kaydeder; çerez ve User-Agent eklenir. `media` mesajına geriye uyumlu alanlar eklendi.
2. **Başlık iletimi:** App `MediaContext` ile Referer/Origin/Cookie/UA'yı hem yt-dlp'ye (`--referer`, `--add-header Origin:`, `Cookie:`, `--user-agent`,
   `--retries/--fragment-retries/--concurrent-fragments/--socket-timeout`) hem doğrudan HLS indiriciye hem doğrudan dosya indirmeye (çerez dahil) iletir.
   Çerez yalnızca çerezin geldiği sunucuya gider.
3. **Hızlı kalite listesi:** `HlsParser/HlsClient` master playlist'i yt-dlp'siz okur (yüzlerce ms); süre için en yüksek varyantın playlist'inden boyut tahmini.
   HLS olmayan/başarısız durumda yt-dlp'ye düşer.
4. **yt-dlp'siz HLS indirme:** parçalar paralel indirilir, AES-128 çözülür, fMP4 (EXT-X-MAP/BYTERANGE) desteklenir, sahte resim başlığı olan TS parçaları
   temizlenir, ffmpeg varsa mp4'e remux (ayrı ses kanalı dahil), yoksa `.ts`. DRM (SAMPLE-AES/FairPlay/Widevine) ve canlı yayın reddedilir.
   Hata olursa (DRM hariç) ve yt-dlp varsa yt-dlp'ye düşülür.
5. **iframe içi video:** (a) sayfa içi düğme her çerçevede kendi videosu için çizilir; (b) eski geometri yolunda iframe rect'i üst çerçeve
   koordinatına çevrilir (`postMessage` + `event.source` ile iframe eşleşmesi).
6. **Sayfa içi düğme + menü (OpenDM content.js'in uyarlaması):** `button.js`, sarı-siyah tema, Shadow DOM, her karede (rAF) konum güncelleme,
   kalite menüsü App'ten `qualities`/`qualitiesResult` ile gelir; seçim `qualitySelect` → IDM tarzı onay penceresi. Yerel düğme penceresi varsayılan kapalı.
7. **Motor/yakalama:** indirme yakalamada dosya adı boşsa tarayıcı adı belirleyene kadar (≤1.2 sn) beklenir (Content-Disposition adı App'e gider);
   "Dosya İndirme Bilgisi" yoklaması Content-Disposition adını önerir.
8. **Hata düzeltmesi (T-Downloader'da bulundu):** `geometry.resendWindow` canlı Map üzerinde dönerken `emit` aynı anahtarı yeniden yazdığı için pencere
   taşınınca sonsuz döngü (saniyede yüzlerce mesaj) oluşuyordu; anlık görüntü üzerinde dönülüyor.

## Alınmayanlar / eksikler
- DRM'li akışlar (Widevine/FairPlay) desteklenmez; canlı HLS yok; DASH (mpd) yalnızca yt-dlp ile.
- HLS indirme duraklat/devamda baştan başlar (parça düzeyinde devam yok).
- Bağlantı başına hata devralma ve onDeterminingFilename ile tutma (OpenDM) alınmadı; mevcut onCreated+pause akışı korundu.
