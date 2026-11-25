Build OpenCam – Universal Wireless Webcam System

Act as a senior full-stack engineer, Windows systems developer, networking engineer, and UI/UX designer.

Build a production-quality application named OpenCam that turns Android phones, iPhones, iPads, and other camera-enabled devices into wireless webcams for Windows PCs over the same local Wi-Fi network.

The goal is to create a powerful, easy-to-use alternative to Iriun Webcam, Camo, and DroidCam, with a clean interface and maximum practical webcam functionality.

1. Core Architecture

Use a LAN-first architecture.

Windows PC:

A standalone Windows desktop application packaged as a single installer or executable.
Runs a local HTTP/WebSocket server.
Automatically detects the PC's local IPv4 address.
Automatically selects an available port.
Displays a QR code for quick device pairing.
Hosts a mobile-friendly web interface.
Manages connected devices and camera streams.
Provides a virtual webcam output for Windows applications.

Mobile devices:

Initially connect through a browser without installing an APK or iOS app.
Open the PC's local IP address and port.
Request camera permission using browser APIs.
Display available front and rear cameras.
Stream live camera video to the Windows server.
Support Android, iPhone, and iPad browsers.

Use WebRTC for low-latency video streaming, WebSocket for signaling and device communication, and HTTPS or a secure local development certificate where required by browser camera permissions.

Important: Mobile browsers generally require a secure context for camera access. Implement a practical LAN HTTPS certificate and trust/pairing workflow. Do not assume HTTP access to a local IP will always permit camera access.

2. Windows Desktop Application

Create a beautiful, modern desktop dashboard.

Main dashboard:

OpenCam connection status.
PC local IP address.
Server port.
QR pairing code.
Connected device list.
Live camera preview.
Start/stop server button.
Start/stop webcam button.
Connection quality indicator.
FPS indicator.
Resolution indicator.
Bitrate indicator.
Network latency indicator.

Settings:

Automatically start server with Windows.
Automatically reconnect devices.
Select preferred network adapter.
Configure server port.
Configure connection security.
Choose video quality.
Select virtual camera output.
Enable hardware acceleration where supported.
3. Mobile Web Camera Interface

Create a responsive, mobile-first camera interface.

Features:

Front/rear camera switching.
Camera selection when multiple cameras exist.
Resolution selection.
480p, 720p, 1080p, and higher resolutions where supported.
24, 30, and 60 FPS where supported.
Autofocus where supported.
Tap-to-focus where supported.
Exposure compensation where supported.
Zoom controls where supported.
Torch/flash control where supported.
Camera orientation control.
Landscape and portrait support.
Fullscreen preview.
Microphone selection where supported.
Audio streaming toggle.
Battery level display where available.
Network signal and streaming quality indicator.
Reconnect automatically after connection loss.

Never display unsupported features as working controls. Detect hardware and browser capabilities dynamically.

4. Video Streaming Engine

Implement:

WebRTC-based real-time video transport.
Adaptive bitrate.
Adaptive resolution.
Dynamic FPS adjustment.
Network congestion handling.
Packet loss monitoring.
Automatic reconnection.
Connection quality monitoring.
Low-latency streaming.
Hardware-accelerated encoding when available.
Configurable video codec where supported.
Multiple device connections with independent streams.

Prioritize stable video and low latency over unnecessarily high resolution.

5. Windows Virtual Webcam

This is a critical feature.

The phone camera must eventually appear as a selectable camera device in:

OBS Studio.
Zoom.
Microsoft Teams.
Google Meet.
Discord.
Other Windows webcam applications.

Investigate the most reliable Windows virtual camera implementation.

Use a supported Windows virtual camera API or a maintained virtual camera driver integration.

If a driver or additional installation is required, clearly explain this and package the installation experience cleanly.

Do not pretend that opening a browser video preview automatically creates a Windows webcam device.

The application should support:

Virtual camera start/stop.
Camera name: OpenCam.
Video resolution configuration.
Frame rate configuration.
Multiple aspect ratios.
Stable frame delivery.
Reconnection without restarting the desktop application.
6. Audio Features

Implement optional microphone streaming:

Use the phone microphone.
Select available audio input devices.
Enable/disable microphone transmission.
Monitor audio levels.
Support echo cancellation where available.
Support noise suppression where available.
Keep audio and video synchronized.

