// Date Everything Access test build setup. A small Windows program that carries the
// installer script and the mod files inside it, unpacks them to a temporary folder, runs
// the installer script, and cleans up.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DEASetup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            Extract("payload.zip", dir);
            Extract("install.ps1", dir);
            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + Path.Combine(dir, "install.ps1") + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("The setup could not start: " + ex.Message, "Date Everything Access Setup",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static void Extract(string name, string dir)
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (s == null) throw new InvalidOperationException("missing part " + name);
            using (FileStream f = File.Create(Path.Combine(dir, name)))
                s.CopyTo(f);
        }
    }
}
