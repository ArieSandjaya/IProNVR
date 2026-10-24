# NVR Knowledge Base

Satu file JSON per merek. Dipakai sebagai kamus endpoint/CGI sekaligus sumber untuk driver.

Status tiap entri (`status`):
- `unverified`  : ditulis dari dokumentasi/pengetahuan umum, BELUM diuji ke perangkat.
- `verified`    : sudah diuji ke perangkat nyata (isi `testedOn` dengan model + firmware).
- `quirk`       : perilaku tak terdokumentasi yang ditemukan saat uji.

Cara memverifikasi: jalankan request di `example`, simpan respons asli ke `Knowledge/samples/<merek>/<nama>.txt`,
lalu ubah `status` dan isi `testedOn`.

Kapabilitas (`capabilities`) memetakan fitur aplikasi ke endpoint: live, playback, search, download, ptz, hdd, snapshot, events, time.
