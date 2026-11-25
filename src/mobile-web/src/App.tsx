import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

type ConnectionStatus = 'idle' | 'connecting' | 'streaming' | 'reconnecting' | 'error';
type MirrorMode = 'auto' | 'on' | 'off';

interface ExtendedCapabilities extends MediaTrackCapabilities {
  torch?: boolean;
  zoom?: { min: number; max: number; step: number };
  exposureCompensation?: { min: number; max: number; step: number };
  focusMode?: string[];
  facingMode?: string[];
}

const ICE_CONFIG: RTCConfiguration = {
  iceServers: [
    { urls: 'stun:stun.l.google.com:19302' },
    { urls: 'stun:stun.cloudflare.com:3478' },
    {
      urls: 'turn:openrelay.metered.ca:443?transport=tcp',
      username: 'openrelayproject',
      credential: 'openrelayproject'
    }
  ],
  iceCandidatePoolSize: 6
};

const RESOLUTIONS: Record<string, { width: number; height: number; label: string }> = {
  '360p': { width: 640, height: 360, label: '640 x 360' },
  '480p': { width: 640, height: 480, label: '640 x 480' },
  '540p': { width: 960, height: 540, label: '960 x 540' },
  '720p': { width: 1280, height: 720, label: '1280 x 720' },
  '1080p': { width: 1920, height: 1080, label: '1920 x 1080' },
  '1440p': { width: 2560, height: 1440, label: '2560 x 1440' }
};

function getOrCreateDeviceId(): string {
  const key = 'opencam_device_id';
  let id = localStorage.getItem(key);
  if (!id) {
    id = `mob-${Math.random().toString(36).slice(2, 10)}`;
    localStorage.setItem(key, id);
  }
  return id;
}

