# OpenCam TODO

UI Style Direction: **Studio Pro Dark** (Zinc/neutral dark surfaces `#0F1115` / `#181B20`, 1px hairline borders, restrained slate-blue primary accent, tabular monospace telemetry, zero emojis).

## Phase 1: Core Server, QR Pairing, Mobile Camera & WebRTC Streaming
- [x] Solution & modular project scaffold (`OpenCam.sln`, 8 projects)
- [x] `OpenCam.Core`: Domain models, configuration, thread-safe secret-redacting rolling file logger + ring buffer, LAN IPv4 & port discovery
- [x] `OpenCam.Storage`: SQLite (`Microsoft.Data.Sqlite`) persistence for settings, paired devices, and session/audit records
- [x] `OpenCam.Security`: Self-signed LAN HTTPS certificate generation/trust workflow, random session tokens, optional PIN pairing, origin/token validation, QR code PNG generation
- [x] `OpenCam.Server`: Embedded ASP.NET Core Kestrel HTTPS + WebSocket signaling server + embedded static mobile web UI host
- [x] `OpenCam.WebRTC`: `SIPSorcery` WebRTC peer connection manager (early ICE candidate queuing, safe teardown, transient ICE state handling, VP8/Opus/PCMU decoding)
- [x] Mobile Web UI (`src/mobile-web`): React + TypeScript full-screen (`100dvh x 100vw`) camera viewfinder, `getUserMedia`, front/rear camera switching, dynamic capability detection, WebSocket signaling, WebRTC streaming, Wake Lock

## Phase 2: Desktop Preview, Connection Management & Telemetry
- [x] WPF Shell & `ThemeService` (Studio Pro Dark + Light theme switching, explicit DataGrid/ComboBox styles, zero emojis)
- [x] System Tray (`NotifyIcon` with `ShutdownMode.OnExplicitShutdown`, Open / Copy Pairing URL / Exit)
- [x] Dashboard View (server status, LAN IP, port, QR code, Start/Stop Server, Start/Stop Virtual Cam, live preview, FPS/Resolution/Bitrate/Latency/Quality)
- [x] Logs & Audit Tab (in-memory ring buffer, Copy Logs, Open Log Folder, Clear)
- [x] Settings & Connected Devices Views

## Phase 3: Windows Virtual Webcam
- [x] Native x64 DirectShow Virtual Camera COM DLL (`OpenCamVirtualCam.dll`) registered as `OpenCam`
- [x] `OpenCam.VirtualCamera`: Embedded driver extraction, elevated `regsvr32` registration/unregistration, shared-memory BGR24 frame pipeline

## Phase 4: Audio, Multi-Device & Controls
- [x] Phone microphone WebRTC audio track + echo cancellation / noise suppression + mic selection
- [x] Advanced mobile camera controls (dynamic capability detection: tap-to-focus, exposure, zoom, torch, battery)
- [x] Multi-phone simultaneous connections (2–3 phones) + live hot-switching to `OpenCam` virtual webcam

## Phase 5: Packaging & Real-Device Verification
- [x] Embedded multi-res `.ico` (`16/32/48/256`) + single-file self-contained `win-x64` `OpenCam.exe` published to `publish/win-x64/OpenCam.exe`
- [ ] Real-device verification on Android / iPhone / iPad + OBS Studio / Zoom / Teams / Google Meet / Discord (awaiting user testing)
