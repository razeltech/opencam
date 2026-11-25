using OpenCam.Core.Logging;
using OpenCam.VirtualCamera;

namespace OpenCam.Tests;

public sealed class VirtualCameraTests
{
    [Fact]
    public void VirtualCameraService_Extracts_Driver_And_Writes_Shared_Frames()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "OpenCamVcamTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var logger = new AppLogger(tempDir);
            using var vcam = new VirtualCameraService(logger, tempDir);

            var dllPath = vcam.EnsureDriverExtracted();
            Assert.True(File.Exists(dllPath));
            Assert.True(new FileInfo(dllPath).Length > 10_000);

            vcam.Start();
            Assert.True(vcam.IsActive);

            var frame = new byte[640 * 480 * 3];
            Array.Fill(frame, (byte)0x80);
            vcam.PushFrame(640, 480, frame);

            vcam.Stop();
            Assert.False(vcam.IsActive);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
