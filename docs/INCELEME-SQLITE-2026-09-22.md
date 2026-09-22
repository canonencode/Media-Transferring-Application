# SQLite katmanı — çok ajanlı inceleme bulguları

**Tarih:** 2026-09-22
**İncelenen commit:** `423177f`
**Durum:** A ve B gruplarının tamamı ile D grubu **düzeltildi** (2026-09-23).
Kalanlar: C grubu (iş parçacığı yarışları), E grubu (şema), F grubu (test delikleri).

**2026-09-23 — düzeltmeler** (`docs/sqlite-bulgular/` testleri test projesine taşındı,
68 test, 18 başarısız → 0; takım 360 → 434, süre 94 sn → 10 sn):

| Bulgu | Nasıl kapatıldı |
|---|---|
| A1, A2 | `finished` bayrağı: bitmiş sink olay kabul etmiyor, ikinci `OnScanFinished` reddediliyor |
| A3 | Retry, tally'ye yeni bir listeleme hatası düşüp düşmediğine bakarak yeniden yürüyüşün başarısını doğruluyor; başarısızsa `stillUnreadable` sayılıyor, kurtarıldı denmiyor |
| A4 | `ScanTally.UnresolvedObjects` + `ScanOutcome.UnresolvedObjects`; `IsTrustworthy` onu da gözetiyor. `HiddenSubtrees` bunun alt kümesi olduğu için tek koşul ikisini kapsıyor |
| A5 | `CultureInfo.InvariantCulture`. `Z` soneki **eklenmedi** — mevcut satırlar onsuz ve biçim değişikliği veri göçü demek, `user_version` işiyle birlikte yapılmalı |
| B1 | `ScanId == 0` ve `ExecuteNonQuery() != 1` gürültülü patlıyor |
| B2, B3 | `Dispose` kaybedilen satır sayısını stderr'e bildiriyor; `finally` ile temizlik garanti |
| B4 | `long.MaxValue`'yu aşan boyut `NULL` |
| B5 | Kurucu, `Open()` sonrası hata olursa bağlantıyı kapatıyor; **havuzlama kapatıldı** (`Pooling=false`) — havuz `Dispose`'dan sonra dosyayı açık tutuyordu |
| B6 | `":memory:"` ve `file:` reddediliyor; yol `GetFullPath` ile normalize |
| B7 | Saklanamayan ad **yine kaydediliyor** (dosya kaybedilmiyor) ama `[UNSTORABLE NAME]` ile bildiriliyor |
| D1 | `DefaultTimeout = 3` — 30 sn donma yerine 3 sn'de açık hata |
| D2 | İkinci `OnScanStarted` hiçbir şey yazmadan reddediliyor |
| D3, D4 | `DeviceIdentity.TrimmedSerial`; `serial` sütunu ve `key_is_fallback` artık aynı şeyi söylüyor |

**İki bulguda ajanla aynı fikirde olunmadı, gerekçeleriyle testlere yazıldı:**
- Seri numarasında büyük-küçük harf **katlanmıyor**. Katlamak birleştirme yönü (iki telefonun
  geçmişini karıştırmak, bölmekten kötü) ve `ToUpperInvariant` Türkçe `ı`'yı `I`'ya çevirerek
  gerçekten farklı iki seriyi birleştirirdi.
- İki eşzamanlı yazıcı **desteklenmiyor**. SQLite tek yazıcıya izin veriyor ve gruplama,
  gerçek `Process.Kill` ile kanıtlanmış dayanıklılık özelliği; onu feda etmek yerine ikinci
  yazıcı hızlı ve açık şekilde reddediliyor.

**Saklanmayan tek yeni alan:** `UnresolvedObjects` scan satırına sütun olarak eklenmedi —
`CREATE TABLE IF NOT EXISTS` mevcut veritabanlarına sütun ekleyemiyor (E1). Yine de
trust açısından kritik olan kısmı `status` üzerinden saklanıyor.

**2026-09-23'te yapılanlar** (liste dışı, ayrıca istendi):
- Yorumlar koda göre yeniden yazıldı; yalan söyleyenler düzeltildi, bilinen hatalar
  yaşadıkları yerde bulgu numarasıyla işaretlendi (`ae2379b`).