export default function App() {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const pcRef = useRef<RTCPeerConnection | null>(null);
  const wsRef = useRef<WebSocket | null>(null);
  const pendingCandidatesRef = useRef<RTCIceCandidateInit[]>([]);
  const remoteDescSetRef = useRef(false);
  const wakeLockRef = useRef<WakeLockSentinel | null>(null);

  const [deviceId] = useState(getOrCreateDeviceId);
  const [token] = useState(() => new URLSearchParams(window.location.search).get('token') ?? '');
  const [pin, setPin] = useState('');
  const [needsPin, setNeedsPin] = useState(false);

  const [permissionGranted, setPermissionGranted] = useState(false);
  const [status, setStatus] = useState<ConnectionStatus>('idle');
  const [statusNote, setStatusNote] = useState('Ready');

  const [videoDevices, setVideoDevices] = useState<MediaDeviceInfo[]>([]);
  const [audioDevices, setAudioDevices] = useState<MediaDeviceInfo[]>([]);
  const [selectedVideoId, setSelectedVideoId] = useState<string>('');
  const [selectedAudioId, setSelectedAudioId] = useState<string>('');
  const [resolutionKey, setResolutionKey] = useState<string>('720p');
  const [fps, setFps] = useState<number>(30);
  const [audioEnabled, setAudioEnabled] = useState<boolean>(false);

  const [coverFit, setCoverFit] = useState<boolean>(true);
  const [mirrorMode, setMirrorMode] = useState<MirrorMode>('auto');
  const [isFrontCamera, setIsFrontCamera] = useState<boolean>(false);
  const [showSettings, setShowSettings] = useState<boolean>(false);
  const [hudVisible, setHudVisible] = useState<boolean>(true);
  const [capabilities, setCapabilities] = useState<ExtendedCapabilities>({});
  const [torchOn, setTorchOn] = useState<boolean>(false);
  const [zoomValue, setZoomValue] = useState<number>(1);
  const [exposureValue, setExposureValue] = useState<number>(0);
  const [batteryPercent, setBatteryPercent] = useState<number | null>(null);
  const [focusPoint, setFocusPoint] = useState<{ x: number; y: number } | null>(null);

  const isMirrored = useMemo(() => {
    if (mirrorMode === 'on') return true;
    if (mirrorMode === 'off') return false;
    return isFrontCamera;
  }, [mirrorMode, isFrontCamera]);

  const refreshDevices = useCallback(async () => {
    const devices = await navigator.mediaDevices.enumerateDevices();
    const cams = devices.filter(d => d.kind === 'videoinput');
    const mics = devices.filter(d => d.kind === 'audioinput');
    setVideoDevices(cams);
    setAudioDevices(mics);
    if (!selectedVideoId && cams.length > 0) {
      const rear = cams.find(c => /back|rear|environment/i.test(c.label));
      setSelectedVideoId((rear ?? cams[0]).deviceId);
    }
    if (!selectedAudioId && mics.length > 0) {
      setSelectedAudioId(mics[0].deviceId);
    }
  }, [selectedVideoId, selectedAudioId]);

  const acquireWakeLock = useCallback(async () => {
    try {
      if ('wakeLock' in navigator && !wakeLockRef.current) {
        wakeLockRef.current = await navigator.wakeLock.request('screen');
      }
    } catch {
      // Ignore unsupported wake lock.
    }
  }, []);

  const startLocalPreview = useCallback(async (
    vidId: string,
    audId: string,
    resKey: string,
    targetFps: number,
    withAudio: boolean
  ): Promise<MediaStream> => {
    if (streamRef.current) {
      streamRef.current.getTracks().forEach(t => t.stop());
    }

    const res = RESOLUTIONS[resKey] ?? RESOLUTIONS['720p'];
    const constraints: MediaStreamConstraints = {
      video: {
        deviceId: vidId ? { exact: vidId } : undefined,
        width: { ideal: res.width },
        height: { ideal: res.height },
        frameRate: { ideal: targetFps, max: 60 }
      },
      audio: withAudio
        ? {
            deviceId: audId ? { exact: audId } : undefined,
            echoCancellation: true,
            noiseSuppression: true,
            autoGainControl: true
          }
        : false
    };

    const stream = await navigator.mediaDevices.getUserMedia(constraints);
    streamRef.current = stream;

    if (videoRef.current) {
      videoRef.current.srcObject = stream;
    }

    const videoTrack = stream.getVideoTracks()[0];
    if (videoTrack) {
      const settings = videoTrack.getSettings();
      const label = videoTrack.label || '';
      const front = settings.facingMode === 'user' || /front|user|selfie/i.test(label);
      setIsFrontCamera(front);

      if (typeof videoTrack.getCapabilities === 'function') {
        const caps = videoTrack.getCapabilities() as ExtendedCapabilities;
        setCapabilities(caps);
        if (caps.zoom) setZoomValue(caps.zoom.min);
        if (caps.exposureCompensation) setExposureValue(0);
        setTorchOn(false);
      } else {
        setCapabilities({});
      }
    }

    setPermissionGranted(true);
    await refreshDevices();
    return stream;
  }, [refreshDevices]);

  const cleanupPeerConnection = useCallback(() => {
    remoteDescSetRef.current = false;
    pendingCandidatesRef.current = [];
    const pc = pcRef.current;
    pcRef.current = null;
    if (pc) {
      pc.onicecandidate = null;
      pc.onconnectionstatechange = null;
      pc.oniceconnectionstatechange = null;
      pc.close();
    }
  }, []);

  const connectToServer = useCallback(async () => {
    try {
      setStatus('connecting');
      setStatusNote('Starting...');

      const stream = streamRef.current ?? await startLocalPreview(
        selectedVideoId,
        selectedAudioId,
        resolutionKey,
        fps,
        audioEnabled
      );

      await acquireWakeLock();

      const wsProto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
      const wsUrl = `${wsProto}//${window.location.host}/ws?token=${encodeURIComponent(token)}&pin=${encodeURIComponent(pin)}`;
      const ws = new WebSocket(wsUrl);
      wsRef.current = ws;

      ws.onopen = async () => {
        setStatusNote('Linking...');
        cleanupPeerConnection();

        const pc = new RTCPeerConnection(ICE_CONFIG);
        pcRef.current = pc;

        stream.getTracks().forEach(track => pc.addTrack(track, stream));

        pc.onicecandidate = ev => {
          if (ev.candidate && ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify({
              type: 'ice-candidate',
              deviceId,
              candidate: {
                candidate: ev.candidate.candidate,
                sdpMid: ev.candidate.sdpMid,
                sdpMLineIndex: ev.candidate.sdpMLineIndex ?? 0
              }
            }));
          }
        };

        pc.oniceconnectionstatechange = () => {
          if (ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify({ type: 'client-log', deviceId, error: `Mobile ICE state: ${pc.iceConnectionState}` }));
          }
        };

        pc.onconnectionstatechange = () => {
          if (ws.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify({ type: 'client-log', deviceId, error: `Mobile PC state: ${pc.connectionState}` }));
          }
          if (pc.connectionState === 'connected') {
            setStatus('streaming');
            setStatusNote('Live');
          } else if (pc.connectionState === 'disconnected') {
            setStatus('reconnecting');
            setStatusNote('Reconnecting');
          } else if (pc.connectionState === 'failed') {
            setStatus('error');
            setStatusNote('Failed');
          }
        };

        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);

        ws.send(JSON.stringify({
          type: 'offer',
          deviceId,
          deviceName: navigator.userAgent.includes('iPhone')
            ? 'iPhone'
            : navigator.userAgent.includes('iPad')
              ? 'iPad'
              : navigator.userAgent.includes('Android')
                ? 'Android Phone'
                : 'Mobile Browser',
          sdp: offer.sdp
        }));
      };

      ws.onmessage = async ev => {
        const msg = JSON.parse(ev.data);
        const pc = pcRef.current;

        if (msg.type === 'answer' && pc && msg.sdp) {
          await pc.setRemoteDescription({ type: 'answer', sdp: msg.sdp });
          remoteDescSetRef.current = true;
          while (pendingCandidatesRef.current.length > 0) {
            const c = pendingCandidatesRef.current.shift()!;
            await pc.addIceCandidate(c);
          }
        } else if (msg.type === 'ice-candidate' && pc && msg.candidate) {
          const init: RTCIceCandidateInit = {
            candidate: msg.candidate.candidate,
            sdpMid: msg.candidate.sdpMid,
            sdpMLineIndex: msg.candidate.sdpMLineIndex
          };
          if (!remoteDescSetRef.current) {
            pendingCandidatesRef.current.push(init);
          } else {
            await pc.addIceCandidate(init);
          }
        } else if (msg.type === 'command' && msg.command) {
          if (msg.command.action === 'set-resolution' && msg.command.value) {
            const rk = String(msg.command.value);
            const targetRes = RESOLUTIONS[rk];
            const track = streamRef.current?.getVideoTracks()[0];
            if (targetRes && track) {
              setResolutionKey(rk);
              await track.applyConstraints({
                width: { ideal: targetRes.width },
                height: { ideal: targetRes.height }
              }).catch(() => {});
            }
          }
        } else if (msg.type === 'error') {
          setStatus('error');
          setStatusNote(msg.error ?? 'Auth failed');
          if (String(msg.error).toLowerCase().includes('pin')) {
            setNeedsPin(true);
          }
        }
      };

      ws.onclose = () => {
        cleanupPeerConnection();
        setStatus(prev => (prev === 'error' ? 'error' : 'idle'));
        setStatusNote('Offline');
      };
    } catch (err) {
      setStatus('error');
      setStatusNote(err instanceof Error ? err.message : 'Camera error');
    }
  }, [
    acquireWakeLock,
    audioEnabled,
    cleanupPeerConnection,
    deviceId,
    fps,
    pin,
    resolutionKey,
    selectedAudioId,
    selectedVideoId,
    startLocalPreview,
    token
  ]);

  const disconnect = useCallback(() => {
    const ws = wsRef.current;
    wsRef.current = null;
    if (ws) ws.close();
    cleanupPeerConnection();
    setStatus('idle');
    setStatusNote('Offline');
  }, [cleanupPeerConnection]);

  useEffect(() => {
    const nav = navigator as Navigator & { getBattery?: () => Promise<{ level: number; addEventListener: (e: string, cb: () => void) => void }> };
    if (typeof nav.getBattery === 'function') {
      nav.getBattery().then(b => {
        const update = () => setBatteryPercent(Math.round(b.level * 100));
        update();
        b.addEventListener('levelchange', update);
      }).catch(() => {});
    }
  }, []);

  useEffect(() => {
    if (status !== 'streaming') return;
    const timer = window.setInterval(() => {
      const ws = wsRef.current;
      const track = streamRef.current?.getVideoTracks()[0];
      const settings = track?.getSettings();
      if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({
          type: 'telemetry',
          deviceId,
          telemetry: {
            width: settings?.width ?? 0,
            height: settings?.height ?? 0,
            fps: settings?.frameRate ?? fps,
            batteryPercent,
            audioEnabled,
            sentTimestampMs: Date.now()
          }
        }));
      }
    }, 1500);
    return () => window.clearInterval(timer);
  }, [audioEnabled, batteryPercent, deviceId, fps, status]);

  const switchCamera = async () => {
    if (videoDevices.length < 2) return;
    const idx = videoDevices.findIndex(d => d.deviceId === selectedVideoId);
    const next = videoDevices[(idx + 1) % videoDevices.length];
    setSelectedVideoId(next.deviceId);
    const newStream = await startLocalPreview(next.deviceId, selectedAudioId, resolutionKey, fps, audioEnabled);
    const pc = pcRef.current;
    const newTrack = newStream.getVideoTracks()[0];
    const sender = pc?.getSenders().find(s => s.track?.kind === 'video');
    if (sender && newTrack) {
      await sender.replaceTrack(newTrack);
    }
  };

  const cycleMirrorMode = () => {
    setMirrorMode(prev => (prev === 'auto' ? 'on' : prev === 'on' ? 'off' : 'auto'));
  };

  const applyTrackConstraint = async (advanced: Record<string, unknown>) => {
    const track = streamRef.current?.getVideoTracks()[0];
    if (!track) return;
    try {
      await track.applyConstraints({ advanced: [advanced as MediaTrackConstraintSet] });
    } catch {
      // Ignore unsupported constraint.
    }
  };

  const handleVideoTap = async (e: React.MouseEvent<HTMLVideoElement>) => {
    setHudVisible(v => !v);
    if (!capabilities.focusMode?.includes('single-shot') && !capabilities.focusMode?.includes('continuous')) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const x = (e.clientX - rect.left) / rect.width;
    const y = (e.clientY - rect.top) / rect.height;
    setFocusPoint({ x: e.clientX, y: e.clientY });
    window.setTimeout(() => setFocusPoint(null), 900);
    await applyTrackConstraint({ focusMode: 'single-shot', pointsOfInterest: [{ x, y }] });
  };

  const toggleFullscreen = async () => {
    if (!document.fullscreenElement) {
      await document.documentElement.requestFullscreen?.().catch(() => {});
    } else {
      await document.exitFullscreen?.().catch(() => {});
    }
  };

  return (
    <div className="viewport">
      <video
        ref={videoRef}
        className={`video-feed ${coverFit ? 'cover' : 'contain'} ${isMirrored ? 'mirrored' : ''}`}
        autoPlay
        muted
        playsInline
        onClick={handleVideoTap}
      />

      {focusPoint && (
        <div className="focus-ring" style={{ left: focusPoint.x, top: focusPoint.y }} />
      )}

      {hudVisible && (
        <div className="hud-top">
          <div className="pill">
            <span className={`dot ${status === 'streaming' ? 'live' : status === 'connecting' || status === 'reconnecting' ? 'connecting' : status === 'error' ? 'error' : ''}`} />
            <span>{statusNote}</span>
          </div>

          <div className="icon-group">
            {batteryPercent !== null && <div className="pill">{batteryPercent}%</div>}
            <button className="icon-btn" title="Fit / Fill" onClick={() => setCoverFit(c => !c)}>
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <rect x="3" y="5" width="18" height="14" rx="2" />
                <path d="M8 9h8v6H8z" />
              </svg>
            </button>
            {document.fullscreenEnabled && (
              <button className="icon-btn" title="Fullscreen" onClick={toggleFullscreen}>
                <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                  <path d="M8 3H5a2 2 0 0 0-2 2v3m18 0V5a2 2 0 0 0-2-2h-3m0 18h3a2 2 0 0 0 2-2v-3M3 16v3a2 2 0 0 0 2 2h3" />
                </svg>
              </button>
            )}
          </div>
        </div>
      )}

      {hudVisible && permissionGranted && (
        <div className="hud-bottom">
          {showSettings && (
            <div className="drawer">
              {videoDevices.length > 0 && (
                <div className="field-row">
                  <label>Camera</label>
                  <select
                    value={selectedVideoId}
                    onChange={async e => {
                      const id = e.target.value;
                      setSelectedVideoId(id);
                      const s = await startLocalPreview(id, selectedAudioId, resolutionKey, fps, audioEnabled);
                      const sender = pcRef.current?.getSenders().find(x => x.track?.kind === 'video');
                      const t = s.getVideoTracks()[0];
                      if (sender && t) await sender.replaceTrack(t);
                    }}
                  >
                    {videoDevices.map((d, i) => (
                      <option key={d.deviceId} value={d.deviceId}>
                        {d.label || `Camera ${i + 1}`}
                      </option>
                    ))}
                  </select>
                </div>
              )}

              <div className="field-row">
                <label>Resolution</label>
                <select
                  value={resolutionKey}
                  onChange={async e => {
                    const rk = e.target.value;
                    setResolutionKey(rk);
                    const targetRes = RESOLUTIONS[rk];
                    const track = streamRef.current?.getVideoTracks()[0];
                    if (targetRes && track) {
                      await track.applyConstraints({
                        width: { ideal: targetRes.width },
                        height: { ideal: targetRes.height }
                      }).catch(() => {});
                    }
                  }}
                >
                  {Object.entries(RESOLUTIONS).map(([k, v]) => (
                    <option key={k} value={k}>{v.label}</option>
                  ))}
                </select>
              </div>

              <div className="field-row">
                <label>FPS</label>
                <select
                  value={fps}
                  onChange={async e => {
                    const nextFps = Number(e.target.value);
                    setFps(nextFps);
                    const s = await startLocalPreview(selectedVideoId, selectedAudioId, resolutionKey, nextFps, audioEnabled);
                    const sender = pcRef.current?.getSenders().find(x => x.track?.kind === 'video');
                    const t = s.getVideoTracks()[0];
                    if (sender && t) await sender.replaceTrack(t);
                  }}
                >
                  <option value={24}>24 FPS</option>
                  <option value={30}>30 FPS</option>
                  <option value={60}>60 FPS</option>
                </select>
              </div>

              {capabilities.zoom && (
                <div className="field-row">
                  <label>Zoom {zoomValue.toFixed(1)}x</label>
                  <input
                    type="range"
                    min={capabilities.zoom.min}
                    max={capabilities.zoom.max}
                    step={capabilities.zoom.step || 0.1}
                    value={zoomValue}
                    onChange={e => {
                      const val = Number(e.target.value);
                      setZoomValue(val);
                      applyTrackConstraint({ zoom: val });
                    }}
                  />
                </div>
              )}

              {capabilities.exposureCompensation && (
                <div className="field-row">
                  <label>Exposure {exposureValue.toFixed(1)}</label>
                  <input
                    type="range"
                    min={capabilities.exposureCompensation.min}
                    max={capabilities.exposureCompensation.max}
                    step={capabilities.exposureCompensation.step || 0.1}
                    value={exposureValue}
                    onChange={e => {
                      const val = Number(e.target.value);
                      setExposureValue(val);
                      applyTrackConstraint({ exposureCompensation: val });
                    }}
                  />
                </div>
              )}

              {audioDevices.length > 0 && (
                <div className="field-row">
                  <label>Mic</label>
                  <select value={selectedAudioId} onChange={e => setSelectedAudioId(e.target.value)}>
                    {audioDevices.map((d, i) => (
                      <option key={d.deviceId} value={d.deviceId}>
                        {d.label || `Mic ${i + 1}`}
                      </option>
                    ))}
                  </select>
                </div>
              )}
            </div>
          )}

          <div className="controls-row">
            <div className="icon-group">
              {videoDevices.length > 1 && (
                <button className="icon-btn" title="Flip Camera" onClick={switchCamera}>
                  <svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                    <path d="M11 19H4a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2h5" />
                    <path d="M13 5h7a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2h-5" />
                    <path d="m14 9-3 3 3 3" />
                  </svg>
                </button>
              )}

              <button
                className={`icon-btn ${isMirrored ? 'active' : ''}`}
                title={`Mirror: ${mirrorMode.toUpperCase()}`}
                onClick={cycleMirrorMode}
              >
                <svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                  <path d="M12 3v18" />
                  <path d="m8 7-5 5 5 5V7z" />
                  <path d="m16 7 5 5-5 5V7z" />
                </svg>
                <span className="icon-badge">{mirrorMode === 'auto' ? 'A' : mirrorMode === 'on' ? '1' : '0'}</span>
              </button>

              {capabilities.torch && (
                <button
                  className={`icon-btn ${torchOn ? 'active' : ''}`}
                  title="Torch"
                  onClick={() => {
                    const next = !torchOn;
                    setTorchOn(next);
                    applyTrackConstraint({ torch: next });
                  }}
                >
                  <svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                    <path d="M13 2 3 14h9l-1 8 10-12h-9l1-8z" />
                  </svg>
                </button>
              )}

              <button
                className={`icon-btn ${audioEnabled ? 'active' : ''}`}
                title="Microphone"
                onClick={async () => {
                  const next = !audioEnabled;
                  setAudioEnabled(next);
                  await startLocalPreview(selectedVideoId, selectedAudioId, resolutionKey, fps, next);
                }}
              >
                <svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                  <path d="M12 2a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3Z" />
                  <path d="M19 10v2a7 7 0 0 1-14 0v-2" />
                  <line x1="12" x2="12" y1="19" y2="22" />
                  {!audioEnabled && <line x1="2" y1="2" x2="22" y2="22" />}
                </svg>
              </button>

              <button
                className={`icon-btn ${showSettings ? 'active' : ''}`}
                title="Settings"
                onClick={() => setShowSettings(s => !s)}
              >
                <svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                  <circle cx="12" cy="12" r="3" />
                  <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9c.26.6.77 1.04 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
                </svg>
              </button>
            </div>

            {status === 'streaming' || status === 'connecting' ? (
              <button className="icon-btn danger" title="Stop" onClick={disconnect}>
                <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor">
                  <rect x="6" y="6" width="12" height="12" rx="2" />
                </svg>
              </button>
            ) : (
              <button className="icon-btn active" title="Connect" onClick={connectToServer}>
                <svg width="19" height="19" viewBox="0 0 24 24" fill="currentColor">
                  <polygon points="6 4 20 12 6 20 6 4" />
                </svg>
              </button>
            )}
          </div>
        </div>
      )}

      {!permissionGranted && (
        <div className="connect-overlay">
          <div className="connect-card">
            <h1>OpenCam</h1>
            <p>Allow camera access to stream wirelessly to your PC.</p>
            {needsPin && (
              <input
                className="pin-input"
                type="password"
                placeholder="Enter PIN"
                value={pin}
                onChange={e => setPin(e.target.value)}
              />
            )}
            <button className="btn btn-primary" onClick={connectToServer}>
              Allow Camera & Connect
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
