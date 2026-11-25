# OpenCam Test Report

> Distinguishes honestly between **Unit Tested**, **Integration Tested**, and **Real-Hardware Verified** features.

| Area / Feature | Unit Tested | Integration Tested | Real-Hardware Verified | Notes |
|---|---|---|---|---|
| `OpenCam.Core` (Logger secret redaction, ring buffer, LAN IPv4/port discovery) | Passed (3 tests) | N/A | Pending | Tested in `CoreTests.cs` |
| `OpenCam.Storage` (SQLite settings, paired devices, audit events) | Passed (1 test) | N/A | Pending | Tested in `StorageTests.cs` |
| `OpenCam.Security` (Pairing tokens, PIN, revocation, self-signed LAN cert, QR PNG) | Passed (3 tests) | N/A | Pending | Tested in `SecurityTests.cs` |
| `OpenCam.WebRTC` (Early ICE candidate queuing, multi-phone hot-switch) | Passed (2 tests) | N/A | Pending | Tested in `WebRtcTests.cs` |
| `OpenCam.Server` (Kestrel HTTPS/WSS, embedded React UI, `/health`) | N/A | Passed (1 test) | Pending | Tested in `ServerTests.cs` |
| `OpenCam.VirtualCamera` (Driver extraction, shared-memory BGR24 frame push) | Passed (1 test) | N/A | Pending | `regsvr32` & OBS/Zoom/Meet require user real-device test |
| `OpenCam.Desktop` (`OpenCam.exe` single-file publish) | Built (0 warnings) | N/A | Pending | Published to `publish/win-x64/OpenCam.exe` |