- **Saklama/budama eklendi** — cihaz başına son 10 tarama. Şema ajanının "anlık görüntü
  modeli kalsın, budama sonra" önerisinin budama kısmı. Üç şeye dokunulmuyor: `running`
  satırı, en yeni `complete` tarama, ve saklama ≤ 0 ise hiçbir şey. `scan_error(scan_id)`
  ve `skipped_folder(scan_id)` indeksleri bununla birlikte geldi. 11 test (`cf37e62`).
- Ölçüm: tarama başına 4,3 MB; 10 tarama tavanı ≈ 43 MB.

Beş bağımsız ajan incelendi: uç durum testi (kod yazıp çalıştırdı), doğruluk incelemesi,
şema incelemesi, dikiş/bağlantı incelemesi, mutasyon testi.

**Üretilen araç:** `docs/sqlite-bulgular/SqliteScanSinkStressTests.cs` — 68 test, 18'i
başarısız. Test projesinin dışında duruyor (derlenmiyor). Düzeltme oturumunda
`Device Probe/Device Probe.Tests/` altına kopyalanıp çalıştırılacak; her düzeltme bir
testi yeşile çevirmeli.

---

## A. KARDİNAL KURAL İHLALLERİ

Kardinal kural: *yarıda kesilmiş bir tarama asla tam sayım olarak kaydedilememeli.*
Kaydedilirse, sonraki karşılaştırma görmediği dosyaları telefondan silinmiş sanar.

### A1. `OnScanFinished`'den sonra gelen dosyalar `complete` taramaya giriyor

`MediaTransfer.Core/SqliteScanSink.cs` — `OnScanFinished` (`Commit()` + durum UPDATE)
sink'i kapalı olarak işaretlemiyor. Sonraki `OnFile` çağrıları başarıyla yazıyor.

Gerçek `Process.Kill()` ile kanıtlandı:

```
2.600 dosya bildirildi
status       = complete
finished_utc = 2026-09-22 14:38:47.364
file satiri  = 2000
total_files_seen = 100      <-- kendi alt satirlariyla celisiyor
```

Sonraki karşılaştırma 2.000'lik tam sayım okur, 600 dosyayı silinmiş sayar.

Asimetri şurada: `OnFile` taramadan *önce* çağrılırsa gürültülü patlıyor (`NotStarted()`),
*sonra* çağrılırsa sessizce kabul ediyor. Tehlikeli olan ikincisi.

**Yön:** `finished` bayrağı; `OnScanFinished` sonrası gelen her olay ya reddedilmeli
(gürültülü) ya da satırı tekrar `running`'e düşürmeli. Reddetmek tercih edilir.

**Test:** `FilesReportedAfterOnScanFinished_CannotLandInACompletedScan`

### A2. `OnScanFinished` iki kez çağrılınca `partial` → `complete` yükseliyor

Aynı dosya. İkinci çağrı aynı satırı yeniden yazıyor ve sonuncu kazanıyor.
`SubtreeLosses = 3` olan durmuş bir tarama tam sayıma dönüştü.

`Program.cs` bunu `Interlocked.Exchange(ref outcomeReported, 1)` ile koruyor (iki çağrı
yerinde de), ama sınıfın kendisi izin vermemeli — bir `catch` ile bir `finally`'nin ikisinin
de bitiş çağırması sıradan bir hata.

**Yön:** A1'in `finished` bayrağıyla aynı mekanizma; ikinci çağrı ya no-op ya da hata.
Durum asla yükselmemeli, yalnızca düşebilmeli.

**Test:** `OnScanFinished_Twice_CannotUpgradeAPartialScanToComplete`

### A3. Başarısız yeniden yürüyüş "kurtarıldı" sayılıyor

`Device Probe/Program.cs:723-731`

```csharp
PrintTree(objectId, recoveredPath);      // :723  sessizce basarisiz olabilir
recoveredFolders++;                       // :724
if (stage is Enumerate or EnumerateNext)  // :728
    tally.MarkContainerRecovered(objectId); // :731
```

`PrintTree` başarısızlığını çağırana bildirmiyor. Üç yoldan dönebiliyor:
- `:822-824` — `Enumerate` yine başarısız, **aynı `objectId`** için yeni hata kaydediyor
- `:852-854` — `EnumerateNext` başarısız, klasör yalnızca kısmen listelendi
- `:813` — iptal bayrağı setli, hiç iş yapmadan dönüyor

Üçünde de akış `MarkContainerRecovered`'a düşüyor. `ScanTally.SubtreeLosses` kurtarma
kümesini kullandığı için **hem eski hem yeni hata** siliniyor.

