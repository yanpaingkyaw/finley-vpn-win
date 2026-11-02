using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Finley;

internal static class ServiceHarness {
    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenSCManager(string machine, string database, uint access);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [System.Runtime.InteropServices.DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)] static extern uint GetFileAttributes(string path);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder path, ref int size);
    static void ProcessQueryTests() {
        var descriptor = WindowsSecurity.ServiceProcessDescriptor();
        var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
        int interactiveMask = 0;
        foreach (System.Security.AccessControl.GenericAce ace in descriptor.DiscretionaryAcl) {
            var entry = ace as System.Security.AccessControl.CommonAce;
            if (entry != null && entry.SecurityIdentifier == interactive) interactiveMask |= entry.AccessMask;
        }
        Assert(interactiveMask == 0x1000, "Interactive process rights exceed limited query access.");
        using (var child = new Process { StartInfo = new ProcessStartInfo {
            FileName = System.Reflection.Assembly.GetExecutingAssembly().Location, Arguments = "--process-query-child",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        } }) {
            child.Start();
            try {
                var ready = child.StandardOutput.ReadLineAsync(); Assert(ready.Wait(5000) && ready.Result == "READY", "Restricted process did not start.");
                IntPtr handle = OpenProcess(0x1000, false, (uint)child.Id);
                Assert(handle != IntPtr.Zero, "Interactive client could not open limited query handle.");
                try {
                    var name = new System.Text.StringBuilder(32768); int size = name.Capacity;
                    Assert(QueryFullProcessImageName(handle, 0, name, ref size), "Limited query could not read executable path.");
                    Assert(String.Equals(name.ToString(), child.StartInfo.FileName, StringComparison.OrdinalIgnoreCase), "Queried executable path mismatch.");
                } finally { WindowsSecurity.CloseHandle(handle); }
                using (var identity = WindowsIdentity.GetCurrent()) {
                    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) {
                        foreach (uint access in new uint[] { 1, 2, 8, 16, 32, 64, 0x200, 0x400, 0x800 }) {
                            handle = OpenProcess(access, false, (uint)child.Id);
                            if (handle != IntPtr.Zero) WindowsSecurity.CloseHandle(handle);
                            Assert(handle == IntPtr.Zero, "Interactive client gained process modification/termination/memory access.");
                        }
                    }
                }
                child.StandardInput.Close(); Assert(child.WaitForExit(5000) && child.ExitCode == 0, "Restricted process did not stop.");
            } finally { if (!child.HasExited) child.Kill(); }
        }
    }
    static void ServiceLookupTests() {
        IntPtr manager = OpenSCManager(null, null, 1);
        Assert(manager != IntPtr.Zero, "Could not query Service Control Manager.");
        try {
            string missing = "Finley.Test.Missing." + Guid.NewGuid().ToString("N");
            IntPtr original = OpenService(manager, missing, 0xF01FF);
            int originalError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Assert(original == IntPtr.Zero && originalError == 1060, "Expected a genuinely missing Windows service.");
            int? afterPermissionChecks = null;
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Finley\resources\bin");
            if (File.Exists(Path.Combine(installed, "finley-tunnel-service.exe"))) {
                try {
                    foreach (string file in new[] { "sing-box.exe", "finley-tunnel-helper.exe", "finley-service-client.exe" }) WindowsSecurity.VerifyProtectedFile(Path.Combine(installed, file));
                    afterPermissionChecks = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                } catch (Fault) { }
            }
            // Reproduce the original install sequence: a later filesystem operation
            // replaces the error that the old registration branch tested too late.
            GetFileAttributes(@"C:\FinleyMissingDirectory-" + Guid.NewGuid().ToString("N") + @"\missing.exe");
            int changedError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Assert(changedError != originalError, "Filesystem call did not overwrite the error.");
            Assert(Maintenance.OpenExistingService(manager, missing) == IntPtr.Zero, "Missing service was not accepted for creation.");
            bool denied = false;
            try { Maintenance.OpenExistingService(IntPtr.Zero, missing); } catch (System.ComponentModel.Win32Exception e) { denied = e.NativeErrorCode == 6; }
            Assert(denied, "Invalid SCM handle was mistaken for an absent service.");
            Console.WriteLine(Json.Encode(Json.Obj("missingServiceError", originalError, "afterPermissionChecks", afterPermissionChecks, "filesystemError", changedError, "missingServiceAccepted", true)));
        } finally { CloseServiceHandle(manager); }
    }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static NamedPipeClientStream Client(string name) { return new NamedPipeClientStream(".", name, (PipeAccessRights)0x12019b, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None); }
    static void PipeTests() {
        string name = "Finley.Test." + Guid.NewGuid().ToString("N");
        using (var server = WindowsSecurity.CreatePipe(true, name)) {
            bool refused = false;
            try { using (var duplicate = WindowsSecurity.CreatePipe(true, name)) { } } catch { refused = true; }
            Assert(refused, "Pipe squatting/first-instance check failed.");
            var wait = Task.Run(() => server.WaitForConnection());
            using (var client = Client(name)) {
                client.Connect(3000); wait.Wait();
                Wire.Write(client, Json.Obj("test", "identity")); Wire.Read(server, 3000);
                var caller = WindowsSecurity.Authenticate(server);
                using (var identity = WindowsIdentity.GetCurrent()) Assert(caller.Sid == identity.User.Value && caller.Session == Process.GetCurrentProcess().SessionId, "Caller identity mismatch.");
                refused = false; try { WindowsSecurity.VerifyServer(client); } catch { refused = true; }
                Assert(refused, "A fake service was accepted.");
            }
        }
        name = "Finley.Test." + Guid.NewGuid().ToString("N");
        using (var server = WindowsSecurity.CreatePipe(true, name)) {
            // An interactive user must not be able to create another server instance.
            // Elevated administrators intentionally retain this maintenance right.
            using (var identity = WindowsIdentity.GetCurrent()) {
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) {
                    bool refused = false;
                    try { using (var rogue = WindowsSecurity.CreatePipe(false, name)) { } } catch { refused = true; }
                    Assert(refused, "Ordinary user created a pipe server instance.");
                }
            }
        }
        name = "Finley.Test." + Guid.NewGuid().ToString("N");
        using (var server = WindowsSecurity.CreatePipe(true, name)) {
            var tunnel = new TunnelController(); Connection connection = null;
            var worker = Task.Run(() => { server.WaitForConnection(); connection = new Connection(server, tunnel, _ => { }); connection.Run(); });
            using (var client = Client(name)) {
                client.Connect(3000);
                Wire.Write(client, Json.Obj("version", 1, "id", "hello", "method", "hello"));
                Assert((bool)Wire.Read(client, 5000)["ok"], "Handshake failed.");
                Wire.Write(client, Json.Obj("version", 1, "id", "bad", "method", "connect", "profile", Json.Obj("server", "example.com", "port", 70000, "method", "aes-256-gcm", "password", "test-only"), "bypass", Json.Obj("enabled", false, "paths", new string[0], "domains", new string[0], "cidrs", new string[0])));
                var response = Wire.Read(client, 5000);
                Assert(!(bool)response["ok"] && (string)Json.Map(response["error"])["code"] == "INVALID_REQUEST", "Invalid connection request was accepted.");
                Wire.Write(client, Json.Obj("version", 1, "id", "status", "method", "getStatus"));
                Assert((string)Json.Map(Wire.Read(client, 5000)["value"])["state"] == "disconnected", "Invalid request changed lifecycle state.");
            }
            Assert(worker.Wait(5000), "Connection did not close on client EOF.");
        }
    }
    static void JobTest(string helper, string fake, string marker) {
        using (var job = new ProcessJob()) using (var process = new Process { StartInfo = new ProcessStartInfo { FileName = helper, Arguments = "\"" + fake + "\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true } }) {
            process.Start(); job.Assign(process); process.StandardInput.WriteLine(marker); process.StandardInput.Flush();
            DateTime limit = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(marker + ".pid")) { if (DateTime.UtcNow >= limit) throw new Exception("Fake engine did not start."); Thread.Sleep(25); }
            int pid = Int32.Parse(File.ReadAllText(marker + ".pid"));
            using (var engine = Process.GetProcessById(pid)) {
                // Killing only the console helper must not allow its engine to escape the job.
                process.Kill(); process.WaitForExit(2000); job.Drain();
                Assert(engine.WaitForExit(2000), "Job left an orphan engine.");
            }
        }
    }
    sealed class FakeBackend : ITunnelBackend {
        internal readonly ManualResetEventSlim Started = new ManualResetEventSlim(false), Allow = new ManualResetEventSlim(false);
        internal int Cleanups; internal bool Clean = true, IgnoreCancellation;
        public event Action Exited = delegate { };
        public bool HasExited { get; private set; }
        public void Connect(object profile, object bypass, CancellationToken cancel) {
            Started.Set(); if (IgnoreCancellation) Allow.Wait(); else Allow.Wait(cancel);
        }
        public bool Cleanup() { Interlocked.Increment(ref Cleanups); HasExited = true; return Clean; }
        public void Abort() { HasExited = true; }
        internal void Crash() { HasExited = true; Exited(); }
    }
    static string State(TunnelController controller, object owner) { return (string)Json.Map(controller.Status(owner))["state"]; }
    static void LifecycleTests() {
        object profile = Json.Obj("server", "vpn.example", "port", (decimal)443, "method", "aes-256-gcm", "password", "test-only");
        object bypass = Json.Obj("enabled", false, "paths", new object[0], "domains", new object[0], "cidrs", new object[0]);
        var caller = new WindowsSecurity.Caller { Sid = "test-owner", Logon = "test-logon", Session = 1 };
        object owner = new object(), other = new object();
        var first = new FakeBackend(); var second = new FakeBackend { IgnoreCancellation = true }; var third = new FakeBackend { Clean = false };
        var backends = new Queue<ITunnelBackend>(new ITunnelBackend[] { first, second, third });
        using (var controller = new TunnelController(() => backends.Dequeue())) {
            var revisions = new List<decimal>();
            controller.Changed += () => { lock (revisions) revisions.Add(Convert.ToDecimal(Json.Map(controller.Status(owner))["revision"])); };
            var connecting = controller.Connect(owner, caller, profile, bypass);
            Assert(first.Started.Wait(3000), "Connect did not start.");
            Assert(State(controller, owner) == "connecting" && State(controller, other) == "busy", "Ownership was not isolated.");
            bool rejected = false; try { controller.Connect(other, caller, profile, bypass); } catch (Fault e) { rejected = e.Code == "VPN_IN_USE"; }
            Assert(rejected, "Concurrent connection was accepted.");
            rejected = false; try { controller.Disconnect(other); } catch (Fault e) { rejected = e.Code == "VPN_IN_USE"; }
            Assert(rejected, "Another session disconnected the owner.");
            first.Allow.Set(); Assert(connecting.Wait(3000), "Connect did not finish.");
            Assert(State(controller, owner) == "connected", "Connected state was lost.");
            controller.Release(other, false); controller.Logoff(2);
            Assert(State(controller, owner) == "connected", "Non-owner loss stopped the tunnel.");
            first.Crash(); Assert(SpinWait.SpinUntil(() => State(controller, owner) == "error", 3000), "Unexpected engine exit was not reported.");
            Assert(first.Cleanups == 1 && (bool)Json.Map(controller.Status(owner))["cleanupConfirmed"], "Unexpected exit cleanup was not confirmed.");
            connecting = controller.Connect(owner, caller, profile, bypass); Assert(second.Started.Wait(3000), "Second connect did not start.");
            var disconnect = controller.Disconnect(owner);
            Assert(Object.ReferenceEquals(disconnect, controller.Disconnect(owner)), "Repeated disconnect did not share the operation.");
            second.Allow.Set(); // Deliberately complete startup AFTER cancellation.
            try { connecting.Wait(3000); throw new Exception("Stale connect succeeded."); } catch (AggregateException e) { Assert(e.GetBaseException() is Fault && ((Fault)e.GetBaseException()).Code == "CANCELLED", "Incorrect cancellation error."); }
            Assert(disconnect.Wait(3000), "Disconnect did not finish.");
            Assert(State(controller, owner) == "disconnected" && second.Cleanups == 1, "Cancelled connect leaked state or duplicated cleanup.");
            third.Allow.Set(); connecting = controller.Connect(owner, caller, profile, bypass); connecting.Wait(3000);
            disconnect = controller.Disconnect(owner);
            try { disconnect.Wait(3000); throw new Exception("Failed cleanup was reported as successful."); } catch (AggregateException e) { Assert(((Fault)e.GetBaseException()).Code == "CLEANUP_UNCONFIRMED", "Incorrect cleanup fault."); }
            rejected = false; try { controller.Connect(owner, caller, profile, bypass); } catch (Fault e) { rejected = e.Code == "CLEANUP_UNCONFIRMED"; }
            Assert(rejected && !(bool)Json.Map(controller.Status(owner))["cleanupConfirmed"], "Unconfirmed cleanup allowed reconnection.");
            lock (revisions) for (int i = 1; i < revisions.Count; i++) Assert(revisions[i] >= revisions[i - 1], "Status revision moved backward.");
        }
        var logoff = new FakeBackend(); logoff.Allow.Set();
        using (var controller = new TunnelController(() => logoff)) {
            controller.Connect(owner, caller, profile, bypass).Wait(3000); controller.Logoff(1);
            Assert(SpinWait.SpinUntil(() => State(controller, owner) == "disconnected", 3000) && logoff.Cleanups == 1, "Owner logoff did not stop the tunnel.");
        }
    }
    static int Main(string[] args) {
        try {
            Console.InputEncoding = new System.Text.UTF8Encoding(false, true);
            Console.OutputEncoding = new System.Text.UTF8Encoding(false, true);
            if (args.Length == 1 && args[0] == "--process-query-child") {
                WindowsSecurity.SetCurrentProcessSecurity(WindowsSecurity.ServiceProcessDescriptor());
                Console.WriteLine("READY"); Console.Out.Flush(); Console.ReadLine(); return 0;
            }
            if (args.Length == 1 && args[0] == "--process-query") { ProcessQueryTests(); Console.WriteLine("Restricted process query tests passed."); return 0; }
            // Read-only protocol diagnostic: no credentials or tunnel commands are sent.
            if (args.Length == 1 && args[0] == "--installed-handshake") {
                WindowsSecurity.EnsureServiceRunning();
                using (var pipe = Client(Wire.Pipe)) {
                    pipe.Connect(3000); Wire.Write(pipe, Json.Obj("version", 1, "id", "diagnose", "method", "hello"));
                    Console.WriteLine(Json.Encode(Wire.Read(pipe, 5000)));
                }
                return 0;
            }
            if (args.Length == 1 && args[0] == "--verify-installed-service") {
                WindowsSecurity.EnsureServiceRunning();
                using (var pipe = Client(Wire.Pipe)) {
                    pipe.Connect(3000);
                    WindowsSecurity.VerifyServer(pipe, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Finley\resources\bin\finley-tunnel-service.exe"));
                    Wire.Write(pipe, Json.Obj("version", 1, "id", "verify", "method", "hello"));
                    var response = Wire.Read(pipe, 5000);
                    if (!(bool)response["ok"]) throw new Exception("Service handshake rejected.");
                    Console.WriteLine("Installed service identity and authenticated handshake passed.");
                }
                return 0;
            }
            if (args.Length == 1 && args[0] == "--service-lookup") { ServiceLookupTests(); return 0; }
            if (args.Length == 1 && args[0] == "--pipes") { PipeTests(); Console.WriteLine("pipe tests passed"); return 0; }
            if (args.Length == 1 && args[0] == "--lifecycle") { LifecycleTests(); Console.WriteLine("lifecycle tests passed"); return 0; }
            if (args.Length == 4 && args[0] == "--job") { JobTest(args[1], args[2], args[3]); Console.WriteLine("job tests passed"); return 0; }
            string text = Console.In.ReadToEnd(); var request = Json.Map(Json.Decode(text));
            string operation = Json.Text(request, "operation", 32);
            if (operation == "config") Console.WriteLine(Json.Encode(Configuration.Build(Json.Get(request, "profile"), Json.Get(request, "bypass"), request.ContainsKey("directInterface") ? (string)request["directInterface"] : null)));
            else if (operation == "normalize") Console.WriteLine(Json.Encode(Configuration.NormalizeBypass(Json.Get(request, "bypass"))));
            else if (operation == "parse") Console.WriteLine(Json.Encode(request));
            else throw Json.Invalid();
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error is Fault ? ((Fault)error).Code + ": " + error.Message : error.Message); return 1; }
    }
}
