MediaMTX v1.21.0 (windows amd64) - https://github.com/bluenviron/mediamtx  (lisensi MIT, lihat LICENSE)
Dipakai IproNvrServer sebagai relay RTSP. Server membuat konfigurasinya sendiri di data\mediamtx\ dan mendaftarkan kamera lewat API (password kamera tidak ditulis ke file).
Mengganti versi: timpa mediamtx.exe (sebaiknya versi 1.21.x; versi lain bisa menolak kunci konfigurasi). Atau isi "MediaMtxPath" di server.json.
Matikan relay: server.json -> "Relay": false.

MediaMTX di mesin lain (server tidak menjalankannya sendiri) - server.json:
  "MediaMtxApi": "http://192.168.1.20:9997",   API MediaMTX di mesin itu
  "MediaMtxApiUser"/"MediaMtxApiPass": isi bila API-nya memakai autentikasi
  "RelayPort": 8554,                            port RTSP di mesin itu
  "RelayHost": "192.168.1.20",                  host yang dipakai client (kosong = host server)
  "RelayUser"/"RelayPass": login baca-saja yang SAMA dengan authInternalUsers di mediamtx.yml mesin itu (action: read)
Di mesin MediaMTX: api: yes dan apiAddress dapat dijangkau server; user API (action: api) untuk IP server.