Sonuç: listelenemeyen ve retry'da da listelenemeyen klasör `SubtreeLosses`'a 0 katkı
yapıyor → `IsTrustworthy` true → konsol "every folder was listed" diyor →
veritabanına `status='complete'` yazılıyor.

`ScanTally` sözleşmesi ("başarılı yeniden yürüyüşten sonra çağır") doğru; çağrı yeri
ön koşulu ihlal ediyor.

**Yön:** `PrintTree` başarı/başarısızlık döndürsün (ya da hata sayısını önce/sonra
karşılaştıran bir kalıp), `MarkContainerRecovered` yalnızca gerçekten başarılıysa çağrılsın.
`recoveredFolders++` de aynı koşula bağlanmalı (bkz. A3b).

### A3b. `recoveredFolders++` kendi yorumuyla çelişiyor

`Program.cs:724` ile `:740-743`. Yorum diyor ki: *"Counted only after the work actually
happened. The old version incremented before the Android check and before the re-walk, so
blocked folders and folders that failed again both still reported as recovered."*

Android kontrolü düzeltilmiş (`:702-706` önce `continue` ediyor), ama **yine başarısız olan
klasörler hâlâ kurtarılmış sayılıyor**. Bu sayı `RetryOutcome` üzerinden
(`Program.cs:751`) `scan.recovered_folders` sütununa gidiyor.

### A4. `IsTrustworthy` iki kayıp türünü hiç görmüyor

`MediaTransfer.Core/IScanSink.cs:128-130` yalnızca `Completed / Stalled / Faulted /
CameraMode / UndeterminedFiles / SubtreeLosses`'a bakıyor.

Görmediği ikisi:

1. **`Properties` aşaması hataları.** `ScanTally.SubtreeLosses` (`ScanTally.cs:50`) bunları
   dışlıyor. Ama `ConsoleScanSink`'in kendi metni diyor ki: *"lost one object (and its
   subtree, if it was a folder)"*. Yani bir Properties hatası da alt ağaç kaybedebiliyor.
   `ScanOutcome` hiç hata sayısı taşımıyor.
2. **`RetryOutcome.HiddenSubtrees`** — *"folders whose entire contents were silently lost
   from this scan"* diye belgelenmiş, sütun olarak saklanıyor, verdict'e hiç dokunmuyor.

Konsol en azından hata dökümünü aşağıda basıyor; veritabanı durumu hiç nitelemiyor —
ve tasarımın "aşağı akış buna baksın" dediği alan `status`.

**Yön:** `ScanOutcome`'a çözülmemiş `Properties` hatası sayısı ve `HiddenSubtrees` eklensin,
`IsTrustworthy` ikisini de dikkate alsın.

### A5. Zaman damgaları ortam kültürüyle biçimleniyor

`SqliteScanSink.cs` — `Timestamp()` içinde `CultureInfo.InvariantCulture` yok:

```
th-TH:  2569-09-22 ...   (Budist takvim)
ar-SA:  1448-04-11 ...   (Hicri - gun bile farkli)
fa-IR:  1405-06-31 ...   (Persian)
tr-TR:  dogru
```

`started_utc`/`finished_utc` TEXT ve **sözlüksel sıralanıyor**. ar-SA'da bugünün taraması
yıl 1448 yazıyor ve tablodaki her Gregoryen taramadan **önce** sıralanıyor → "en yeni
tarama" en eskiyi getiriyor → bayat sayım canlı telefonla karşılaştırılıyor.

Bir Windows bölge ayarı tetiklemeye yetiyor; kod değişikliği gerekmiyor.

**Yön:** `ToString("...", CultureInfo.InvariantCulture)`. Aynı anda `Z` soneki eklensin
(değer kendini tanımlasın).

**Test:** `TheTimestampIsTheSameInEveryCulture`

### A6. Tarama hangi depolama köklerini gezdiğini kaydetmiyor

Şema `scan_root` tablosu içermiyor. SD kartı çıkarılmış telefon hatasız taranır,
`status='complete'` yazılır, sonraki karşılaştırma **karttaki her dosyayı silinmiş sanar**.
`IsTrustworthy`'nin "aynı kökler var mıydı" diye bir kavramı yok.

J7 Prime2'de 3 depolama nesnesi ölçülmüştü (dahili + SD) — senaryo gerçek.

