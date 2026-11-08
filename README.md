# XCloudSSD

**Cloud Virtual SSD untuk Windows 10/11 x64.** Setiap akun cloud dipasang sebagai drive lokal sendiri
(`D:`, `E:`, …) yang muncul di *This PC → Devices and drives*. Isi file berada langsung di cloud; secara lokal
hanya dipakai buffer RAM yang dibatasi — **tidak ada cache game/file di C: atau SSD fisik lain**.

> Status: v1.2.0. Google Drive lewat API resmi. TeraBox **eksperimental**: login di dalam aplikasi pada halaman
> resmi TeraBox, lalu memakai antarmuka web TeraBox (tidak resmi, belum diuji dengan akun sungguhan) — lihat
> [docs/LIMITATIONS.md](docs/LIMITATIONS.md).
> Hasil pengujian nyata ada di [docs/TESTING.md](docs/TESTING.md).

## Fitur

* **Disk virtual sungguhan** (WinFsp): create/read/write/delete/rename/move, folder, enumerasi, atribut,
  resize/truncate, flush, sharing mode dan byte-range lock dari Windows, akses acak & berurutan.
* **Multi-akun terisolasi**: satu akun = satu huruf drive, kredensial terenkripsi sendiri, namespace, kuota,
  metadata, status koneksi, dan antrean transfer sendiri. Akun Google yang sama tidak bisa dipasang dua kali.
* **Kapasitas mengikuti kuota cloud** (mis. 1 TB total / 200 GB terpakai → drive 1 TB, 800 GB kosong).
* **Streaming**: baca = HTTP range request ke RAM (read-ahead untuk akses berurutan); tulis = resumable upload
  berpotongan, diverifikasi ukuran + MD5 sebelum dianggap selesai.
* **Permanent mount**: `XCloudSSDService` (start otomatis, restart otomatis saat crash) memasang ulang semua drive
  dengan huruf yang sama setelah restart, tetap terpasang saat offline, dan menyambung ulang otomatis.
* **Aplikasi desktop** (WPF, Fluent dark): Dashboard, My Virtual Disks, Cloud Accounts, File Manager,
  Game Storage (deteksi Steam library + uji penyimpanan nyata), Transfer Manager, Performance Monitor
  (nilai terukur), Settings. Bahasa Indonesia, English, dan bahasa kustom (file JSON).
* **Keamanan**: OAuth 2.0 + PKCE di browser (password tidak pernah dilihat), token DPAPI per akun,
  pipe kontrol lokal saja.

## Instalasi

1. Unduh `XCloudSSD-Setup.exe` dari artefak build GitHub Actions (atau Release).
2. Jalankan sebagai administrator. Installer memasang aplikasi, **driver WinFsp** (jika belum ada),
   mendaftarkan dan menjalankan **XCloudSSDService**, serta shortcut.
3. Siapkan login Google sekali: [docs/SETUP-GOOGLE.md](docs/SETUP-GOOGLE.md) (± 5 menit di Google Cloud Console).
4. Buka XCloudSSD → **Cloud Accounts → Add Google Drive account**.

Portable: ekstrak `XCloudSSD-Portable-x64.zip`, jalankan `install-service.cmd` sebagai administrator,
lalu `XCloudSSD.exe`.

## Build dari source

Kebutuhan: Windows 10/11 x64, .NET 10 SDK, WinFsp 2.x, Inno Setup 6 (untuk installer).

```powershell
dotnet build XCloudSSD.slnx -c Release
dotnet test tests/XCloudSSD.Tests/XCloudSSD.Tests.csproj -c Release
dotnet test tests/XCloudSSD.IntegrationTests/XCloudSSD.IntegrationTests.csproj -c Release   # butuh WinFsp
dotnet publish src/XCloudSSD.Desktop -c Release -r win-x64 --self-contained -o out/app
dotnet publish src/XCloudSSD.Service -c Release -r win-x64 --self-contained -o out/app
```

Pipeline lengkap (build → unit test → integration test dengan mount WinFsp nyata → uji service → installer →
uji install/uninstall → zip portable) ada di [.github/workflows/build.yml](.github/workflows/build.yml).

## Dokumentasi

* [Arsitektur](docs/ARCHITECTURE.md)
* [Keterbatasan](docs/LIMITATIONS.md) — baca sebelum memindahkan library game
* [Hasil pengujian](docs/TESTING.md)
* [Setup login Google](docs/SETUP-GOOGLE.md)
* [Komponen pihak ketiga & lisensi](THIRD-PARTY-NOTICES.md)

## Lisensi

MIT. WinFsp didistribusikan tanpa modifikasi di bawah lisensinya sendiri (GPLv3 + FLOSS exception).
