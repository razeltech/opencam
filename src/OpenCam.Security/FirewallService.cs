using System.Diagnostics;
using OpenCam.Core.Logging;

namespace OpenCam.Security;

public static class FirewallService
{
    private const string PortRulePrefix = "OpenCam Inbound Port ";
    private const string AppRuleName = "OpenCam App";

    public static bool IsPortRulePresent(int port)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            if (!CheckRuleOutput($"advfirewall firewall show rule name=\"{PortRulePrefix}{port}\"", "Enabled:"))
            {
                return false;
            }

            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                return CheckRuleOutput($"advfirewall firewall show rule name=\"{AppRuleName}\" verbose", exePath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckRuleOutput(string args, string requiredSubstring)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc is null) return false;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(3000);
        return proc.ExitCode == 0 && output.Contains(requiredSubstring, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> EnsurePortAllowedAsync(int port, AppLogger logger)
    {
        if (!OperatingSystem.IsWindows()) return true;

        var portRule = $"{PortRulePrefix}{port}";
        var exePath = Environment.ProcessPath;
        var commands = $"netsh advfirewall firewall delete rule name=\"{portRule}\" >nul 2>&1 & " +
                       $"netsh advfirewall firewall add rule name=\"{portRule}\" dir=in action=allow protocol=TCP localport={port} profile=any";

        if (!string.IsNullOrEmpty(exePath))
        {
            commands += $" & netsh advfirewall firewall delete rule name=\"{AppRuleName}\" >nul 2>&1 & " +
                        $"netsh advfirewall firewall add rule name=\"{AppRuleName}\" dir=in action=allow program=\"{exePath}\" protocol=any profile=any";
        }

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {commands}",
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync();
            var ok = proc.ExitCode == 0 && IsPortRulePresent(port);
            logger.Info("Firewall", ok
                ? $"Windows Firewall rules added for TCP {port} and WebRTC UDP media ({exePath})."
                : $"Failed to add firewall rule (exit {proc.ExitCode}).");
            return ok;
        }
        catch (Exception ex)
        {
            logger.Warn("Firewall", $"Firewall rule elevation cancelled or failed: {ex.Message}");
            return false;
        }
    }

    public static void RemovePortRuleOnExit(int port, AppLogger logger)
    {
        if (!OperatingSystem.IsWindows() || port <= 0) return;
        try
        {
            var portRule = $"{PortRulePrefix}{port}";
            var cmd = $"netsh advfirewall firewall delete rule name=\"{portRule}\" >nul 2>&1 & " +
                      $"netsh advfirewall firewall delete rule name=\"{AppRuleName}\" >nul 2>&1";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {cmd}",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            logger.Info("Firewall", $"Cleaned up firewall rules for port {port}.");
        }
        catch
        {
            // Ignore if UAC declined on exit.
        }
    }
}
