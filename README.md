# i-PRO NVR Tool - tampilan bergaya ASM300

Aplikasi WinForms (.NET Framework 4.8) untuk NVR i-PRO lewat CGI. Mulai versi ini tampilan utamanya meniru
**i-PRO ASM300 v7.80**: buka aplikasi, pilih kamera/group, gambar langsung tampil.
Tampilan lama (tab Koneksi/Live/Playback/Info/Download) tetap ada: jalankan `IproNvrTool.exe --classic`
atau menu ≡ > "Tampilan klasik".

## Struktur jendela
- **Bar judul**: ikon Live / Map / Search / Setting, tab jendela (klik kanan atau seret tab ke bawah = pisah jadi jendela), menu ≡.
- **Operation window** (tab utama)
  - Function panel kiri: Device (pohon folder/NVR, seret kamera ke area), Group, Sequence; bawah: Quick playback, Download, Alarm log, Device error log, Network error log; kolom cari.
  - Toolbar: pola layar (1, 2A/B, 3A-C, 4, 4A-E, 5, 6, 6A, 7, 7A, 9, 10, 13, 16, 25, 36, 49, 64), 5 ikon pintasan, layar penuh, still picture, print, kosongkan.
  - Area gambar: seret kamera dari pohon, tukar kamera antar area dengan seret, klik ganda = perbesar, klik kanan = Still picture / Print / Quick download / No image display / Register layout to group.
  - Status bar: waktu, SEQ, langkah FF/REW, LIVE/PLAY, ERR, ALM.
  - Operation panel: tab **Camera** (PTZ pad, zoom, focus, brightness/iris, click-to-center, preset 1-256, AUX1-8) dan tab **HDD** (playback: mundur/maju/pause/frame/FF/REW/stop, skip waktu, Go ke waktu, kembali ke Live, Quick DL).
- **Live window** (hingga 4): area gambar live + Group / Sequence / Device.
- **Map window**: daftar map, ikon kamera (warna status), ikon pindah-map, menu klik kanan (Live, Playback, AUX1-8).
- **Search window**: Thumbnail search (gambar diam per interval, klik ganda = putar) dan Recording info (HDD).
- **Setup window**: System, Device setting (daftar NVR & kamera, koneksi/ffmpeg/GPU), User mng. (user, level, hak akses), Folder, Group, Sequence, Default screen, Map (editor), Event, Maintenance.
- Notifikasi popup + buzzer saat alarm/error, auto logout, login opsional (aktifkan di Setup > System setelah membuat admin).

## Catatan teknis
- Satu UID NVR maksimum 16 stream; aplikasi otomatis membagi kamera ke beberapa UID (Pool). Live dan playback memakai UID terpisah.
- Multi-area memakai substream bila tersedia (urutan: substream NVR, RTSP substream kamera, stream utama); area diperbesar memakai stream utama.
- Download httpdl.cgi dipecah per 60 menit. Zona waktu STARTTIME/ENDTIME diatur di Setup > System.
- Data (folder, group, sequence, map, user) disimpan di SQLite yang sama dengan Daftar NVR.
- Perbaikan bug dari versi sebelumnya: `Db.Init()` punya variabel `c` di luar scope (error kompilasi); sudah dirapikan.

## Export / import database
Setup > Maintenance > **Export database** / **Import database** (berkas `.iprodb`). Isi: NVR, kamera, folder, group, sequence, map, user & hak akses, pengaturan fisheye.
Password NVR/kamera di database terikat user Windows (DPAPI), jadi saat export dipilih: dienkripsi dengan passphrase (AES-256) atau dikosongkan; saat import dienkripsi ulang untuk PC/user ini.
Import mengganti seluruh data; database lama dicadangkan otomatis (`nvr_backup_*.db` di folder data) dan aplikasi dimulai ulang. Pengaturan aplikasi (ukuran tampilan dll.) tidak ikut.

## Kamera fisheye
Klik kanan area gambar > **Fisheye**:
- **Cara 1 (dewarp di aplikasi, bawaan):** pilih Panorama, Quad (4 sudut), atau PTZ virtual (geser dengan mouse, roda = zoom). Berlaku untuk live dan playback. Area fisheye otomatis memakai stream utama (lebar dekode min. 1280) supaya hasil tajam.
- **Kalibrasi lensa...:** posisi pasang (langit-langit/dinding/meja), sudut lensa, pusat dan radius lingkaran (lingkaran kuning di gambar mentah harus pas dengan tepi lensa), putaran; pratinjau hasil langsung.
- **Cara 2 (dewarp di kamera):** di dialog yang sama pilih "pakai stream dewarp dari kamera (RTSP)" dan isi URL RTSP stream panorama/quad kamera (atur dulu di web kamera). Aplikasi tidak melakukan dewarp lagi; bila RTSP gagal, jatuh ke stream utama NVR.
Pengaturan tersimpan per kamera dan dipakai di semua jendela.

## Tidak tersedia (CGI NVR tidak menyediakan)
Pencarian event REC/VMD/wajah, heat map, decoder, system controller, kamera AI, registrasi lisensi, crop,
timeline, SD card, audio/mic, auto mode, preset HOME, EL-zoom x1. Varian pola layar A-E dan bentuk lain adalah perkiraan
dari manual. Playback H.264/H.265 (GOP -> ffmpeg) dan seluruh alur belum diuji pada NVR sungguhan; diperiksa lewat kompilasi
dan uji konstruksi UI saja. ffmpeg.exe diperlukan untuk live/playback H.264/H.265.

## Operasi 24x7

- **Auto-reconnect kamera**: stream live yang putus/gagal disambung ulang otomatis (jeda 4→60 dtk), login ulang bila gagal 3x berturut-turut; watchdog memutus stream yang diam >30 dtk; timeout baca 25 dtk.
- **Sesi NVR**: keep-alive gagal 3x → UID dibuang dan login ulang otomatis pada perintah berikutnya.
- **Supervisor** (Setup > System > Operasi 24x7): proses induk menjalankan aplikasi sebagai proses anak dan menghidupkannya lagi bila crash, hang UI >2 menit, melewati batas memori, atau restart harian. Keluar normal (tombol Exit) tidak di-restart.
- Satu instance saja, penangkap exception global (ditulis ke log), cegah PC tidur, auto-start saat login Windows, buzzer diam otomatis setelah 5 menit.
- Untuk PC wall: aktifkan auto-login Windows + "Jalankan otomatis saat login Windows", dan set Operation/Live start group di Setup > Default screen agar tampilan kembali sendiri setelah restart.
