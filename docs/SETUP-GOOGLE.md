# Menyiapkan login Google (OAuth client) — sekali saja

Google mewajibkan setiap aplikasi yang mengakses Google Drive memakai **OAuth client** miliknya sendiri.
XCloudSSD tidak membawa client bawaan, jadi kamu membuatnya sekali di proyek Google Cloud milikmu (gratis).

1. Buka https://console.cloud.google.com/ dan buat proyek baru (mis. `XCloudSSD`).
2. **APIs & Services → Library** → cari **Google Drive API** → **Enable**.
3. **APIs & Services → OAuth consent screen** (atau *Google Auth Platform*):
   * User type: **External**. Isi nama aplikasi (mis. `XCloudSSD`) dan e-mail.
   * Scopes: tambahkan `https://www.googleapis.com/auth/drive`.
   * **Test users**: tambahkan setiap akun Google yang ingin kamu pasang sebagai drive.
     Selama aplikasi berstatus *Testing*, hanya akun di daftar ini yang bisa masuk.
4. **APIs & Services → Credentials → Create credentials → OAuth client ID**:
   * Application type: **Desktop app**. Nama bebas.
   * Klik **Download JSON**.
5. Di XCloudSSD buka **Settings → Google sign-in (OAuth client)** → **Import client JSON** → pilih file tadi.
   Secret disimpan terenkripsi (DPAPI) oleh service dan tidak ditampilkan lagi.
6. Buka **Cloud Accounts → Add Google Drive account**. Browser membuka halaman login resmi Google;
   pilih akun, izinkan akses, lalu kembali ke XCloudSSD. Drive baru langsung terpasang.

Catatan:
* Untuk aplikasi berstatus *Testing*, Google bisa meminta login ulang secara berkala (refresh token
  aplikasi Testing dapat kedaluwarsa). Disk akan berstatus **Sign-in required**; hapus lalu tambahkan
  ulang akun, atau publikasikan aplikasi di consent screen.
* XCloudSSD tidak pernah melihat password Google. Yang disimpan hanyalah refresh token, terenkripsi per akun.