Aynı tablo, yerelleştirilmiş depolama adı riskini de kapatıyor: telefonun dili değişirse
"Dahili depolama" → başka bir şey olur, **her yol değişir**, her eski dosya silinmiş ve her
yeni dosya yeni görünür.

**Yön:** `scan_root(scan_id, object_id, persistent_id, name)`; silinme çıkarımı cihaz
başına değil kök başına.

---

## B. SESSİZ HATALAR

| # | Nerede | Ne |
|---|---|---|
| B1 | `SqliteScanSink.OnScanFinished` | `ScanId = 0` iken `UPDATE ... WHERE scan_id = 0` sıfır satır eşleşiyor, `ExecuteNonQuery` 0 döndürüyor, istisna yok. Çağıran baştan sona hatasız bir tarama görüyor, veritabanında hiçbir şey yok. `OnFile` aynı durumda patlıyor; sessiz olan, patlaması en gereken çağrı |
| B2 | `SqliteScanSink.Dispose` | `catch (SqliteException) { }` — 999 satıra kadar veri tek kelime edilmeden gidiyor. Yorum "kaydedecek yer kalmadı" diyor; bu *kaydetmemeyi* haklı çıkarır, *susmayı* değil. Durum `running` kaldığı için kardinal ihlal değil, ama kodun tek sessiz yutma noktası |
| B3 | `SqliteScanSink.Dispose` | `try/finally` yok. `Commit()` `SqliteException` dışında bir şey atarsa (bağlantı altından kapandıysa `InvalidOperationException`) dört komut, işlem ve bağlantı kapatılmıyor → dosya kilidi elde kalıyor. `CompositeScanSink` bunu yakalıyor, ama doğrudan çağıran için `using`'den fırlıyor |
| B4 | `SqliteScanSink.OnFile` | `(long)size` işaretsizden işaretliye kontrolsüz dönüşüm. `0xFFFFFFFFFFFFFFFF` (yaygın MTP "boyut bilinmiyor" sentinel'i) **-1** olarak yazılıyor. `ScanTypes.cs` kendi yorumu bunu yasaklıyor: *"bilinmeyen boyut için 0 yazmak eksik veriyi ölçülmüş gibi sokar"* |
| B5 | `SqliteScanSink` kurucu | `connection.Open()` çalıştıktan sonraki PRAGMA/şema hatası bağlantıyı kapatmadan fırlıyor. Fırlatmanın amacı çağıranın geri çekilebilmesi, ama dosya GC'ye kadar kilitli kalıyor — aynı dosyaya yeniden deneme de başarısız. (Diğer bütün kurucu korumaları geçti: dizin, olmayan sürücü, `""`, `"   "`, geçersiz ad, veritabanı olmayan dosya) |
| B6 | `SqliteScanSink` kurucu | `":memory:"` yolu kabul ediliyor. 5.000 dosya yazıldı, `complete` işaretlendi, hiçbir istisna yok, **disk üzerinde hiçbir şey yok**. `Path.GetFullPath` yalnızca klasör oluşturmak için kullanılıyor, `DataSource`'a ham string gidiyor |
| B7 | `SqliteScanSink.OnFile` | Eşsiz vekil karakter (lone surrogate) sessizce `U+FFFD`'ye çevriliyor. MTP adları COM'dan ham UTF-16 geliyor ve hiçbir yerde doğrulanmıyor. Saklanan yol cihazınkiyle eşleşmiyor → dosya bir daha bulunamıyor |
| B8 | `Program.cs:249` + `CompositeScanSink.cs:96-102` | SQLite açılamazsa uyarı yalnızca **stderr**'e gidiyor (başarı satırı stdout'a). Bu proje stdout'u loga yönlendiriyor → uyarı logda yok. Ayrıca tarama sonunda hatırlatma yok: `faults` yalnızca *sonradan* patlayan sink'leri içeriyor, hiç kurulamayan sink orada değil. 20 dakikalık tarama "COMPLETE" diye bitiyor, kaydedilmediğine dair hiçbir iz yok |

---

## C. İŞ PARÇACIĞI YARIŞLARI

Bekçi `scanTask.Wait(20_000)` başarısız olunca rapor veriyor (`Program.cs:533-541`) — yani
**tarama iş parçacığı hâlâ canlı.** Hiçbir sink kilit tutmuyor.

