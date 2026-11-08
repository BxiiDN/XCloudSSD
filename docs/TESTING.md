# Hasil pengujian

Semua angka di bawah berasal dari run CI nyata, bukan perkiraan.

* Run: https://github.com/BxiiDN/XCloudSSD/actions/runs/42481550509 (commit `3af9d3a`, 8 Nov 2026) — **sukses**
* Mesin: GitHub Actions `windows-2025-vs2026` (Windows Server 2025 x64), .NET SDK 10.0.403
* WinFsp: `winfsp-2.1.25156.msi` (rilis resmi), dipasang lewat `msiexec /qn`

| Tahap | Hasil |
|---|---|
| Build Release (12 proyek + 3 proyek test) | ✅ |
| Unit test | ✅ 25/25 |
| Integration test dengan **mount WinFsp sungguhan** | ✅ 10/10 |
| Service: dipasang sebagai Windows service nyata (`sc create/start`), diuji lewat named pipe | ✅ 1/1 |
| UI self-test: kedelapan halaman dibuka dan dirender ke PNG tanpa error | ✅ 8/8 |
| `XCloudSSD-Setup.exe` dibangun (Inno Setup 6.7.1) | ✅ 103,3 MB |
| Install senyap → service **Running, Automatic** → uninstall senyap → service terhapus | ✅ |
| `XCloudSSD-Portable-x64.zip` | ✅ 104,7 MB |

Output: `XCloudSSD.exe` (75,7 MB) dan `XCloudSSDService.exe` (38,6 MB), self-contained single-file x64.
Unduh dari artefak **XCloudSSD-windows-x64** pada run di atas.

## Integration test (WinFsp nyata, cloud = test double in-memory)

Filesystem produksi (`CloudFileSystem`) dipasang sebagai drive letter sungguhan dan diuji lewat `System.IO`
(jalur I/O Windows yang sama dengan Explorer/Steam). Sisi cloud memakai `InMemoryCloudProvider` (khusus test,
tidak ikut dikirim) yang meniru aturan Google Drive: objek utuh, resumable upload berurutan kelipatan 256 KiB,
kuota, change feed, offline, dan kegagalan sementara.

| Test | Yang dibuktikan |
|---|---|
| `Volume_ReportsCloudCapacity_NotLocalDisk` | Drive siap, label "XCloudSSD Google 01", filesystem "NTFS", total 1 TiB dan kosong 1 TiB − 200 GiB **persis dari kuota cloud**, bukan dari C: |
| `Files_And_Folders_FullLifecycle` | buat folder bertingkat, tulis/baca, append, rename/move, enumerasi, atribut, hapus file & folder — setiap langkah dicek di sisi cloud |
| `LargeSequentialCopy_StreamsToCloud_WithBoundedRam` | file 256 MiB ditulis lewat budget RAM 48 MiB; MD5 di cloud cocok; puncak RAM ≤ 48 MiB; baca balik cocok |
| `RandomAccess_ReadAndWrite_InExistingFile` | 40 random read terverifikasi + 20 random write → isi cloud persis sama |
| `SteamLikeLayout_PreallocateThenSequential_Works` | struktur `SteamLibrary\steamapps\common\NARUTO SHIPPUDEN Ultimate Ninja STORM 4`, `appmanifest_349040.acf`, 30 file kecil, file 96 MiB dengan `SetLength` lalu tulis berurutan, RAM ≤ 48 MiB |
| `LargeOutOfOrderWrite_FailsClearly_InsteadOfUsingDisk` | tulis mundur 64 MiB dengan RAM 16 MiB → `IOException` yang jelas, tidak ada fallback ke disk, file tidak di-commit |
| `FileSharing_And_ByteRangeLocks_AreEnforced` | `FileShare.None` dan `LockFile` dihormati Windows |
| `Offline_KeepsDriveAndUnsavedData_ThenCommitsOnReconnect` | saat offline drive tetap ada, listing tetap tampil, file yang ditulis disimpan di RAM; setelah online di-commit otomatis |
| `MultipleAccounts_AreIsolated_EachWithOwnLetterAndQuota` | dua akun = dua drive letter, data tidak tercampur, kapasitas masing-masing (1 TiB vs 15 GiB) |
| `Remount_KeepsData_BecauseItLivesInTheCloud` | unmount → drive hilang; mount ulang → data masih ada |

## Unit test (25)

* **Transfer**: streaming file 160 MiB lewat RAM 24 MiB dengan verifikasi MD5; merge random write dengan konten
  cloud; truncate/extend → nol; write di bawah data yang sudah ter-stream ditolak; budget penuh → error tanpa
  disk; gagal sementara → data tetap untuk retry; offline → online commit; file kosong baru tetap dibuat;
  chunk cache lintas batas, hemat request, menghormati budget, mengembalikan RAM saat dibutuhkan.
* **Protokol Google Drive** (handler HTTP terskrip — memeriksa request yang *kita* kirim, bukan server Google):
  kuota dari `about.storageQuota`; query `'id' in parents and trashed = false`; header `Range`; protokol
  resumable upload termasuk 308 + `Range` parsial (sisa byte dikirim ulang); 401 → refresh token lalu ulang;
  `storageQuotaExceeded` → disk penuh.
* **Core**: PKCE S256 + URL otorisasi; `SecretStore` (token tidak tersimpan plaintext, blob akun A tidak bisa
  dibuka sebagai akun B); store akun/pengaturan/jurnal (huruf drive unik, transfer yang terputus ditandai
  *Interrupted*); `MetadataTree` + change feed; `NameCodec`; pembatas bandwidth.

## Belum diuji (butuh PC dan akun milikmu)

Hal-hal ini **tidak** bisa dijalankan di CI, jadi tidak diklaim berhasil:

* **Akun Google Drive sungguhan**: login OAuth di browser, kuota akun asli, kecepatan & batas laju Google.
  Protokolnya diuji terhadap handler terskrip, bukan server Google.
* **TeraBox**: tidak ada integrasi (lihat LIMITATIONS.md).
* **Steam client sungguhan** dan instalasi Naruto Shippuden Ultimate Ninja Storm 4: belum diuji. Pola tulis
  downloader Steam bisa melebihi buffer RAM (lihat LIMITATIONS.md). Jalankan *Game Storage → Run storage check*
  lalu coba satu game.
* Menjalankan game/EXE dari drive (memory-mapped image loading) dan anti-cheat.
* Tampilan di Explorer *This PC* secara visual (drive diverifikasi lewat API `DriveInfo`, bukan screenshot Explorer).
* **Restart Windows sungguhan**: yang diuji adalah service ber-start type *Automatic* + restore konfigurasi;
  reboot fisik belum.
* Pemutusan internet fisik dan token yang benar-benar kedaluwarsa (disimulasikan di test).
* Windows 10 (CI memakai Windows Server 2025, basis yang sama dengan Windows 11 24H2).

## Cara mengulang di PC sendiri

```powershell
# Butuh .NET 10 SDK dan WinFsp 2.1
dotnet test tests/XCloudSSD.Tests/XCloudSSD.Tests.csproj -c Release
dotnet test tests/XCloudSSD.IntegrationTests/XCloudSSD.IntegrationTests.csproj -c Release --filter "Category!=Service"
```
