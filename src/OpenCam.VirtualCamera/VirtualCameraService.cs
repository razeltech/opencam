using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using OpenCam.Core.Logging;

namespace OpenCam.VirtualCamera;

public sealed class VirtualCameraService : IDisposable
{
    public const string FilterClsid = "{8E3A9B12-4F7D-4C21-9A6E-3D5C1B2A7F01}";
    private const string SharedMemoryName = @"Local\OpenCamVirtualCamSharedFrame";
    private const uint SharedMagic = 0x4F43414D; // "OCAM"
    private const int HeaderBytes = 20;
    private const int MaxWidth = 1920;
    private const int MaxHeight = 1080;
    private const int MaxCapacity = HeaderBytes + (MaxWidth * MaxHeight * 3);

    private readonly AppLogger _logger;
    private readonly string _stagingDirectory;
    private readonly object _sync = new();

    private SafeFileHandle? _nativeMapHandle;
    private IntPtr _mappedView = IntPtr.Zero;
    private uint _sequence;
    private bool _isActive;

    public bool IsActive => _isActive;
    public string StagingDllPath => Path.Combine(_stagingDirectory, "OpenCamVirtualCam.dll");
    public static string ProgramFilesDriverPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "OpenCam",
        "OpenCamVirtualCam.dll");

    public VirtualCameraService(AppLogger logger, string? driverDirectory = null)
    {
        _logger = logger;
        _stagingDirectory = driverDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenCam",
            "Driver");
        Directory.CreateDirectory(_stagingDirectory);
    }

    public static bool IsDriverRegistered()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{FilterClsid}\InprocServer32");
        var registeredPath = key?.GetValue(null) as string;
        return !string.IsNullOrEmpty(registeredPath) &&
               registeredPath.Equals(ProgramFilesDriverPath, StringComparison.OrdinalIgnoreCase) &&
               File.Exists(ProgramFilesDriverPath);
    }

    public bool IsDriverUpToDate()
    {
        if (!IsDriverRegistered()) return false;
        try
        {
            var embeddedBytes = ReadEmbeddedDriverBytes();
            var installedBytes = File.ReadAllBytes(ProgramFilesDriverPath);
            if (embeddedBytes.Length != installedBytes.Length) return false;
            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(embeddedBytes),
                SHA256.HashData(installedBytes));
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ReadEmbeddedDriverBytes()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("OpenCamVirtualCam.dll", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded OpenCamVirtualCam.dll resource not found.");
        using var src = asm.GetManifestResourceStream(resourceName)!;
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        return ms.ToArray();
    }

    public string EnsureDriverExtracted()
    {
        var bytes = ReadEmbeddedDriverBytes();
        try
        {
            File.WriteAllBytes(StagingDllPath, bytes);
            _logger.Info("VirtualCam", $"Extracted virtual camera driver to {StagingDllPath}");
        }
        catch (IOException)
        {
            // Keep existing file if locked.
        }

        return StagingDllPath;
    }

    public async Task<bool> RegisterDriverElevatedAsync()
    {
        var stagedPath = EnsureDriverExtracted();
        var targetDir = Path.GetDirectoryName(ProgramFilesDriverPath)!;
        var targetPath = ProgramFilesDriverPath;
        var oldBackupPath = targetPath + ".old";

        // Rename any in-use DLL to .old (Windows permits renaming loaded DLLs) before copying the new DLL.
        var cmd = $"if exist \"{stagedPath}\" ( " +
                  $"mkdir \"{targetDir}\" 2>nul & " +
                  $"regsvr32 /u /s \"{stagedPath}\" 2>nul & " +
                  $"del /f /q \"{oldBackupPath}\" 2>nul & " +
                  $"if exist \"{targetPath}\" move /y \"{targetPath}\" \"{oldBackupPath}\" >nul 2>&1 & " +
                  $"copy /y \"{stagedPath}\" \"{targetPath}\" >nul && " +
                  $"regsvr32 /s \"{targetPath}\" )";

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {cmd}",
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync();
            var ok = proc.ExitCode == 0 && IsDriverRegistered();
            _logger.Info("VirtualCam", ok
                ? $"OpenCam DirectShow virtual camera registered at {targetPath}."
                : $"Driver registration exited with code {proc.ExitCode}.");
            return ok;
        }
        catch (Exception ex)
        {
            _logger.Warn("VirtualCam", $"Driver registration cancelled or failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UnregisterDriverElevatedAsync()
    {
        var targetPath = ProgramFilesDriverPath;
        var cmd = $"regsvr32 /u /s \"{targetPath}\" 2>nul & regsvr32 /u /s \"{StagingDllPath}\" 2>nul";
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {cmd}",
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync();
            _logger.Info("VirtualCam", $"OpenCam virtual camera driver unregistered (exit {proc.ExitCode}).");
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.Warn("VirtualCam", $"Driver unregistration failed: {ex.Message}");
            return false;
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            EnsureMapped();
            _isActive = true;
            if (_mappedView != IntPtr.Zero)
            {
                Marshal.WriteInt32(_mappedView, 0, unchecked((int)SharedMagic));
                Marshal.WriteInt32(_mappedView, 16, 1);
            }
            _logger.Info("VirtualCam", "Virtual camera output started.");
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _isActive = false;
            if (_mappedView != IntPtr.Zero)
            {
                Marshal.WriteInt32(_mappedView, 8, 0);
                Marshal.WriteInt32(_mappedView, 12, 0);
                Marshal.WriteInt32(_mappedView, 16, 0);
            }
            _logger.Info("VirtualCam", "Virtual camera output stopped.");
        }
    }

    public void ClearLiveFeed()
    {
        lock (_sync)
        {
            if (_mappedView != IntPtr.Zero)
            {
                Marshal.WriteInt32(_mappedView, 8, 0);
                Marshal.WriteInt32(_mappedView, 12, 0);
            }
        }
    }

    public void PushFrame(int width, int height, byte[] bgr24)
    {
        if (!_isActive || width <= 0 || height <= 0 || width > MaxWidth || height > MaxHeight) return;
        var expectedBytes = width * height * 3;
        if (bgr24.Length < expectedBytes) return;

        lock (_sync)
        {
            if (!_isActive || _mappedView == IntPtr.Zero) return;
            _sequence++;
            Marshal.WriteInt32(_mappedView, 0, unchecked((int)SharedMagic));
            Marshal.WriteInt32(_mappedView, 4, unchecked((int)_sequence));
            Marshal.WriteInt32(_mappedView, 8, width);
            Marshal.WriteInt32(_mappedView, 12, height);
            Marshal.WriteInt32(_mappedView, 16, 1);
            Marshal.Copy(bgr24, 0, IntPtr.Add(_mappedView, HeaderBytes), expectedBytes);
        }
    }

    private void EnsureMapped()
    {
        if (_mappedView != IntPtr.Zero) return;

        const string sddl = "D:(A;;GA;;;IU)(A;;GA;;;BA)(A;;GA;;;SY)(A;;GR;;;WD)(A;;GR;;;AC)";
        IntPtr pSd = IntPtr.Zero;
        try
        {
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                bInheritHandle = false
            };

            if (ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out pSd, out _))
            {
                sa.lpSecurityDescriptor = pSd;
            }

            var hMap = CreateFileMappingW(
                new IntPtr(-1),
                ref sa,
                0x04,
                0,
                (uint)MaxCapacity,
                SharedMemoryName);

            if (hMap == IntPtr.Zero || hMap == new IntPtr(-1))
            {
                throw new InvalidOperationException($"CreateFileMappingW failed with Win32 error {Marshal.GetLastWin32Error()}");
            }

            _nativeMapHandle = new SafeFileHandle(hMap, ownsHandle: true);
            _mappedView = MapViewOfFile(_nativeMapHandle, 0x0002 | 0x0004, 0, 0, (UIntPtr)MaxCapacity);
            if (_mappedView == IntPtr.Zero)
            {
                throw new InvalidOperationException($"MapViewOfFile failed with Win32 error {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            if (pSd != IntPtr.Zero) LocalFree(pSd);
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_sync)
        {
            if (_mappedView != IntPtr.Zero)
            {
                UnmapViewOfFile(_mappedView);
                _mappedView = IntPtr.Zero;
            }
            _nativeMapHandle?.Dispose();
            _nativeMapHandle = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(
        IntPtr hFile,
        ref SECURITY_ATTRIBUTES lpFileMappingAttributes,
        uint flProtect,
        uint dwMaximumSizeHigh,
        uint dwMaximumSizeLow,
        string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(
        SafeFileHandle hFileMappingObject,
        uint dwDesiredAccess,
        uint dwFileOffsetHigh,
        uint dwFileOffsetLow,
        UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