| # | Ne |
|---|---|
| C1 | `BuildOutcome` bekçi iş parçacığında `ScanTally.errors` listesini **numaralandırırken** tarama iş parçacığı `errors.Add` yapıyor → `InvalidOperationException`. Ve bu istisna `Dispatch`'in `try`'ının **dışında** oluşuyor (çünkü `BuildOutcome` argüman olarak değerlendiriliyor) → yakalanmıyor → süreç `Console.Out.Flush()` ve `FailFast`'ten **önce** ölüyor. "Gitmeden önce ne bulduğunu bildir" güvenlik ağı kendi yarışıyla etkisizleşiyor. `outcomeReported` zaten 1 olduğu için başka kimse de raporlamıyor |
| C2 | Aynı pencerede `OnFile/OnFolder/OnError/OnFolderSkipped` ile `OnScanFinished` çakışıyor: `CompositeScanSink.live` listesi indeksle gezilirken `RemoveAt`; `ConsoleScanSink.Errors/SkippedFolders` numaralandırılırken `Add`; `SqliteScanSink`'te aynı bağlantı ve aynı işlem iki iş parçacığından |
| C3 | `sinks.Dispose()` bekçi hâlâ `OnScanFinished` içindeyken çalışabiliyor → `connection.Dispose()` UPDATE yürürken → `ObjectDisposedException` `Dispatch` tarafından yutuluyor → **gerçekten bitmiş bir tarama `running` kalıyor** |
| C4 | `SqliteScanSink.retry` alanı tarama iş parçacığında yazılıp bekçi iş parçacığında bariyersiz okunuyor → bayat `null` okunup retry sütunlarına NULL yazılabiliyor |

**Yön:** sink'lerin etrafına tek bir kilit, ya da bekçinin rapor öncesi tarama iş parçacığını
kesin olarak durdurması. `Program.cs:312-317`'deki yorum bu korumanın var olduğunu iddia
ediyor — **yanlış ve yarışı gizliyor**, mutlaka düzeltilmeli.

---

## D. SAĞLAMLIK

