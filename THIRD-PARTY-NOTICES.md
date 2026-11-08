# Third-party components

XCloudSSD is released under the MIT License (see `LICENSE`). It uses or redistributes the following components.

| Component | Use | License |
|---|---|---|
| **WinFsp** — Windows File System Proxy (Bill Zissimopoulos) | Kernel filesystem driver + user-mode API that makes the virtual disks real Windows volumes. The installer and the portable zip ship the unmodified official `winfsp-*.msi`. | GPLv3 with the WinFsp FLOSS exception (allows use by free/open-source software under licenses such as MIT). See https://github.com/winfsp/winfsp/blob/master/License.txt |
| **winfsp.net** (NuGet, `winfsp-msil.dll`) | .NET binding to the WinFsp API | Same as WinFsp |
| **.NET 10 runtime** (Microsoft) | Self-contained runtime inside `XCloudSSD.exe` and `XCloudSSDService.exe` | MIT |
| **WPF / Windows Forms** (Microsoft) | Desktop UI, tray icon | MIT |
| **Microsoft.Extensions.Hosting / Logging / WindowsServices** | Windows service host | MIT |
| **System.Security.Cryptography.ProtectedData** | Windows DPAPI token encryption | MIT |
| **xUnit.net**, **Microsoft.NET.Test.Sdk** | Tests only (not shipped) | Apache-2.0 / MIT |
| **Inno Setup** | Builds `XCloudSSD-Setup.exe` (build tool, not shipped as a library) | Inno Setup License |

## Services

* **Google Drive API v3** and **Google OAuth 2.0** are used under the Google APIs Terms of Service. Each user supplies their own OAuth client from their own Google Cloud project.
* **TeraBox**: no TeraBox code or endpoints are included (see `docs/LIMITATIONS.md`). "TeraBox" and "Google Drive" are trademarks of their owners; XCloudSSD is not affiliated with either.
