using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;

namespace Finley {
    internal static class Maintenance {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateService(IntPtr manager, string name, string display, uint access, uint type, uint start, uint error, string command, string group, IntPtr tag, string dependencies, string account, string password);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint error, string command, string group, IntPtr tag, string dependencies, string account, string password, string display);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetServiceObjectSecurity(IntPtr service, uint information, byte[] descriptor);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool DeleteService(IntPtr service);
        [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);
        internal static IntPtr OpenExistingService(IntPtr manager, string name) {
            IntPtr service = OpenService(manager, name, 0xF01FF);
            // Capture immediately: subsequent file/ACL APIs overwrite the cached error.
            int error = service == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            if (service == IntPtr.Zero && error != 1060) throw new Win32Exception(error);
            return service;
        }
        internal static int Run(string command) {
            string operation = "validating the installation";
            try {
                using (var identity = WindowsIdentity.GetCurrent()) {
                    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidOperationException("Open an administrator terminal to install, repair, stop, or remove the Finley service.");
                }
                if (command == "--prepare-install") { PrepareInstall(); return 0; }
                string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "finley-tunnel-service.exe");
                WindowsSecurity.VerifyProtectedFile(executable);
                if (command != "--install" && command != "--stop" && command != "--uninstall" && command != "--acknowledge-cleanup") throw new InvalidOperationException("Unknown service maintenance command.");
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Wire.Service)) {
                    if (key != null && !String.Equals((string)key.GetValue("ImagePath"), "\"" + executable + "\"", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A Finley service is installed at another location. Uninstall that service before changing installations.");
                }
                bool exists = false;
                foreach (ServiceController installed in ServiceController.GetServices()) { using (installed) { if (installed.ServiceName == Wire.Service) exists = true; } }
                if (exists) Stop();
                if (command == "--stop") { VerifyClean(); return 0; }
                if (command == "--acknowledge-cleanup") {
                    // Explicit administrator recovery after inspecting/restoring networking.
                    // No routes or DNS settings are modified by this command.
                    VerifyClean(false);
                    string baseline = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "finley-network-baseline.json");
                    if (File.Exists(baseline)) { WindowsSecurity.VerifyProtectedFile(baseline); File.Delete(baseline); }
                    if (exists) { using (var controller = new ServiceController(Wire.Service)) { controller.Start(); controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); } }
                    return 0;
                }
                operation = "opening Windows Service Control Manager";
                IntPtr manager = OpenSCManager(null, null, 3), service = IntPtr.Zero;
                if (manager == IntPtr.Zero) throw new Win32Exception();
                bool created = false;
                try {
                    operation = "looking up the existing service";
                    service = OpenExistingService(manager, Wire.Service);
                    if (command == "--uninstall") {
                        VerifyClean();
                        operation = "removing the service";
                        if (service != IntPtr.Zero && !DeleteService(service)) throw new Win32Exception();
                    } else {
                        operation = "validating protected service files";
                        WindowsSecurity.VerifyProtectedFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sing-box.exe"));
                        WindowsSecurity.VerifyProtectedFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "finley-tunnel-helper.exe"));
                        WindowsSecurity.VerifyProtectedFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "finley-service-client.exe"));
                        if (service == IntPtr.Zero) {
                            operation = "creating the Windows service";
                            service = CreateService(manager, Wire.Service, "Finley Tunnel Service", 0xF01FF, 0x10, 2, 1, "\"" + executable + "\"", null, IntPtr.Zero, null, "LocalSystem", null);
                            if (service == IntPtr.Zero) throw new Win32Exception(); created = true;
                        } else {
                            operation = "updating the service configuration";
                            if (!ChangeServiceConfig(service, 0x10, 2, 1, "\"" + executable + "\"", null, IntPtr.Zero, null, "LocalSystem", null, "Finley Tunnel Service")) throw new Win32Exception();
                        }
                        operation = "setting service permissions";
                        var acl = new RawSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;CCLCRC;;;IU)");
                        var bytes = new byte[acl.BinaryLength]; acl.GetBinaryForm(bytes, 0);
                        if (!SetServiceObjectSecurity(service, 4, bytes)) throw new Win32Exception();
                        operation = "starting the Windows service";
                        using (var controller = new ServiceController(Wire.Service)) { controller.Start(); controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
                        operation = "checking network cleanup";
                        VerifyClean();
                    }
                } catch {
                    try { Stop(); } catch { }
                    if (created && service != IntPtr.Zero) DeleteService(service);
                    throw;
                } finally { if (service != IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); }
                if (command == "--uninstall") {
                    DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                    while (true) {
                        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Wire.Service)) { if (key == null) break; }
                        if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("Finley service is marked for deletion. Restart Windows before reinstalling.");
                        Thread.Sleep(100);
                    }
                }
                return 0;
            } catch (Exception error) {
                Win32Exception native = error as Win32Exception ?? error.InnerException as Win32Exception;
                Console.Error.WriteLine(native != null ? "Finley service setup failed while " + operation + " (Windows " + native.NativeErrorCode + ": " + native.Message + ")." : error.Message);
                return 1;
            }
        }
        static void PrepareInstall() {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Finley");
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Wire.Service)) {
                string expected = "\"" + Path.Combine(root, @"resources\bin\finley-tunnel-service.exe") + "\"";
                if (key != null && !String.Equals((string)key.GetValue("ImagePath"), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Uninstall the Finley service at its other location before installing this build.");
            }
            // Validate BEFORE NSIS writes into an existing installation. Never traverse
            // a reparse point or overwrite a tree controlled by an ordinary user.
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);
            var pending = new System.Collections.Generic.Stack<string>(); pending.Push(root);
            while (pending.Count > 0) {
                string directory = pending.Pop(); WindowsSecurity.VerifyProtectedDirectory(directory);
                foreach (string path in Directory.GetFileSystemEntries(directory)) {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Unsafe reparse point in the Finley installation. Ask an administrator to repair the installation directory.");
                    if (Directory.Exists(path)) pending.Push(path); else WindowsSecurity.VerifyProtectedFile(path);
                }
            }
        }
        static void Stop() {
            using (var controller = new ServiceController(Wire.Service)) {
                controller.Refresh();
                if (controller.Status != ServiceControllerStatus.Stopped && controller.Status != ServiceControllerStatus.StopPending) controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        static void VerifyClean(bool compareBaseline = true) {
            using (var timeout = new CancellationTokenSource(20000)) {
                if (!NetworkChecks.NoTunnel(timeout.Token)) throw new InvalidOperationException("Finley networking cleanup is unconfirmed. Check Windows network settings before repairing or uninstalling.");
                string baseline = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "finley-network-baseline.json");
                if (compareBaseline && File.Exists(baseline)) {
                    WindowsSecurity.VerifyProtectedFile(baseline);
                    if (new FileInfo(baseline).Length > 65536 || File.ReadAllText(baseline) != NetworkChecks.Baseline(timeout.Token)) throw new InvalidOperationException("Windows networking differs from the interrupted Finley session. Restore/check networking, then explicitly run --acknowledge-cleanup as administrator.");
                }
            }
        }
    }
}
