# Arsitektur XCloudSSD

```
Game / Steam / Explorer
        │  Win32 file I/O
        ▼
Windows I/O manager ──► WinFsp kernel driver (winfsp-x64.sys)
        │                         │ IRPs forwarded to user mode
        ▼                         ▼
                XCloudSSDService.exe  (LocalSystem, auto start, recovery = restart)
 ┌─────────────────────────────────────────────────────────────────────────┐
 │ DiskManager ── one VirtualDisk per account (own letter, provider,      │
 │                credentials, metadata tree, read buffer, status)        │
 │   VirtualDisk ─► CloudFileSystem (Fsp.FileSystemBase)                  │
 │        ├─ MetadataTree   path → cloud id (lazy listing + change feed)   │
 │        ├─ ChunkReadCache range reads → RAM chunks (LRU, idle expiry)    │
 │        ├─ WriteSession   RAM pages + ordered resumable upload, MD5 check│
 │        └─ TransferTracker journal (JSON store) for the Transfer Manager │
 │   MemoryBudget (one global RAM cap)  BandwidthThrottle (up/down)        │
 │   ControlPipeServer  ◄── named pipe ──►  XCloudSSD.exe (WPF UI)         │
 └─────────────────────────────────────────────────────────────────────────┘
        │ HTTPS
        ▼
Google Drive API v3   (TeraBox: not available, see LIMITATIONS.md)
```

## Proyek

| Proyek | Isi |
|---|---|
| `XCloudSSD.Core` | Kontrak provider (`ICloudProvider`, `IUploadSession`), model, `MemoryBudget`, `Metrics`, `BandwidthThrottle`, kontrak IPC + `PipeClient`, logger file |
| `XCloudSSD.Security` | `SecretStore` — DPAPI (LocalMachine untuk service), satu file + entropy per akun, ACL folder SYSTEM/Administrators |
| `XCloudSSD.Authentication` | OAuth 2.0 Google: PKCE + loopback (`127.0.0.1:port`), token exchange/refresh/revoke, refresh otomatis |
| `XCloudSSD.Metadata` | `AppDatabase` (file JSON atomik: akun, pengaturan, jurnal transfer), `MetadataTree`, `NameCodec` |
| `XCloudSSD.Cloud` | Retry/backoff, klasifikasi error jaringan |
| `XCloudSSD.GoogleDrive` | REST Drive v3: list, range GET, resumable upload (308/Range resume), about/quota, changes, trash/delete, appProperties untuk atribut Windows |
| `XCloudSSD.TeraBox` | Adapter jujur: melaporkan "tidak tersedia" (lihat LIMITATIONS) |
| `XCloudSSD.Transfer` | `ChunkReadCache`, `WriteSession`, `TransferTracker` |
| `XCloudSSD.Filesystem` | `CloudFileSystem` — implementasi WinFsp lengkap |
| `XCloudSSD.VirtualDrive` | `VirtualDisk` (mount/unmount, offline, reconnect, polling kuota & change feed), `DiskManager` |
| `XCloudSSD.Service` | Windows service `XCloudSSDService` + server named pipe |
| `XCloudSSD.Desktop` | Aplikasi WPF (tema Fluent gelap), 8 halaman, bahasa id/en/kustom, tray |

## Aturan data (zero local game cache)

* Isi file **tidak pernah** ditulis ke disk fisik. Baca = HTTP range request ke buffer RAM; tulis = halaman RAM
  yang di-stream ke resumable upload.
* Satu `MemoryBudget` global membatasi seluruh RAM I/O (default 512 MB). Chunk baca dilepas LRU saat butuh ruang
  dan setelah 60 detik tidak dipakai. Jika budget penuh dan tidak ada yang bisa dibebaskan, operasi tulis
  **gagal dengan error** (`STATUS_INSUFFICIENT_RESOURCES`), tidak pernah diam-diam pindah ke disk.
* Yang disimpan lokal: database kecil berbentuk file JSON (akun, pengaturan, jurnal transfer — tanpa isi file), token
  terenkripsi DPAPI, dan log operasional (`%ProgramData%\XCloudSSD`).
* Kapasitas volume = kuota provider (`about.storageQuota`), bukan ruang kosong C:.

## Alur tulis (commit)

1. File baru = placeholder RAM. File yang ditulis berurutan tanpa konten dasar di-stream ke upload session
   setelah ≥ 64 MB terkumpul; halaman yang sudah terkirim dilepas dari RAM.
2. Penulisan acak di file yang sudah ada disimpan sebagai halaman 64 KB; saat commit, seluruh file diunggah ulang
   dengan menggabungkan rentang asli dari cloud + halaman RAM.
3. Commit terjadi saat handle terakhir ditutup (Cleanup) atau `FlushFileBuffers`. Close **menunggu** sampai upload
   selesai dan ukuran + MD5 dari cloud cocok. Baru setelah itu transfer berstatus *Completed*.
4. Gagal sementara (offline/timeout/429): data tetap di RAM, dicoba ulang otomatis tiap 15 detik (status
   *Offline/Retrying* di Transfer Manager). Gagal permanen: *Failed* + log.
5. Jika ada penulisan yang gagal pada sebuah file, isi yang tidak lengkap itu tidak pernah diunggah.

## Pemulihan

* Service: `sc failure` restart 5/10/30 detik; setelah crash, jurnal menandai upload yang terputus sebagai
  *Interrupted* (datanya hanya ada di RAM, jadi perlu disalin ulang — ini dilaporkan, bukan disembunyikan).
* Offline saat start: drive tetap dipasang memakai root id terakhir; listing dari metadata, isi file yang belum
  ada di RAM → `STATUS_NETWORK_UNREACHABLE`. Loop reconnect tiap 30 detik.
* Token: refresh otomatis sebelum kedaluwarsa dan saat 401; `invalid_grant` → status *Sign-in required*.