| # | Ne |
|---|---|
| D1 | İkinci yazar 30 saniye bloke olup `SQLite Error 5: database is locked` veriyor. WAL iki yazara izin vermiyor ve sink 1.000 satır boyunca yazma işlemini açık tutuyor. İki telefon, uygulamanın iki kez açılması, ya da çökmüş taramadan sızan sink → 30 sn donma. Yön güvenli (ikinci tarama `complete` iddia etmiyor) ama kullanıcı donma sanıp fişi çekiyor, aynı kilide tekrar giriyor |
| D2 | `OnScanStarted` iki kez: ikinci satır ekleniyor (commit'li, `running`), `PrepareStatements()` önceki dört komutu **kapatmadan** değiştiriyor, sonra `BeginBatch()` "nested transaction" diye patlıyor. Geriye iki kalıcı `running` satırı kalıyor ve `ScanId` yanlışını gösteriyor. (İlk taramanın satırları kaybolmuyor) |
| D3 | Seri numarasında boşluk/büyük-küçük harf farkı cihazı bölüyor: `"SER123"`, `"SER123 "`, `" SER123"` → 3 cihaz satırı. `DeviceKey` `IsNullOrWhiteSpace` kontrol ediyor ama `Trim()` yapmıyor. `ScanTypes.cs` bölmenin birleştirmekten iyi olduğunu savunuyor (doğru), ama dolgulu MTP serileri yaygın ve `Trim()` bedava |
| D4 | `Nullable()` `IsNullOrEmpty`, `DeviceKey` `IsNullOrWhiteSpace` kullanıyor → `"   "` serisi için `key_is_fallback=1` ama `serial='   '` yazılıyor: aynı anda hem seri var hem yok diyen satır |

---

## E. ŞEMA

| # | Ne | Aciliyet |
|---|---|---|
| E1 | `PRAGMA user_version` yok. `CREATE TABLE IF NOT EXISTS` yarın sütun eklendiğinde mevcut veritabanlarında **sessizce hiçbir şey yapmaz**, sonra kod olmayan sütuna INSERT eder ve kullanıcının makinesinde çalışma anında patlar. **Testler bunu asla yakalayamaz — her test sıfırdan dosya açıyor.** ~20 satır | **ilk sıra** — maliyeti bekledikçe artan tek madde |
| E2 | `scan.status` üzerinde `CHECK (status IN (...))` yok. Güven modelinin tamamı bu değerlere dayanıyor ama bugün her string yasal. Aynısı `camera_mode`, `recovered`, `key_is_fallback` için `CHECK (x IN (0,1))` | yüksek |
| E3 | Çökmüş taramanın `running` satırı sonsuza dek `running`. "Şu anda çalışıyor" ile "üç hafta önce öldü" aynı değer. Açılışta `UPDATE scan SET status='abandoned' WHERE status='running'` | yüksek |
| E4 | İndeks eksik: `persistent_id` (kimlik sorgularının tamamı tam tarama). **`scan_error(scan_id)` ve `skipped_folder(scan_id)` eklendi** (2026-09-23, budamayla birlikte) | orta |
| E5 | `file_id`/`folder_id` üzerindeki `AUTOINCREMENT` kimsenin kullanmadığı bir garanti için tarama başına ~13.900 ek `sqlite_sequence` güncellemesi yapıyor. `scan_id`'de **kalmalı** (her yerden referans veriliyor) | orta |
| E6 | Zaman damgalarında `Z` yok — değer kendini UTC olarak tanımlamıyor. A5 ile birlikte yapılsın | düşük |
| E7 | `camera_mode` `OnScanStarted`'dan, `status` `outcome.CameraMode`'dan geliyor — aynı gerçek, iki kaynak, çapraz kontrol yok | düşük |
| E8 | `PRAGMA foreign_keys=ON` bağlantı başına. Gelecekteki UI okuyucusu ya da `sqlite3` oturumu için FK'ler **kapalı** → bildirimler pratikte tavsiye niteliğinde | not |

**Doğrulanan karar:** `file(scan_id, path)` üzerinde UNIQUE olmaması doğru. Tek düzeltme:
savunulan şey "upsert olmasın"dı, "kısıt olmasın" değil — UNIQUE **gürültülü patlar**,
sessizce birleştiren `ON CONFLICT DO UPDATE`.

---

## F. TEST DELİKLERİ (mutasyon testi: 22 mutasyondan 17'si yakalandı)

| # | Hayatta kalan mutasyon | Eksik test |
|---|---|---|
| F1 | `OnScanFinished`'den `WHERE scan_id = $scan` silindi → **her tarama satırı** yeniden yazılıyor, 349/349 geçti | Birden fazla tarama içeren veritabanı. Her SQLite testi **tek satırlık** veritabanıyla çalışıyor. Bu mutasyon yarıda kalmış eski bir `running` satırını, alakasız yeni bir taramayla **geriye dönük** `complete` yapıyor |
| F2 | `RowsPerTransaction` **her iki yönde** (1 ve 1.000.000) değiştirildi, ikisi de geçti | İkinci bağlantıdan, tarama sürerken, `RowsPerTransaction` üstü satırın **görünür** olduğunu doğrulayan test. `RowsCommittedBeforeAKill_SurviveIt` ölümü `Dispose()` ile taklit ediyor ve `Dispose()` commit atıyor → gruplama tamamen kapalıyken de geçiyor. `MoreRowsThanOneTransactionHolds_AreAllWritten` sınırın geçildiğini hiç doğrulamıyor. **İkisi de iddia ettikleri şeyi kanıtlamıyor** |
| F3 | `Commit()`'ten `AttachTransaction()` silindi, geçti (eşdeğer mutant değil — doğrulandı) | `OnScanFinished`'den sonra gelen olay (A1 ile aynı test bunu da kapatır) |
| F4 | `BuildOutcome`'da `MediaFiles → Documents → UndeterminedFiles` döndürüldü, geçti | `BuildOutcome_CarriesEveryFigureTheSinksNeed` her türden **birer** dosya sayıyor (hepsi `1`) → yer değiştirme görünmez. Aynı testte `SignatureStats` 5/2/1/3 ile kurulmuş, yani risk düşünülmüş ama bu grupta kaçırılmış. `UndeterminedFiles` `IsTrustworthy` girdisi olduğu için kardinal ilgili |
| F5 | `CompositeScanSink.Dispose`'dan `try/catch` silindi, geçti | `Dispose()`'dan fırlatan sink fixture'ı. `SqliteScanSink.Dispose` `Commit()` çağırıyor ve dolu diskte `Commit` fırlatır — üretimde en olası senaryo |

**Flake:** `ASecondScanOfOneDevice_ReusesItsRow_AndKeepsTheFirstSighting` milisaniye
çözünürlüklü zaman damgası karşılaştırıyor; ilk koşuda iki tarama aynı milisaniyeye düşüp
**mutasyonla birlikte geçti**. Ayrıca `last_seen_utc`'nin güncellendiğini hiçbir test
doğrulamıyor.

**Vakum testler:** `RowsCommittedBeforeAKill_SurviveIt`, `MoreRowsThanOneTransactionHolds_AreAllWritten`
(ikisi de yukarıda), `OnFolder_PrintsNoFileLines` (`Assert.All` boş listede geçer).

**Güçlü sonuç:** `MarkContainerRecovered` küme yerine azalan sayaç yapıldığında, beş kurtarma
testinden tam olarak sayacın yanlış yapacağı ikisi patladı, doğru yapacağı üçü geçti —
implementasyona değil gerçek hata moduna karşı yazılmış test.

---

## G. BAYAT YORUMLAR (refaktörün geride bıraktıkları)

| Yer | Sorun |
|---|---|
| `Program.cs:312-317` | `outcomeReported` guard'ının iki iş parçacığının listeye aynı anda dokunmasını engellediğini iddia ediyor. **Engellemiyor** (C1, C2) ve yorum yarışı gizliyor |
| `Program.cs:740-743` | "Counted only after the work actually happened" — yine başarısız olan klasörler hâlâ kurtarılmış sayılıyor (A3b) |
| `Program.cs:335-345` | `PRE-SQLITE: the counter goes` — gitmedi. `filePropertyMisses` hâlâ sayılıyor, `ScanOutcome`'da taşınıyor ve artık `file_property_misses` olarak **saklanıyor** |
| `Program.cs:299-304` | "An upsert on UNIQUE(device_id, path) would make a duplicate WRITE harmless" — şemada böyle bir kısıt yok ve `SqliteScanSink.cs` neden hiç olmayacağını uzun uzun anlatıyor. İki yorum birbiriyle çelişiyor |
| `Program.cs:204-207`, `:210-219` | `classified_by` ve `ScanStage.Stream` gerçekten hâlâ yok (doğrulandı), ama bu rakamların depolamaya ulaşmadığını ima ediyorlar — üçü de artık scan satırında. Silinmemeli, yeniden yazılmalı |

---

## H. GİZLİLİK (şema ajanı)

Saklanan: telefondaki her dosya adı ve tam yol, cihaz seri numarası, üretici, model, boyut,
tarih. Dosya içeriği, küçük resim, hash **yok**. Saklanmaması gereken hiçbir şey saklanmıyor.

Karar şimdi verilmeli, geriye dönük düzeltmesi pahalı: **her "tanılama dışa aktar" özelliği
sayı, durum ve hata kodu üretmeli — asla yol.** Yollar uygulama kullanımını açığa vuruyor
(WhatsApp/Telegram/Signal klasör varlığı ve hacmi) ve Android dosya adları çekim zamanını
kodluyor. Seri numarası da birincil anahtar — bu veritabanı asla telemetri yükü olmamalı.

---

## I. ÖNERİLEN SIRA

1. **A5** (kültür) — tek satır, en ucuz kardinal düzeltme
2. **A1 + A2** (`finished` bayrağı) — ikisi tek mekanizma
3. **B1** (`ScanId = 0` sessiz no-op) — A1 ile aynı bölge
4. **A3 + A3b** (`Program.cs`, kurtarma ön koşulu)
5. **A4** (`IsTrustworthy` + `ScanOutcome` genişletme)
6. **B2, B3, B4, B5** (`SqliteScanSink` sessiz hataları — küçük, bağımsız)
7. **E1** (`user_version`) — sonraki şema değişikliğinden **önce**
8. **E2, E3** + **A6** (`scan_root`) — tek şema sürümünde
9. **C1-C4** (iş parçacığı) — tasarım kararı gerektiriyor, tek başına ele alınmalı
10. **F1-F5** (test delikleri) + `docs/sqlite-bulgular/` testlerinin taşınması
11. **G** (bayat yorumlar) — her biri ilgili düzeltmeyle birlikte
12. **B6, B7, D1-D4** — nadir ya da UX; en sona

**Not:** A6 ve E grubu şema değiştiriyor; E1 onlardan önce gelmeli, yoksa mevcut
veritabanları sessizce eski şemada kalır.
