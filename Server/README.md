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

## Deteksi RTSP otomatis
Di halaman admin: tab **NVR** tombol *Deteksi RTSP* (semua kamera NVR yang punya IP) atau tab **Kamera** tombol *Deteksi* (satu kamera). Syarat:
- `ffmpeg.exe` ada di folder `IproNvrServer.exe`, di PATH, atau isi `FfmpegPath` di `server.json`
- server harus bisa menjangkau IP kamera (port RTSP 554 dan ONVIF)
- isi `cam_user`/`cam_pass` dan `rtsp_template` di data NVR, serta `ip_address` kamera
- kamera yang user/password-nya berbeda dari NVR: isi `cam_user`/`cam_pass` di data kamera itu (login khusus menggantikan login NVR)
Hasil: `rtsp_url` (templat `{user}{pass}{ip}`), `codec`, `width`, `height`, `scan_detail`. Kodenya sama dengan di aplikasi client (file dipakai bersama).

## API (JSON, `Authorization: Bearer <token>`)
| Endpoint | Fungsi |
|---|---|
| `POST /api/login` `{name,pass}` | token + data user (percobaan gagal 5x = kunci 1 menit per IP) |
| `GET /api/ping` | tanpa login, cek server hidup + nomor versi |
| `GET /api/version` | nomor versi konfigurasi (naik tiap ada perubahan), untuk polling client |
| `GET /api/config` | seluruh konfigurasi: nvrs, cameras, groups, sequences (+ users bila admin) |
| `GET/POST /api/{nvrs,cameras,groups,sequences,users}` | daftar / simpan (POST tanpa `id` mendaftar baru; NVR host+port, kamera nvr_id+channel, nama group sama otomatis di-update) |
| `DELETE /api/{tabel}/{id}` | hapus (NVR ikut menghapus kameranya) |

| `POST /api/discover` `{nvr_id, camera_ids?, overwrite?}` | deteksi URL RTSP otomatis di server (ONVIF, templat NVR, pola merk; tiap kandidat diuji ffmpeg), hasil disimpan ke kamera. Mengembalikan `job` |
| `GET /api/jobs/{id}` / `DELETE /api/jobs/{id}` | progres + log deteksi / batalkan |

Hanya admin yang boleh menulis. Format `slots` group: `nvr:ch,nvr:ch,,` (kosong = area kosong). `steps` sequence: `groupId:detik,...`.
Password NVR/kamera disimpan terenkripsi AES-256 (kunci `data\server.key`, **ikut di-backup**), dikirim ke client dalam bentuk asli — pakai HTTPS (reverse proxy/IIS ARR atau sertifikat `netsh http add sslcert`) bila jaringan tidak terpercaya.