Clearly distinguish phone microphone input from the PC microphone.

7. Privacy and Security

The system should work primarily on the local network.

Implement:

Pairing using QR code.
Random session authentication token.
Optional PIN pairing.
Encrypted connections.
No public internet exposure by default.
No automatic port forwarding.
No unnecessary cloud dependency.
Clear camera and microphone permission indicators.
Disconnect and revoke device access.
Session expiration.
Protection against unauthorized LAN connections.

Never stream camera or microphone content without explicit user permission.

8. UI/UX Design

Design direction:

Premium, minimal, modern desktop application.
Inspired by professional camera software.
Dark theme by default.
Clean typography.
Rounded panels.
Subtle animations.
Simple navigation.
No clutter.
No unnecessary menus.

Navigation:

Dashboard
Camera
Connected Devices
Virtual Webcam
Settings
About

Mobile interface should be extremely simple:

Open connection link.
Grant permissions.
Select camera.
Tap Connect.
Start streaming.
9. Recommended Technology

Choose technologies that make the final product stable and maintainable.

Suggested architecture:

Windows desktop: C#/.NET with WPF or WinUI 3, or Tauri if justified.
Backend: ASP.NET Core or a suitable lightweight local server.
Streaming: WebRTC.
Signaling: WebSocket.
Mobile web interface: React + TypeScript.
Camera access: WebRTC getUserMedia and MediaDevices APIs.
QR pairing: QR code generation library.
Windows virtual camera: Supported Windows virtual camera API or maintained driver integration.

Avoid unnecessary dependencies.

10. Build and Packaging

Deliver:

Windows x64 executable.
Installer.
Local web camera interface.
Automatic LAN discovery.
QR pairing.
Virtual webcam integration.
Configuration persistence.
Application logs.
Error handling.
Startup and shutdown management.

The application should not require users to manually install Node.js, Python, or development dependencies.

Package all required runtime dependencies appropriately.

11. Testing

Test the following scenarios:

Windows PC and Android phone on the same Wi-Fi.
Windows PC and iPhone on the same Wi-Fi.
Windows PC and iPad on the same Wi-Fi.
QR code pairing.
Manual IP address pairing.
Switching between front and rear cameras.
Network disconnection and reconnection.
PC application restart.
Multiple connected mobile devices.
OBS virtual camera integration.
Zoom and Google Meet camera selection.
Microphone streaming.
Different resolutions and frame rates.
Windows firewall permissions.
Browser camera permissions and HTTPS certificate handling.
12. Development Approach

Do not build everything as a fake prototype.

Work in phases:

Phase 1: Build the Windows server, QR pairing, mobile browser camera access, and real-time WebRTC streaming.

Phase 2: Build the desktop preview, connection management, camera controls, and performance monitoring.

Phase 3: Implement the Windows virtual webcam output and test it with OBS.

Phase 4: Add microphone streaming, advanced camera controls, adaptive quality, and multi-device support.

Phase 5: Polish the UI, improve security, test Android/iOS compatibility, and package the final Windows application.

At the end of each phase:

Run the application.
Test actual functionality.
Fix errors.
Document completed features.
Clearly identify limitations.

Do not mark a feature complete unless it has been implemented and tested.

Start by designing the architecture and implementing Phase 1. Prioritize a working end-to-end connection before adding advanced features.


---

## 13. Engineering & Packaging Rules (Learned from MyRemote)

### Technology & Packaging Baseline
- **Target Stack:** .NET 9 (`net9.0-windows`) + WPF + C# + SQLite (`Microsoft.Data.Sqlite`) + xUnit (`OpenCam.Tests`).
- **Modular Solution Structure:** Separate into `OpenCam.Core`, `OpenCam.Storage`, `OpenCam.Security`, `OpenCam.Server` (embedded ASP.NET Core Kestrel HTTPS + WebSocket + static mobile web UI), `OpenCam.WebRTC` (`SIPSorcery`), `OpenCam.VirtualCamera`, `OpenCam.Desktop` (WPF), and `OpenCam.Tests`.
- **Single-File Standalone Executable:** Publish as a self-contained single-file `win-x64` executable (`OpenCam.exe`, `PublishSingleFile=true`, `IncludeNativeLibrariesForSelfExtract=true`, `IncludeAllContentForSelfExtract=true`, `EnableCompressionInSingleFile=true`) with an embedded multi-resolution `.ico` application icon (`16/32/48/256`). End users must not need Node.js, Python, or a .NET runtime installed; bundle the built mobile web UI as embedded resources inside the executable.

