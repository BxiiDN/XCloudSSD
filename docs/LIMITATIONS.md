# Keterbatasan yang diketahui (jujur)

## TeraBox — eksperimental, lewat antarmuka web (tidak resmi)
TeraBox tidak menyediakan API publik untuk aplikasi (Open Platform hanya untuk partner). Atas permintaan pengguna,
XCloudSSD sekarang menambahkan akun TeraBox dengan cara ini:
* Login dilakukan **di dalam aplikasi** pada halaman resmi terabox.com (WebView2). XCloudSSD tidak melihat password.
  Setelah login, cookie sesi (`ndus`) disimpan terenkripsi DPAPI oleh service dan hanya dikirim ke terabox.com.
  Profil browser sementara dihapus setelah login.
* Operasi file memakai antarmuka web yang sama dengan situs TeraBox, mengikuti driver open-source AList
  (`/api/list`, `/api/quota`, `/api/filemanager`, `/api/create`, `/api/precreate`, `superfile2`, `/api/download`).
* **Belum diuji dengan akun TeraBox sungguhan.** Protokolnya diuji terhadap server terskrip di CI. TeraBox bisa
  mengubah antarmuka ini kapan saja, dan akses otomatis mungkin melanggar ketentuan TeraBox — gunakan dengan akun
  sendiri atas risiko sendiri.
* Batasan: tidak ada change feed (folder diperbarui berkala), atribut Windows tidak tersimpan di TeraBox; ukuran
  ruang "reserved" (sparse) hanya dicatat di PC ini (`sparse-<akun>.json`, metadata saja — bukan isi file), hapus = masuk recycle bin TeraBox, pencarian hanya pada folder yang sudah dibuka,
  akun gratis membatasi satu file maks. 4 GB, server upload dipilih sesuai domain login (terabox.com, 1024tera.com, …).

## Google Drive tidak bisa menulis sebagian file
Drive menyimpan file sebagai objek utuh. Konsekuensi tanpa cache disk:
* **Menulis file baru secara berurutan** (copy di Explorer, sebagian besar installer): di-stream, RAM tetap terbatas,
  ukuran file tidak dibatasi RAM.
* **Mengubah sebagian file yang sudah ada** (save game, config): bekerja, tetapi seluruh file diunggah ulang saat
  ditutup. Wajar untuk file kecil–menengah, lambat untuk file besar.
* **Ruang yang dipesan (sparse)**: file yang dibuat dengan ukuran akhir tetapi belum diisi (Steam "Reserving space",
  `SetLength`, atau ditulis nol) tidak diunggah. Ukurannya dicatat di metadata Google Drive (`appProperties`) dan
  bagian kosongnya dibaca sebagai nol. Saat diisi berurutan setelahnya, data asli di-stream ke cloud.
* **Menulis tidak berurutan secara lokal** (Steam/Epic menulis blok-blok yang sedikit acak dan tidak sejajar):
  sejak v1.2.0 bagian yang sudah lengkap di-stream begitu celahnya terisi, dan jika buffer penuh penulisan
  **menunggu** (maks. 120 detik) sampai upload membebaskan RAM, bukan langsung gagal. Akibatnya kecepatan install
  = kecepatan upload internet Anda.
* **Menulis sangat acak di file besar** (memory-mapped write, patch acak di seluruh file): data tertunda tetap
  harus muat di buffer RAM. Jika tidak muat dalam waktu tunggu, penulisan **gagal dengan error yang jelas** dan
  file tidak di-commit. Inilah harga aturan "tanpa cache disk"; XCloudSSD tidak melanggarnya diam-diam.
  Buffer RAM default sekarang ¼ RAM fisik (1024–4096 MB); untuk install game disarankan 2048–4096 MB.
* Menulis ulang bagian awal file yang sedang di-stream (setelah data terkirim) tidak didukung
  (`STATUS_NOT_SUPPORTED`).
* Google Drive membatasi upload ±750 GB per akun per hari.

## Steam dan game
* Steam bisa memakai drive ini sebagai library (drive dilaporkan sebagai disk tetap ber-filesystem "NTFS").
  **Instalasi game besar lewat Steam belum diuji pada akun cloud sungguhan** (CI menguji pola tulisnya terhadap
  provider di memori dan mount WinFsp nyata). Gunakan **Game Storage → Run storage check**, lalu uji satu game sebelum memindahkan library.
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
