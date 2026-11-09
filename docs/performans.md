# Performans notları (Faz 3, kuramsal)

> Bu belge kod incelemesine dayanır; GUI çalıştırılmadı. Sayılar büyüklük mertebesidir.

## Ne zaten iyiydi
`BufferedListView` sahip-çizimli ve çift tamponlu, motor ilerlemesi `_dirty` kümesinde birikiyor, hız/kalan süre metni satır başına 1 sn kısıtlı (`UpdateGate`),
`downloads.json` atomik ve arka planda yazılıyor. Faz 3 bu temelin üstüne **görünmeyen işi sıfırlar**.

## Değişenler ve nedenleri
| Değişiklik | Neden / beklenen etki |
|---|---|
| Liste `VirtualMode` (`ViewIndex<Row>` + `RetrieveVirtualItem`) | Önce: N satırın tamamı `ListViewItem` olarak `Items`'ta, her ilerleme turunda `FillRow` N'e kadar. Şimdi: yalnız ekranda görünen satırlar için metin üretilir (`Row.Stale` bayrağı); 10.000 satırlık listede kare başına iş ~10.000 -> ~30-40 satır. Süzme/sıralama `ViewIndex` üzerinde, `ListView.Sort` yok (sanal modda desteklenmez). Seçim kimliklere göre korunur. |
| ~6 Hz tek zamanlayıcı (160 ms) + `RedrawItems` | Motor olayları (`ProgressChanged`) yalnız kirli kimlik işaretler (kilitli küme); Invoke/BeginInvoke yok. Zamanlayıcı turunda yalnız görünen aralıktaki (`LVM_GETTOPINDEX`/`COUNTPERPAGE`) değişen satırlar yeniden çizilir; `EndUpdate` tüm listeyi geçersiz kılmadığı için titreme yok. |
| Toplu işlemlerde `BeginUpdate/EndUpdate` | `RebuildList` ve Ctrl+A: tek yeniden çizim, tek durum güncellemesi. |
| Shell simgeleri asenkron (`ShellIcons.IndexFor`) | `SHGetFileInfo` ilk görülen uzantı için shell'e gider (onlarca ms, bazen yüzlerce). Artık UI thread'inde değil: yer tutucu (0. dizin) döner, simge arka planda yüklenir ve `IconsChanged` ile bir kez yeniden çizilir. Sonuç uzantı başına önbellekte. |
| Metin biçimlendirme önbellekleme | Hız/ETA/boyut metni yalnız görünen satır `Stale` iken ve satır başına en çok 1 sn'de üretilir; gizli satırlar için hiç üretilmez. |
| `downloads.json` yazımı 500 ms -> 2 sn (`DownloadManager.SaveDelayMs`), ayarlar `Debouncer` ile 2 sn | Art arda ilerleme/durum değişikliği tek diske yazma olur; tümü thread-pool'da. Çıkışta `FlushSettings` + `ShutdownAsync` kaydeder. Çökmede en fazla ~2 sn'lik ilerleme kaybı (parça durumu zaten diske ayrı yazılır). |
| `ProgressDialog` | Zaten ana zamanlayıcıdan (`Flush`) besleniyordu; ek Invoke yok. Yalnız açık pencereler güncellenir. |
| `CompletedDialog` | Tek pencere yeniden kullanılır, çizim `UserPaint` + çift tampon, yazı tipleri alan olarak tutulur; dosya varlık denetimi arka planda (ağ sürücüsü UI'yı bloklamaz). |

## UI thread denetimi (engelleyen çağrılar)
- `File.Exists` yalnız kullanıcı eylemlerinde (Aç, Klasörde göster, sürükle-bırak başlangıcı) — yerel, tek sefer; kabul edildi. Sürükle-bırakta çok sayıda seçili tamamlanmış öğe için döngü kalır.
- DNS/HEAD istekleri App'te UI thread'inde yok (motor / `PromptDownloadAsync` async).
- Ayar kaydı (`SaveSettings`/`ApplySettings`) artık UI thread'inde disk yazmaz.