### UI, Theme & System Tray Standards
- **Theme Switching:** Ensure `ThemeService` removes all existing `DarkTheme.xaml` / `LightTheme.xaml` dictionaries from `Application.Current.Resources.MergedDictionaries` before inserting the new theme so Light/Dark switching always takes effect.
- **Control Contrast:** Provide explicit styles for `DataGridColumnHeader`, `DataGridRow`, `DataGridCell`, and `ComboBox` using dynamic theme brushes so text is never invisible in Dark or Light mode.
- **System Tray:** Support visible System Tray (`NotifyIcon` with `ShutdownMode.OnExplicitShutdown` and `<Using Remove="System.Windows.Forms" />` to avoid WPF type ambiguities) with **Open OpenCam**, **Copy Pairing URL**, and **Exit OpenCam** menu items.
- **Live Logs & Audit Trail UI:** Include a dedicated **Logs & Audit** tab in the main window backed by a thread-safe, secret-redacting rolling file logger + in-memory ring buffer, with **Copy Logs**, **Open Log Folder**, and **Clear** buttons so connection, HTTPS, ICE, and virtual-camera diagnostics are immediately visible.

### WebRTC & Signaling Reliability
- **Early ICE Candidate Queuing:** Always buffer incoming ICE candidates until `RTCPeerConnection` is created and `setRemoteDescription` has completed, then drain the queue.
- **Safe Teardown:** Null out `RTCDataChannel` and `RTCPeerConnection` fields before calling `.close()` to prevent `onclose` re-entrancy stack overflows.
- **Transient ICE States:** Do not abort initial ICE negotiation on transient `disconnected` states; only fail on `failed` or `closed`, and log all ICE candidate types (`host`, `srflx`, `relay`) and connection states.

### Strict Testing & Git Gate
- **No Unverified Pushes:** Do **NOT** run `git commit`, `git tag`, `git push`, or create any GitHub Release until all phases are implemented, automated tests pass, the single-file `OpenCam.exe` is published, and the user confirms real-device testing works first.
- **Project Tracking:** Maintain `TODO.md`, `PROGRESS.md`, and `TEST_REPORT.md` honestly, clearly distinguishing unit-tested, integration-tested, and real-hardware-verified features.


---

## 14. Core Technical Decisions & Priority Features

### 1. Browser-First Mobile Capture (No APKs Initially)
- Do **not** require Android APKs or iOS apps initially.
- Build the Windows desktop application and the mobile browser interface first so testing across Android, iPhone, and iPad works immediately via QR code.
- Only consider native Android/iOS apps later if specific browser hardware limitations justify them.

### 2. Full-Screen Mobile Web App
- The mobile web camera interface must be **full-screen** (`100dvh` × `100vw`, edge-to-edge live camera preview with `object-fit: cover` or toggleable fit, safe-area inset handling for notches, and minimal translucent/collapsible controls so the viewfinder fills the screen).
- Include Fullscreen API toggle where supported (Android Chrome) and Screen Wake Lock (`navigator.wakeLock`) where supported so the phone screen does not dim or sleep while streaming.

### 3. True WebRTC Video Streaming (Not HTTP Video Stream)
- Use **WebRTC** (`RTCPeerConnection` video/audio tracks) for real-time, low-latency camera transmission — do not fall back to an HTTP MJPEG or frame-polling stream as the primary transport.

### 4. Windows Virtual Webcam is Mandatory
- Do **not** skip the Windows virtual camera implementation.
- `OpenCam` must appear as a real selectable webcam device in **OBS Studio, Zoom, Microsoft Teams, Google Meet, and Discord** (not just a preview window inside the app).

### 5. Multi-Phone Simultaneous Connections & Live Switching
- Support connecting **2 to 3 phones simultaneously** to the Windows desktop application, each with an independent WebRTC stream and live preview card.
- Allow the user to **select and hot-switch** which connected phone is currently feeding the active `OpenCam` Windows virtual webcam output in real time without disconnecting the other phones.
