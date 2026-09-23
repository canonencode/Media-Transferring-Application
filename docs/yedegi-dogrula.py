"""Yedeği, onu üreten programa hiç güvenmeden doğrular.

Neden ayrı bir betik: kopyalayan program kendi yazdığını kendi hash'iyle
karşılaştırıyor. Bu, hesaplamanın doğru olduğunu gösterir ama aynı koddaki bir
hatayı gösteremez - yanlış hash'i yazıp aynı yanlış hash'le karşılaştırmak da
"tuttu" der. Burası deftere dışarıdan bakıyor: satırda yazan SHA-256'yı alıyor,
diskteki dosyayı kendi bağımsız kodu ile yeniden hash'liyor ve ikisini
karşılaştırıyor. Farklı dil, farklı kütüphane, farklı süreç.

Telefonu elden çıkarmadan önce çalıştırılacak şey budur.

Kullanım:
    python docs/yedegi-dogrula.py "D:/A56 yedek"
    python docs/yedegi-dogrula.py "D:/A56 yedek" --hepsi     (örnekleme yok)
"""

import argparse
import hashlib
import os
import random
import sqlite3
import sys

# Windows konsolu cp1252'ye düşüp Türkçe karakterde patlıyor. Bu dosyanın işi
# kötü haberi bildirmek; kendisi kodlama yüzünden çökerse hiçbir işe yaramaz.
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

VERI = os.path.expandvars(r"%LOCALAPPDATA%\MediaTransfer\scans.db")


