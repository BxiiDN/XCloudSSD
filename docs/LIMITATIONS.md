# Keterbatasan yang diketahui (jujur)

## TeraBox — belum bisa dipasang
TeraBox menyediakan API resmi bernama **TeraBox Open Platform**, tetapi aksesnya hanya untuk partner atas
permintaan (formulir kontak / e-mail ke TeraBox) dan dokumentasi endpoint-nya tidak dapat dibaca publik
(https://www.terabox.com/integrations/docs). Proyek lain biasanya memakai API web privat dengan cookie `ndus`
dari browser — ini dilarang oleh spesifikasi XCloudSSD (tidak boleh endpoint fiktif / ambil cookie diam-diam).

Maka: tombol **Add TeraBox account** menjelaskan hal ini dan membuka situs resmi TeraBox / halaman Open Platform.
`XCloudSSD.TeraBox` melaporkan kemampuan `CanMount = false`. Begitu kredensial Open Platform dan dokumentasinya
tersedia, adapter cukup mengimplementasikan `ICloudProvider` — struktur multi-akun, drive per akun, dan UI
sudah siap.

## Google Drive tidak bisa menulis sebagian file
Drive menyimpan file sebagai objek utuh. Konsekuensi tanpa cache disk:
* **Menulis file baru secara berurutan** (copy di Explorer, sebagian besar installer): di-stream, RAM tetap terbatas,
  ukuran file tidak dibatasi RAM.
* **Mengubah sebagian file yang sudah ada** (save game, config): bekerja, tetapi seluruh file diunggah ulang saat
  ditutup. Wajar untuk file kecil–menengah, lambat untuk file besar.
* **Menulis file besar secara tidak berurutan** (mis. downloader Steam yang menulis chunk acak, memory-mapped write):
  semua data tertunda harus muat di buffer RAM. Jika tidak muat, penulisan **gagal dengan error yang jelas** dan
  file tidak di-commit. Inilah harga dari aturan "tanpa cache disk"; XCloudSSD tidak melanggarnya diam-diam.
* Menulis ulang bagian awal file yang sedang di-stream (setelah > 64 MB terkirim) tidak didukung
  (`STATUS_NOT_SUPPORTED`).

## Steam dan game
* Steam bisa memakai drive ini sebagai library (drive dilaporkan sebagai disk tetap ber-filesystem "NTFS").
  **Instalasi game besar lewat Steam belum diuji pada akun Google sungguhan** dan mungkin gagal karena pola tulis
  acak di atas. Gunakan **Game Storage → Run storage check**, lalu uji satu game sebelum memindahkan library.
* Kecepatan loading game = kecepatan jaringan + batas laju Google (range request ~ ratusan ms latensi).
  Game dengan banyak akses acak kecil akan terasa lambat; ini bukan SSD lokal.
* Aplikasi Xbox (folder `WindowsApps`) butuh fitur NTFS yang tidak ada di filesystem virtual — tidak didukung.
* Anti-cheat tertentu menolak executable dari filesystem non-NTFS asli; tidak dapat diuji di sini.

## Daya tahan data
* Data yang belum di-commit hanya ada di RAM. Jika PC mati mendadak atau service crash sebelum close selesai,
  data itu hilang; jurnal transfer menandainya *Interrupted* supaya terlihat.
* Saat Windows shutdown, SCM memberi waktu terbatas pada service; upload yang sangat besar bisa terpotong.

## Lainnya
* ACL file tidak disimpan di cloud; semua file memakai descriptor tetap (SYSTEM, Administrators, Authenticated Users).
* Google Docs/Sheets/Slides (tanpa konten biner) disembunyikan. Nama dengan karakter terlarang Windows
  ditampilkan dengan karakter full-width (mis. `：`).
* Nama file duplikat dalam satu folder Drive: hanya yang terbaru yang terlihat.
* Hapus default = pindah ke **Trash** Drive (masih memakai kuota). Ubah ke *permanen* di Settings bila perlu.
* Untuk OAuth app berstatus *Testing*, Google dapat meminta login ulang secara berkala.
* Drive yang dipasang oleh service (LocalSystem) terlihat global untuk semua pengguna Windows di PC tersebut.
