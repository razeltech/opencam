using OpenCam.Core.Logging;
using OpenCam.WebRTC;

namespace OpenCam.Tests;

public sealed class WebRtcTests
{
    [Fact]
    public void WebRtcPeerSession_Queues_Early_Ice_Candidates_Before_Remote_Description()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "OpenCamWebRtcTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var logger = new AppLogger(tempDir);
            using var session = new WebRtcPeerSession("phone-1", "Test Phone", logger);

            Assert.False(session.IsRemoteDescriptionSet);
            Assert.Equal(0, session.PendingIceCandidateCount);

            session.AddRemoteIceCandidate(new IceCandidatePayload
            {
                Candidate = "candidate:1 1 UDP 2122252543 192.168.1.50 54321 typ host",
                SdpMid = "0",
                sdpMLineIndex = 0
            });

            Assert.Equal(1, session.PendingIceCandidateCount);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WebRtcSessionManager_Supports_MultiPhone_And_HotSwitch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "OpenCamWebRtcTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var logger = new AppLogger(tempDir);
            using var manager = new WebRtcSessionManager(logger);

            manager.CreateOrReplaceSession("phone-1", "Pixel 9");
            manager.CreateOrReplaceSession("phone-2", "iPhone 16");
            manager.CreateOrReplaceSession("phone-3", "iPad Pro");

            Assert.Equal(3, manager.ConnectedDeviceIds.Count);
            Assert.Equal("phone-1", manager.ActiveVirtualCamDeviceId);

            manager.SetActiveVirtualCamDevice("phone-2");
            Assert.Equal("phone-2", manager.ActiveVirtualCamDeviceId);

            manager.RemoveSession("phone-2");
            Assert.Equal(2, manager.ConnectedDeviceIds.Count);
            Assert.NotEqual("phone-2", manager.ActiveVirtualCamDeviceId);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WebRtcPeerSession_Connects_Ice_With_Peer()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "OpenCamWebRtcLoopback", Guid.NewGuid().ToString("N"));
        try
        {
            using var logger = new OpenCam.Core.Logging.AppLogger(tempDir);
            using var receiver = new WebRtcPeerSession("loop-1", "Loopback", logger);

            var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.ConnectionStateChanged += (_, state) =>
            {
                if (state == OpenCam.Core.Models.DeviceConnectionState.Streaming)
                    connectedTcs.TrySetResult(true);
            };

            using var sender = new SIPSorcery.Net.RTCPeerConnection(new SIPSorcery.Net.RTCConfiguration { iceServers = [] });
            var videoTrack = new SIPSorcery.Net.MediaStreamTrack(
                [new SIPSorceryMedia.Abstractions.VideoFormat(SIPSorceryMedia.Abstractions.VideoCodecsEnum.VP8, 96, 90000)],
                SIPSorcery.Net.MediaStreamStatusEnum.SendOnly);
            sender.addTrack(videoTrack);

            sender.onicecandidate += c =>
            {
                if (c is not null)
                    receiver.AddRemoteIceCandidate(new IceCandidatePayload { Candidate = c.candidate, SdpMid = c.sdpMid, sdpMLineIndex = c.sdpMLineIndex });
            };
            receiver.LocalIceCandidateReady += (_, c) =>
            {
                sender.addIceCandidate(new SIPSorcery.Net.RTCIceCandidateInit { candidate = c.Candidate, sdpMid = c.SdpMid, sdpMLineIndex = c.sdpMLineIndex });
            };

            var offer = sender.createOffer();
            await sender.setLocalDescription(offer);

            var answerSdp = await receiver.AcceptOfferAndCreateAnswerAsync(offer.sdp);
            sender.setRemoteDescription(new SIPSorcery.Net.RTCSessionDescriptionInit { type = SIPSorcery.Net.RTCSdpType.answer, sdp = answerSdp });

            var completed = await Task.WhenAny(connectedTcs.Task, Task.Delay(5000));
            Assert.Equal(connectedTcs.Task, completed);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void VpxVideoEncoder_Roundtrips_Bgr24_Frame()
    {
        using var encoder = new SIPSorceryMedia.Encoders.VpxVideoEncoder();
        var bgr = new byte[320 * 240 * 3];
        Array.Fill(bgr, (byte)0x5A);
        var encoded = encoder.EncodeVideo(320, 240, bgr, SIPSorceryMedia.Abstractions.VideoPixelFormatsEnum.Bgr, SIPSorceryMedia.Abstractions.VideoCodecsEnum.VP8);
        Assert.NotNull(encoded);
        Assert.NotEmpty(encoded);

        var decoded = encoder.DecodeVideo(encoded, SIPSorceryMedia.Abstractions.VideoPixelFormatsEnum.Bgr, SIPSorceryMedia.Abstractions.VideoCodecsEnum.VP8).ToList();
        Assert.NotEmpty(decoded);
        Assert.Equal(320u, decoded[0].Width);
        Assert.Equal(240u, decoded[0].Height);
    }
}
