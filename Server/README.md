# IPRO NVR Server

Server konfigurasi terpisah dari aplikasi client (`IproNvrTool`). Menyimpan registrasi NVR, kamera, user, group, dan sequence.
Video **tidak** lewat server; client tetap tersambung langsung ke NVR.

## Build
Buka `Server/IproNvrServer/IproNvrServer.csproj` di Visual Studio (net48) lalu Build, atau `dotnet build`.

## Jalankan
- Konsol: `IproNvrServer.exe` (Ctrl+C berhenti)
- Service Windows (jalankan sebagai Administrator): `IproNvrServer.exe install` (juga membuka firewall dan urlacl), `uninstall` untuk menghapus
- Pertama kali jalan dibuat user `admin` dengan password acak (tampil di konsol/log dan `data\admin-awal.txt`). Ganti: `IproNvrServer.exe setpass admin PASSWORDBARU`
- Pengaturan: `server.json` di samping exe (`Port` 8470, `DataDir`, `TokenDays` 30)
- Halaman admin web: `http://SERVER:8470/`

## API (JSON, `Authorization: Bearer <token>`)
| Endpoint | Fungsi |
|---|---|
| `POST /api/login` `{name,pass}` | token + data user (percobaan gagal 5x = kunci 1 menit per IP) |
| `GET /api/ping` | tanpa login, cek server hidup + nomor versi |
| `GET /api/version` | nomor versi konfigurasi (naik tiap ada perubahan), untuk polling client |
| `GET /api/config` | seluruh konfigurasi: nvrs, cameras, groups, sequences (+ users bila admin) |
| `GET/POST /api/{nvrs,cameras,groups,sequences,users}` | daftar / simpan (POST tanpa `id` mendaftar baru; NVR host+port, kamera nvr_id+channel, nama group sama otomatis di-update) |
| `DELETE /api/{tabel}/{id}` | hapus (NVR ikut menghapus kameranya) |

Hanya admin yang boleh menulis. Format `slots` group: `nvr:ch,nvr:ch,,` (kosong = area kosong). `steps` sequence: `groupId:detik,...`.
Password NVR/kamera disimpan terenkripsi AES-256 (kunci `data\server.key`, **ikut di-backup**), dikirim ke client dalam bentuk asli — pakai HTTPS (reverse proxy/IIS ARR atau sertifikat `netsh http add sslcert`) bila jaringan tidak terpercaya.
