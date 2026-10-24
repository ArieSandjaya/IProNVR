# IPRO NVR Server

Server konfigurasi terpisah dari aplikasi client (`IproNvrTool`). Menyimpan registrasi NVR, kamera, user, group, dan sequence.
Video **tidak** lewat server; client tetap tersambung langsung ke NVR.

## Client (IproNvrTool) mode server
Di aplikasi client: **Setup > System > Server konfigurasi** - centang *Pakai server*, isi alamat server (mis. `http://192.168.1.5:8470`) dan akun sinkron (cukup operator), lalu *Tes koneksi dan sinkron sekarang*.
- NVR, kamera, URL RTSP, group, sequence disalin ke database lokal (cache) saat aplikasi dibuka dan saat memilih menu **Periksa pembaruan dari server** (tidak ada pengecekan otomatis berkala). Client tetap jalan dengan data terakhir bila server mati.
- Pertama kali, database lokal dicadangkan otomatis (`nvr.db.sebelum-server-...`) lalu NVR/kamera/group lokal diganti data server.
- Tab registrasi NVR/kamera menjadi hanya-lihat (diatur lewat halaman admin server). Group, sequence, dan user yang disimpan dari aplikasi dikirim ke server (butuh akun admin server).
- Login aplikasi memakai akun server; bila server mati, boleh masuk dengan akun yang pernah login di PC itu.
- Tetap lokal per PC: fisheye, map, folder device, hak per level, tampilan terakhir.

## Build
Buka `Server/IproNvrServer/IproNvrServer.csproj` di Visual Studio (net48) lalu Build, atau `dotnet build`.

## Jalankan
- Konsol: `IproNvrServer.exe` (Ctrl+C berhenti)
- Service Windows (jalankan sebagai Administrator): `IproNvrServer.exe install` (juga membuka firewall dan urlacl), `uninstall` untuk menghapus
- Pertama kali jalan dibuat user `admin` dengan password acak (tampil di konsol/log dan `data\admin-awal.txt`). Ganti: `IproNvrServer.exe setpass admin PASSWORDBARU`
- Pengaturan: `server.json` di samping exe (`Port` 8470, `DataDir`, `TokenDays` 30)
- Pendaftaran NVR: setelah NVR baru disimpan, server otomatis tes login dan membaca kamera yang terdaftar di NVR (kanal, status normal/error, jumlah kanal). Ulangi dengan tombol "Deteksi kamera".
- Menu Sequence disembunyikan di halaman admin (data dan API tetap ada).
- Isi IP kamera dari Excel: tab Kamera, tombol "Template Excel" (berisi NVR/kanal yang ada) lalu isi kolom IP Address dan "Upload Excel (IP)". Kolom: Nama NVR, Channel, IP Address, Nama Kamera, Username Kamera, Password Kamera (.xlsx atau .csv; sel kosong = tidak diubah; password tidak ikut diekspor ke template). Ada pratinjau sebelum disimpan; kamera yang belum ada dibuat.
- Group: menu Group memakai editor visual seperti aplikasi client (pilih pola layar, klik/seret kamera ke area).
- Halaman admin web: `http://SERVER:8470/`

## Merek NVR
Kolom **Merek** pada data NVR (halaman admin): i-PRO (default), Hikvision, atau Dahua. Server memakai logika yang sama dengan aplikasi client (`BrandCgi.cs` dipakai bersama) untuk tes login dan membaca daftar kamera ("Deteksi kamera"); merek ikut disinkron ke client. Kamera memiliki URL RTSP substream dan stream utama (`rtsp_main_url`, diisi tombol Deteksi RTSP). Endpoint Hikvision/Dahua belum diuji ke perangkat nyata (lihat `Knowledge/`).

## Deteksi RTSP otomatis
Di halaman admin: tab **NVR** tombol *Deteksi RTSP* (semua kamera NVR yang punya IP) atau tab **Kamera** tombol *Deteksi* (satu kamera). Syarat:
- `ffmpeg.exe` ada di folder `IproNvrServer.exe`, di PATH, atau isi `FfmpegPath` di `server.json`
- server harus bisa menjangkau IP kamera (port RTSP 554 dan ONVIF)
- isi `cam_user`/`cam_pass` dan `rtsp_template` di data NVR, serta `ip_address` kamera
- kamera yang user/password-nya berbeda dari NVR: isi `cam_user`/`cam_pass` di data kamera itu (login khusus menggantikan login NVR)
Hasil: `rtsp_url` (templat `{user}{pass}{ip}`), `codec`, `width`, `height`, `scan_detail`. Kodenya sama dengan di aplikasi client (file dipakai bersama).

## Pratinjau live kamera
Di form **Edit/Tambah kamera** tekan **▶ Live**: server membuka RTSP kamera dengan ffmpeg dan mengirimnya ke browser sebagai MJPEG (±8 fps, lebar 640, tanpa suara). Memakai nilai yang sedang terisi di form (IP, login khusus, URL RTSP), jadi bisa dicoba sebelum disimpan. Maksimal 4 pratinjau bersamaan, otomatis berhenti setelah 10 menit atau saat ditutup. Pesan error (login salah, kamera tidak terjangkau, dst.) tampil di kotak pratinjau.

## API (JSON, `Authorization: Bearer <token>`)
| Endpoint | Fungsi |
|---|---|
| `POST /api/login` `{name,pass}` | token + data user (percobaan gagal 5x = kunci 1 menit per IP) |
| `GET /api/ping` | tanpa login, cek server hidup + nomor versi |
| `GET /api/version` | nomor versi konfigurasi (naik tiap ada perubahan), untuk polling client |
| `GET /api/config` | seluruh konfigurasi: nvrs, cameras, groups, sequences (+ users bila admin) |
| `GET/POST /api/{nvrs,cameras,groups,sequences,users}` | daftar / simpan (POST tanpa `id` mendaftar baru; NVR host+port, kamera nvr_id+channel, nama group sama otomatis di-update) |
| `DELETE /api/{tabel}/{id}` | hapus (NVR ikut menghapus kameranya) |

| `POST /api/preview` lalu `GET /api/stream?k=KEY&t=TOKEN` | pratinjau live MJPEG (admin) |
| `POST /api/discover` `{nvr_id, camera_ids?, overwrite?}` | deteksi URL RTSP otomatis di server (ONVIF, templat NVR, pola merk; tiap kandidat diuji ffmpeg), hasil disimpan ke kamera. Mengembalikan `job` |
| `GET /api/jobs/{id}` / `DELETE /api/jobs/{id}` | progres + log deteksi / batalkan |

Hanya admin yang boleh menulis. Format `slots` group: `nvr:ch,nvr:ch,,` (kosong = area kosong). `steps` sequence: `groupId:detik,...`.
Password NVR/kamera disimpan terenkripsi AES-256 (kunci `data\server.key`, **ikut di-backup**), dikirim ke client dalam bentuk asli — pakai HTTPS (reverse proxy/IIS ARR atau sertifikat `netsh http add sslcert`) bila jaringan tidak terpercaya.
