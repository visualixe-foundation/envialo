using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Envialo
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            TrySetupFirewallOnce();
        }

        // Runs once per PC. If the Envialo firewall rules don't exist yet,
        // attempts to create them by launching an elevated (admin) helper
        // process. This will show ONE Windows UAC prompt the very first
        // time the app runs on a given PC/account.
        //
        // If the person clicks "No" on the UAC prompt, or their account
        // doesn't have admin rights at all, this fails silently and the
        // app continues to run normally — file transfer / discovery just
        // won't work across the network until the firewall is opened
        // some other way (see setup-firewall.ps1 / Group Policy).
        private void TrySetupFirewallOnce()
        {
            try
            {
                string appFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Envíalo");

                Directory.CreateDirectory(appFolder);

                string markerFile = Path.Combine(appFolder, "firewall_setup.done");

                if (File.Exists(markerFile))
                {
                    // Already handled (either succeeded or the user
                    // declined) on a previous run — don't ask again.
                    return;
                }

                // Build the netsh commands that open the two ports.
                // Using netsh directly (no extra script file needed) so
                // this works even before the app has written anything
                // else to disk.
                string netshArgs =
                    "/c " +
                    "netsh advfirewall firewall add rule name=\"Envialo - File Transfer (TCP 9000)\" dir=in action=allow protocol=TCP localport=9000 && " +
                    "netsh advfirewall firewall add rule name=\"Envialo - Discovery (UDP 9001)\" dir=in action=allow protocol=UDP localport=9001";

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = netshArgs,
                    UseShellExecute = true,
                    Verb = "runas", // triggers the UAC elevation prompt
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };

                try
                {
                    using (Process process = Process.Start(startInfo))
                    {
                        process.WaitForExit();

                        // Whether it succeeded or the user declined the
                        // UAC prompt, mark this as "handled" so we don't
                        // interrupt every single launch with a prompt.
                        File.WriteAllText(markerFile, DateTime.Now.ToString());
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Thrown when the user clicks "No" on the UAC prompt.
                    // Still mark as handled so we don't nag on every launch —
                    // worth documenting for IT that manual setup is needed
                    // on this PC (see setup-firewall.ps1).
                    File.WriteAllText(markerFile, "declined " + DateTime.Now.ToString());
                }
            }
            catch
            {
                // Never let firewall setup problems prevent the app itself
                // from starting.
            }
        }
    }
}
