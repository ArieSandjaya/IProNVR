MediaMTX v1.21.0 (windows amd64) - https://github.com/bluenviron/mediamtx  (lisensi MIT, lihat LICENSE)
Dipakai IproNvrServer sebagai relay RTSP. Server membuat konfigurasinya sendiri di data\mediamtx\ dan mendaftarkan kamera lewat API (password kamera tidak ditulis ke file).
Mengganti versi: timpa mediamtx.exe (sebaiknya versi 1.21.x; versi lain bisa menolak kunci konfigurasi). Atau isi "MediaMtxPath" di server.json.
Matikan relay: server.json -> "Relay": false.