def hash_of(path, chunk=1 << 20):
    """Parça parça okunuyor: bunların arasında gigabaytlık videolar var."""
    sha = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(chunk), b""):
            sha.update(block)
    return sha.hexdigest().upper()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("hedef", help="Yedek klasörü, örn. D:/A56 yedek")
    ap.add_argument("--hepsi", action="store_true",
                    help="Her dosyayı hash'le (yavaş ama tam)")
    ap.add_argument("--ornek", type=int, default=300,
                    help="--hepsi yokken kaç dosya hash'lensin (varsayılan 300)")
    ap.add_argument("--veri", default=VERI)
    args = ap.parse_args()

    kok = os.path.abspath(args.hedef)

    if not os.path.isdir(kok):
        print(f"HATA: {kok} diye bir klasör yok.")
        return 2
    if not os.path.exists(args.veri):
        print(f"HATA: defter bulunamadı: {args.veri}")
        return 2

    db = sqlite3.connect("file:" + args.veri.replace("\\", "/") + "?mode=ro", uri=True)

    # Sadece bu hedefe yazılmış satırlar. Defter cihazın tüm geçmişini tutuyor
    # ve başka bir diske yapılmış eski bir yedeğin satırlarını buraya karıştırmak,
    # var olmayan dosyaları "kayıp" diye raporlamak olurdu.
    satirlar = db.execute(
        """SELECT source_path, source_name, source_size, destination, status,
                  bytes_copied, sha256
           FROM copy WHERE destination LIKE ? ORDER BY copy_id""",
        (kok + "%",)).fetchall()

    if not satirlar:
        print(f"Bu klasöre ait kayıt yok: {kok}")
        print("Aktarım başka bir hedefe mi yapıldı?")
        return 2

    # Dosya başına en son satır. Yarıda kalan bir koşunun bıraktığı eski satır,
    # aynı dosyayı sonradan bitiren satırın yerine geçmemeli.
    son = {}
    for r in satirlar:
        son[r[0]] = r

    bitmis = [r for r in son.values() if r[4] == "done"]
    basarisiz = [r for r in son.values() if r[4] == "failed"]
    yarim = [r for r in son.values() if r[4] == "copying"]

    print(f"Defter  : {len(son)} dosya  ({len(bitmis)} bitti, "
          f"{len(basarisiz)} başarısız, {len(yarim)} yarım)")
    print(f"Hedef   : {kok}\n")

    # ---- 1. Var mı, boyutu doğru mu (hepsi, hızlı) ----------------------
    yok, yanlis_boyut = [], []
    for _, ad, kaynak_boyut, hedef, _, yazilan, _ in bitmis:
        if not os.path.exists(hedef):
            yok.append((ad, hedef))
            continue
        diskte = os.path.getsize(hedef)
        # İki karşılaştırma: diskteki boyut deftere göre doğru mu, VE defter
        # telefondaki boyutla uyuşuyor mu. İkincisi olmadan, eksik yazılmış bir
        # dosyanın eksik boyutunu deftere kaydedip "tuttu" demek mümkün olurdu.
        if diskte != yazilan or (kaynak_boyut and yazilan != kaynak_boyut):
            yanlis_boyut.append((ad, kaynak_boyut, yazilan, diskte))

    print(f"[1/3] Varlık ve boyut: {len(bitmis)} dosya denetlendi")
    print(f"      eksik: {len(yok)}   boyutu tutmayan: {len(yanlis_boyut)}")

    # ---- 2. Hash (örneklem ya da hepsi) --------------------------------
    hashli = [r for r in bitmis if r[6] and os.path.exists(r[3])]
    if args.hepsi:
        secilen = hashli
    else:
        secilen = random.Random(1).sample(hashli, min(args.ornek, len(hashli)))

    toplam_bayt = sum(os.path.getsize(r[3]) for r in secilen)
    print(f"\n[2/3] Hash: {len(secilen)} dosya, {toplam_bayt / 1024**3:.1f} GB okunuyor...")

    uyusmayan, okunamayan = [], []
    for i, (_, ad, _, hedef, _, _, beklenen) in enumerate(secilen, 1):
        try:
            if hash_of(hedef) != beklenen:
                uyusmayan.append((ad, hedef))
        except OSError as e:
            okunamayan.append((ad, str(e)))
        if i % 100 == 0:
            print(f"      {i}/{len(secilen)}")

    print(f"      uyuşmayan: {len(uyusmayan)}   okunamayan: {len(okunamayan)}")

    # ---- 3. Diskte olup defterde olmayan --------------------------------
    # Ters yön. Defterden diske bakmak "yazdıklarım duruyor mu" sorusunu
    # cevaplıyor; diskten deftere bakmak "burada hesabını veremediğim bir şey
    # var mı" sorusunu. İkisi aynı soru değil.
    diskteki = set()
    for dizin, _, dosyalar in os.walk(kok):
        for d in dosyalar:
            diskteki.add(os.path.join(dizin, d))
    kayitli = {r[3] for r in son.values()}
    fazlalik = diskteki - kayitli
    yarim_kalan = [p for p in fazlalik if p.endswith(".part")]

    print(f"\n[3/3] Diskte {len(diskteki)} dosya var, "
          f"{len(fazlalik)} tanesi defterde yok ({len(yarim_kalan)} adet .part)")

    # ---- Karar -----------------------------------------------------------
    print("\n" + "=" * 62)
    temiz = not (yok or yanlis_boyut or uyusmayan or okunamayan
                 or basarisiz or yarim or yarim_kalan)

    if temiz:
        kapsam = "tamamı" if args.hepsi else f"{len(secilen)} dosyalık örneklem"
        print(f"YEDEK SAĞLAM. {len(bitmis)} dosya yerinde ve doğru boyutta; "
              f"hash denetimi ({kapsam}) hatasız.")
        if not args.hepsi:
            print("Telefonu elden çıkarmadan önce --hepsi ile tam denetim öneririm.")
    else:
        print("YEDEK EKSİK VEYA BOZUK. Telefonu henüz elden çıkarma.\n")
        for baslik, liste in [
            ("Diskte olmayan dosyalar", [f"{a}  ->  {h}" for a, h in yok]),
            ("Boyutu tutmayanlar",
             [f"{a}  telefon={k} defter={y} disk={d}" for a, k, y, d in yanlis_boyut]),
            ("HASH UYUŞMAYAN (içerik bozuk)", [f"{a}  ->  {h}" for a, h in uyusmayan]),
            ("Okunamayan", [f"{a}: {e}" for a, e in okunamayan]),
            ("Aktarılamamış (hâlâ telefonda)", [f"{r[1]}" for r in basarisiz]),
            ("Yarıda kalmış", [f"{r[1]}" for r in yarim]),
            ("Yarım .part dosyaları", yarim_kalan),
        ]:
            if liste:
                print(f"{baslik} ({len(liste)}):")
                for satir in liste[:20]:
                    print("   " + satir)
                if len(liste) > 20:
                    print(f"   ... ve {len(liste) - 20} tane daha")
                print()
        print("Aynı --copy komutunu tekrar çalıştır: biten dosyalar "
              "ikinci kez kopyalanmaz, sadece eksikler alınır.")

    return 0 if temiz else 1


if __name__ == "__main__":
    sys.exit(main())
