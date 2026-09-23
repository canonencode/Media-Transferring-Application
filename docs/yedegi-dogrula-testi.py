"""yedegi-dogrula.py'yi sahte bir yedek üzerinde sınar:  python docs/yedegi-dogrula-testi.py

Betiğin işi kötü haberi bildirmek, o yüzden asıl sınanması gereken şey temiz
durumu "sağlam" demesi değil - bozuk durumu yakalayıp yakalamadığı.
"""
import hashlib, os, shutil, sqlite3, subprocess, sys, tempfile

BETIK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "yedegi-dogrula.py")
KOK = os.path.join(tempfile.gettempdir(), "dogrula-testi")


def sha(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        h.update(f.read())
    return h.hexdigest().upper()


_acik = []


def kur():
    # Onceki baglanti kapatilmadan Windows dosyayi silmiyor, ve silinmemis bir
    # db "table copy already exists" ile patliyor.
    while _acik:
        _acik.pop().close()
    shutil.rmtree(KOK, ignore_errors=True)
    yedek = os.path.join(KOK, "A56 yedek")
    os.makedirs(os.path.join(yedek, "Kamera"))
    db = os.path.join(KOK, "test.db")

    c = sqlite3.connect(db)
    _acik.append(c)
    c.execute("""CREATE TABLE copy (
        copy_id INTEGER PRIMARY KEY AUTOINCREMENT, device_key TEXT, source_path TEXT,
        source_name TEXT, source_size INTEGER, source_modified TEXT, object_id TEXT,
        destination TEXT, status TEXT, started_utc TEXT, finished_utc TEXT,
        bytes_copied INTEGER, sha256 TEXT, error TEXT)""")

    def dosya(ad, icerik, durum="done", hash_bozuk=False, yazma=False):
        hedef = os.path.join(yedek, "Kamera", ad)
        if not yazma:
            with open(hedef, "wb") as f:
                f.write(icerik)
        h = sha(hedef) if not yazma else hashlib.sha256(icerik).hexdigest().upper()
        if hash_bozuk:
            h = "0" * 64
        c.execute("""INSERT INTO copy (device_key, source_path, source_name, source_size,
                     destination, status, started_utc, bytes_copied, sha256, error)
                     VALUES ('SER1', ?, ?, ?, ?, ?, '2026-09-23', ?, ?, ?)""",
                  ("/P/DCIM/Camera/" + ad, ad, len(icerik), hedef, durum,
                   len(icerik), h, "0x80070141" if durum == "failed" else None))
        return hedef

    return yedek, db, c, dosya


def calistir(yedek, db, *ek):
    r = subprocess.run([sys.executable, BETIK, yedek, "--veri", db, *ek],
                       capture_output=True, text=True, encoding="utf-8")
    return r.returncode, (r.stdout or "") + (r.stderr or "")


print("=" * 64)
print("1. TEMIZ YEDEK  ->  cikis 0, 'SAGLAM'")
yedek, db, c, dosya = kur()
for i in range(5):
    dosya(f"IMG_{i}.jpg", b"x" * (1000 + i))
c.commit()
kod, cikti = calistir(yedek, db, "--hepsi")
print(f"   cikis={kod}  saglam_gecti={'SAĞLAM' in cikti}")
assert kod == 0 and "SAĞLAM" in cikti, cikti

print("\n2. ICERIK BOZUK (hash tutmuyor, boyut DOGRU)  ->  cikis 1")
yedek, db, c, dosya = kur()
dosya("iyi.jpg", b"x" * 1000)
p = dosya("bozuk.jpg", b"y" * 1000)
c.commit()
# Ayni uzunlukta farkli icerik: sadece hash yakalayabilir, boyut yakalayamaz.
with open(p, "wb") as f:
    f.write(b"z" * 1000)
kod, cikti = calistir(yedek, db, "--hepsi")
yakaladi = "HASH UYUŞMAYAN" in cikti and "bozuk.jpg" in cikti
print(f"   cikis={kod}  hash_yakaladi={yakaladi}  boyut_sessiz={'boyutu tutmayan: 0' in cikti}")
assert kod == 1 and yakaladi, cikti

print("\n3. DOSYA SILINMIS  ->  cikis 1, 'Diskte olmayan'")
yedek, db, c, dosya = kur()
dosya("duran.jpg", b"x" * 1000)
p = dosya("silinen.jpg", b"x" * 1000)
c.commit()
os.remove(p)
kod, cikti = calistir(yedek, db, "--hepsi")
print(f"   cikis={kod}  yakaladi={'Diskte olmayan' in cikti and 'silinen.jpg' in cikti}")
assert kod == 1 and "silinen.jpg" in cikti, cikti

print("\n4. KIRPILMIS (boyut yanlis)  ->  cikis 1")
yedek, db, c, dosya = kur()
p = dosya("kirpik.jpg", b"x" * 1000)
c.commit()
with open(p, "wb") as f:
    f.write(b"x" * 400)
kod, cikti = calistir(yedek, db, "--hepsi")
print(f"   cikis={kod}  yakaladi={'Boyutu tutmayanlar' in cikti}")
assert kod == 1 and "Boyutu tutmayanlar" in cikti, cikti

print("\n5. AKTARILAMAMIS + YARIM SATIR  ->  cikis 1, ikisi de raporlanir")
yedek, db, c, dosya = kur()
dosya("iyi.jpg", b"x" * 1000)
dosya("olmadi.jpg", b"x" * 1000, durum="failed", yazma=True)
dosya("yarim.jpg", b"x" * 1000, durum="copying", yazma=True)
c.commit()
kod, cikti = calistir(yedek, db, "--hepsi")
ok = "hâlâ telefonda" in cikti and "olmadi.jpg" in cikti and "Yarıda kalmış" in cikti
print(f"   cikis={kod}  yakaladi={ok}")
assert kod == 1 and ok, cikti

print("\n6. ARTIK .part DOSYASI  ->  cikis 1")
yedek, db, c, dosya = kur()
dosya("iyi.jpg", b"x" * 1000)
c.commit()
with open(os.path.join(yedek, "Kamera", "yarim.jpg.part"), "wb") as f:
    f.write(b"yarim")
kod, cikti = calistir(yedek, db, "--hepsi")
print(f"   cikis={kod}  yakaladi={'.part' in cikti and 'Yarım .part' in cikti}")
assert kod == 1 and "Yarım .part" in cikti, cikti

print("\n7. YARIM SATIRI SONRADAN BITEN DOSYA  ->  temiz sayilmali")
yedek, db, c, dosya = kur()
# Ayni source_path icin once 'copying' sonra 'done': sert kill sonrasi devam.
p = os.path.join(yedek, "Kamera", "a.jpg")
with open(p, "wb") as f:
    f.write(b"x" * 1000)
for durum in ("copying", "done"):
    c.execute("""INSERT INTO copy (device_key, source_path, source_name, source_size,
                 destination, status, started_utc, bytes_copied, sha256)
                 VALUES ('SER1','/P/DCIM/Camera/a.jpg','a.jpg',1000,?,?,'2026-09-23',1000,?)""",
              (p, durum, sha(p)))
c.commit()
kod, cikti = calistir(yedek, db, "--hepsi")
print(f"   cikis={kod}  saglam={'SAĞLAM' in cikti}  defter_1_dosya={'Defter  : 1 dosya' in cikti}")
assert kod == 0 and "SAĞLAM" in cikti, cikti

print("\n8. BASKA DISKE YAPILMIS ESKI YEDEGIN SATIRLARI karismamali")
yedek, db, c, dosya = kur()
dosya("bizim.jpg", b"x" * 1000)
c.execute("""INSERT INTO copy (device_key, source_path, source_name, source_size,
             destination, status, started_utc, bytes_copied, sha256)
             VALUES ('SER1','/P/DCIM/Camera/eski.jpg','eski.jpg',1000,
                     'Z:\\baska yedek\\Kamera\\eski.jpg','done','2026-01-01',1000,'AA')""")
c.commit()
kod, cikti = calistir(yedek, db, "--hepsi")
print(f"   cikis={kod}  saglam={'SAĞLAM' in cikti}  sadece_1={'Defter  : 1 dosya' in cikti}")
assert kod == 0 and "Defter  : 1 dosya" in cikti, cikti

shutil.rmtree(KOK, ignore_errors=True)
print("\n" + "=" * 64)
print("8/8 senaryo gecti.")
