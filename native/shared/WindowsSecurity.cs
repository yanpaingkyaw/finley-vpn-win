using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Finley {
    internal static class WindowsSecurity {
        [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafePipeHandle CreateNamedPipe(string name, uint mode, uint pipeMode, uint instances, uint outSize, uint inSize, uint timeout, ref SecurityAttributes security);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int kind, out int value, int size, out int returned);
        [StructLayout(LayoutKind.Sequential)] struct Luid { internal uint Low; internal int High; }
        [StructLayout(LayoutKind.Sequential)] struct TokenStatistics { internal Luid TokenId, AuthenticationId; internal long Expiration; internal int Type, Impersonation; internal uint DynamicCharged, DynamicAvailable, GroupCount, PrivilegeCount; internal Luid Modified; }
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int kind, out TokenStatistics value, int size, out int returned);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
        [StructLayout(LayoutKind.Sequential)] struct ServiceConfig { internal uint Type, Start, Error; internal IntPtr Binary, Group; internal uint Tag; internal IntPtr Dependencies, Account, Display; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryServiceConfig(IntPtr service, IntPtr config, int size, out int needed);
        [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetKernelObjectSecurity(IntPtr handle, uint information, byte[] descriptor);
        [StructLayout(LayoutKind.Sequential)] struct ServiceStatus { internal uint Type, State, Controls, ExitCode, SpecificExit, Checkpoint, WaitHint, Pid, Flags; }

        internal static void ConfigureServiceProcessSecurity() {
            using (var identity = WindowsIdentity.GetCurrent()) {
                if (!identity.IsSystem) throw Unauthorized();
            }
            SetCurrentProcessSecurity(ServiceProcessDescriptor());
        }
        internal static RawSecurityDescriptor ServiceProcessDescriptor() {
            // Read only the executable path and retain a PID-reuse-safe process handle.
            // Do not grant VM read/write, termination, or handle duplication.
            return new RawSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x1000;;;IU)");
        }
        internal static void SetCurrentProcessSecurity(RawSecurityDescriptor descriptor) {
            byte[] bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
            if (!SetKernelObjectSecurity(GetCurrentProcess(), 4, bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        internal static NamedPipeServerStream CreatePipe(bool first) { return CreatePipe(first, Wire.Pipe); }
        internal static NamedPipeServerStream CreatePipe(bool first, string pipeName) {
            IntPtr sd; uint size;
            // 0x12019b: read/write data and attributes + synchronize, WITHOUT create-instance (0x4).
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x12019b;;;IU)", 1, out sd, out size)) throw new Win32Exception();
            try {
                var sa = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = sd };
                SafePipeHandle handle = CreateNamedPipe(@"\\.\pipe\" + pipeName, 0x40000003u | (first ? 0x80000u : 0), 8, 16, 65536, 65536, 0, ref sa);
                if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
                return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
            } finally { LocalFree(sd); }
        }
        internal sealed class Caller {
            internal string Sid, Logon; internal int Session;
        }
        internal static Caller Authenticate(NamedPipeServerStream pipe) {
            Caller caller = null; uint pid, pipeSession;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid) || !GetNamedPipeClientSessionId(pipe.SafePipeHandle, out pipeSession) || pipeSession == 0) throw Unauthorized();
            pipe.RunAsClient(() => {
                using (var identity = WindowsIdentity.GetCurrent(true)) {
                    if (identity == null || identity.User == null || identity.IsAnonymous || identity.IsSystem || !identity.IsAuthenticated) throw Unauthorized();
                    bool interactive = false;
                    foreach (IdentityReference group in identity.Groups) { string sid = group.Value; if (sid == "S-1-5-4") interactive = true; if (sid == "S-1-5-2") throw Unauthorized(); }
                    int session, returned;
                    TokenStatistics statistics;
                    if (!interactive || !GetTokenInformation(identity.Token, 12, out session, 4, out returned) || session != pipeSession || !GetTokenInformation(identity.Token, 10, out statistics, Marshal.SizeOf(typeof(TokenStatistics)), out returned)) throw Unauthorized();
                    // AuthenticationId is the kernel's logon-session LUID. Unlike Groups,
                    // it is available even when the token exposes no S-1-5-5-* logon SID.
                    caller = new Caller { Sid = identity.User.Value, Logon = statistics.AuthenticationId.High + ":" + statistics.AuthenticationId.Low, Session = session };
                }
            });
            using (var process = Process.GetProcessById((int)pid)) { if (process.SessionId != caller.Session) throw Unauthorized(); }
            return caller;
        }
        internal static Fault Unauthorized() { return new Fault("UNAUTHORIZED", "This Windows session cannot use the Finley service."); }
        internal static void EnsureServiceRunning() {
            IntPtr manager = OpenSCManager(null, null, 1), service = IntPtr.Zero;
            try {
                if (manager == IntPtr.Zero) throw Unauthorized();
                service = OpenService(manager, Wire.Service, 4);
                if (service == IntPtr.Zero) {
                    if (Marshal.GetLastWin32Error() == 1060) throw new Fault("SERVICE_MISSING", "Finley Tunnel Service is not installed. Ask an administrator to install or repair Finley.");
                    throw Unauthorized();
                }
                ServiceStatus status; int needed;
                if (!QueryServiceStatusEx(service, 0, out status, Marshal.SizeOf(typeof(ServiceStatus)), out needed)) throw Unauthorized();
                if (status.State != 4) throw new Fault("SERVICE_STOPPED", "Finley Tunnel Service is stopped or starting. Wait a moment, or ask an administrator to start or repair it.");
            } finally { if (service != IntPtr.Zero) CloseServiceHandle(service); if (manager != IntPtr.Zero) CloseServiceHandle(manager); }
        }
        static Fault VerificationFailed(string step, int error = 0) { return new Fault("SERVICE_IDENTITY_FAILED", "Finley could not verify its Windows service (" + step + (error == 0 ? "" : ", Windows " + error) + "). Ask an administrator to repair Finley."); }
        internal static void VerifyServer(NamedPipeClientStream pipe) { VerifyServer(pipe, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "finley-tunnel-service.exe")); }
        internal static void VerifyServer(NamedPipeClientStream pipe, string expected) {
            uint pid; if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid)) throw VerificationFailed("pipe server PID", Marshal.GetLastWin32Error());
            IntPtr process = IntPtr.Zero, manager = IntPtr.Zero, service = IntPtr.Zero, configuration = IntPtr.Zero;
            try {
                manager = OpenSCManager(null, null, 1); if (manager == IntPtr.Zero) throw VerificationFailed("SCM query access", Marshal.GetLastWin32Error());
                service = OpenService(manager, Wire.Service, 5); ServiceStatus status; int needed;
                if (service == IntPtr.Zero) throw VerificationFailed("service query access", Marshal.GetLastWin32Error());
                if (!QueryServiceStatusEx(service, 0, out status, Marshal.SizeOf(typeof(ServiceStatus)), out needed)) throw VerificationFailed("service status", Marshal.GetLastWin32Error());
                if (status.State != 4 || status.Pid != pid) throw VerificationFailed("service PID mismatch");
                QueryServiceConfig(service, IntPtr.Zero, 0, out needed);
                if (needed <= 0 || needed > 65536) throw VerificationFailed("configuration size", Marshal.GetLastWin32Error());
                configuration = Marshal.AllocHGlobal(needed);
                if (!QueryServiceConfig(service, configuration, needed, out needed)) throw VerificationFailed("service configuration", Marshal.GetLastWin32Error());
                var config = (ServiceConfig)Marshal.PtrToStructure(configuration, typeof(ServiceConfig));
                // SCM's protected configuration establishes the service account; querying
                // a LocalSystem process token itself can require privileges clients lack.
                if (Marshal.PtrToStringUni(config.Account) != "LocalSystem" || config.Type != 0x10) throw VerificationFailed("service account/type");
                if (!String.Equals(Marshal.PtrToStringUni(config.Binary), "\"" + expected + "\"", StringComparison.OrdinalIgnoreCase)) throw VerificationFailed("configured executable path mismatch");
                VerifyProtectedFile(expected);
                process = OpenProcess(0x1000, false, pid);
                if (process == IntPtr.Zero) throw VerificationFailed("process query access", Marshal.GetLastWin32Error());
                var name = new StringBuilder(32768); int length = name.Capacity;
                if (!QueryFullProcessImageName(process, 0, name, ref length)) throw VerificationFailed("process image path", Marshal.GetLastWin32Error());
                if (!String.Equals(Path.GetFullPath(name.ToString()), expected, StringComparison.OrdinalIgnoreCase)) throw VerificationFailed("executable path mismatch");
            } finally { if (configuration != IntPtr.Zero) Marshal.FreeHGlobal(configuration); if (service != IntPtr.Zero) CloseServiceHandle(service); if (manager != IntPtr.Zero) CloseServiceHandle(manager); if (process != IntPtr.Zero) CloseHandle(process); }
        }
        static bool Trusted(string sid) { return sid == "S-1-5-18" || sid == "S-1-5-32-544" || sid == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"; }
        internal static void VerifyProtectedFile(string file) {
            VerifyProtectedPath(file, false);
        }
        internal static void VerifyProtectedDirectory(string directory) {
            VerifyProtectedPath(directory, true);
        }
        static void VerifyProtectedPath(string file, bool directory) {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(file);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !(directory ? Directory.Exists(full) : File.Exists(full))) throw new Fault("UNSAFE_INSTALL", "Reinstall Finley under Program Files with administrator approval.");
            string current = full;
            while (current.Length >= root.Length - 1) {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Unauthorized();
                FileSystemSecurity acl = Directory.Exists(current) ? (FileSystemSecurity)Directory.GetAccessControl(current) : File.GetAccessControl(current);
                if (!Trusted(acl.GetOwner(typeof(SecurityIdentifier)).Value)) throw Unauthorized();
                foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier))) {
                    if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 || Trusted(rule.IdentityReference.Value)) continue;
                    const FileSystemRights writes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                    if ((rule.FileSystemRights & writes) != 0) throw new Fault("UNSAFE_INSTALL", "Finley service files are writable by ordinary users. Ask an administrator to reinstall Finley.");
                }
                current = Path.GetDirectoryName(current); if (current == null) break;
            }
        }
    }
}
