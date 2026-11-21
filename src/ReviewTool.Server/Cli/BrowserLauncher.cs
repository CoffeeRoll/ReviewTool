using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReviewTool.Server.Cli;

public static class BrowserLauncher
{
    /// <summary>Opens a URL in the default browser. Returns false if that wasn't possible (e.g. headless machine).</summary>
    public static bool TryOpen(string url)
    {
        try
        {
            ProcessStartInfo psi;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) psi = new ProcessStartInfo(url) { UseShellExecute = true };
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) psi = new ProcessStartInfo("open", [url]);
            else psi = new ProcessStartInfo("xdg-open", [url]);
            psi.RedirectStandardError = !psi.UseShellExecute;
            psi.RedirectStandardOutput = !psi.UseShellExecute;
            using var p = Process.Start(psi);
            return p != null;
        }
        catch
        {
            return false;
        }
    }
}
