using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Finley {
    internal interface ITunnelBackend {
        event Action Exited;
        bool HasExited { get; }
        void Connect(object profile, object bypass, CancellationToken cancel);
        bool Cleanup();
        void Abort();
    }
    internal sealed class NativeTunnelBackend : ITunnelBackend {
        static readonly string directory = AppDomain.CurrentDomain.BaseDirectory;
        static string BaselineFile { get { return Path.Combine(directory, "finley-network-baseline.json"); } }
        string baseline;
        Process child;
        ProcessJob job;
        volatile bool helperCleanupFault;
        public event Action Exited = delegate { };
        public bool HasExited { get { return child == null || child.HasExited; } }
        internal static bool Initialize() {
            WindowsSecurity.VerifyProtectedFile(Path.Combine(directory, "finley-tunnel-service.exe"));
            WindowsSecurity.VerifyProtectedFile(Path.Combine(directory, "finley-tunnel-helper.exe"));
            WindowsSecurity.VerifyProtectedFile(Path.Combine(directory, "sing-box.exe"));
            try {
                using (var timeout = new CancellationTokenSource(20000)) {
                    if (!NetworkChecks.NoTunnel(timeout.Token)) { return false; }
                    if (File.Exists(BaselineFile)) {
                        WindowsSecurity.VerifyProtectedFile(BaselineFile);
                        if (new FileInfo(BaselineFile).Length > 65536 || File.ReadAllText(BaselineFile) != NetworkChecks.Baseline(timeout.Token)) { return false; }
                        File.Delete(BaselineFile);
                    }
                }
            }
            catch { return false; }
            return true;
        }
        public void Connect(object profile, object bypass, CancellationToken cancel) {
            var config = Configuration.Build(profile, bypass, null);
            var safe = Configuration.NormalizeBypass(bypass);
            baseline = NetworkChecks.Baseline(cancel);
            SaveBaseline();
            if ((bool)safe["enabled"] && new[] { "paths", "domains", "cidrs" }.Any(key => ((string[])safe[key]).Length > 0)) {
                string direct = NetworkChecks.DefaultInterface(cancel);
                var outbounds = (object[])config["outbounds"]; Json.Map(outbounds[1])["bind_interface"] = direct;
            }
            string name = "Finley-" + NetworkChecks.Secret().Substring(0, 8);
            int port = NetworkChecks.Port(); string user = NetworkChecks.Secret(), password = NetworkChecks.Secret();
            var inbounds = (List<object>)config["inbounds"]; Json.Map(inbounds[0])["interface_name"] = name;
            inbounds.Add(Json.Obj("type", "socks", "tag", "probe", "listen", "127.0.0.1", "listen_port", port, "users", new[] { Json.Obj("username", user, "password", password) }));
            ((List<object>)Json.Map(config["route"])["rules"]).Insert(0, Json.Obj("inbound", "probe", "outbound", "proxy"));
            StartHelper(config, cancel); config = null;
            bool verified = false;
            for (int i = 0; i < 12; i++) {
                cancel.ThrowIfCancellationRequested(); if (child.HasExited) throw new Fault("ENGINE_EXIT", "The VPN engine stopped during connection.");
                try { NetworkChecks.Probe(port, user, password, cancel); verified = true; break; }
                catch { cancel.ThrowIfCancellationRequested(); if (cancel.WaitHandle.WaitOne(500)) cancel.ThrowIfCancellationRequested(); }
            }
            if (!verified) throw new Fault("CONNECTIVITY_FAILED", "Could not verify the VPN connection. Check the access key, server, and network.");
            NetworkChecks.Routes(name, cancel); cancel.ThrowIfCancellationRequested();
            if (child.HasExited) throw new Fault("ENGINE_EXIT", "The VPN engine stopped during connection.");
        }
        void SaveBaseline() {
            string temporary = BaselineFile + ".tmp";
            if (File.Exists(temporary)) { WindowsSecurity.VerifyProtectedFile(temporary); File.Delete(temporary); }
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(baseline);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            File.Move(temporary, BaselineFile);
        }
        void StartHelper(object config, CancellationToken cancel) {
            string helper = Path.Combine(directory, "finley-tunnel-helper.exe"), engine = Path.Combine(directory, "sing-box.exe");
            WindowsSecurity.VerifyProtectedFile(helper); WindowsSecurity.VerifyProtectedFile(engine);
            job = new ProcessJob(); helperCleanupFault = false;
            child = new Process { StartInfo = new ProcessStartInfo { FileName = helper, Arguments = "\"" + engine + "\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory }, EnableRaisingEvents = true };
            child.Exited += (_, e) => Exited();
            var ready = new ManualResetEventSlim(false);
            child.OutputDataReceived += (_, e) => { if (e.Data == "READY") ready.Set(); if (e.Data == "CLEANUP_UNCONFIRMED") helperCleanupFault = true; };
            child.ErrorDataReceived += (_, e) => { };
            if (!child.Start()) throw new Fault("STARTUP_FAILED", "Could not start the Windows tunnel helper.");
            // The helper is inert until its first configuration line. Assign BEFORE sending it,
            // eliminating the process-start/child-spawn race without changing console behavior.
            job.Assign(child); child.BeginOutputReadLine(); child.BeginErrorReadLine();
            if (!child.StandardInput.WriteLineAsync(Json.Encode(config)).Wait(5000, cancel)) throw new Fault("STARTUP_FAILED", "The tunnel helper did not accept its configuration.");
            if (!ready.Wait(12000, cancel) || child.HasExited) throw new Fault("STARTUP_FAILED", "The Windows tunnel helper could not start the VPN engine.");
            // The callback can still arrive after a cancelled wait; keep the tiny event alive
            // until process disposal rather than disposing it under the output callback.
        }
        public bool Cleanup() {
            bool clean = true;
            using (var timeout = new CancellationTokenSource(20000)) {
                try {
                    if (child != null) {
                        if (!child.HasExited) { if (!child.StandardInput.WriteLineAsync("STOP").Wait(1000)) throw new IOException(); if (!child.WaitForExit(10000)) { clean = false; if (job != null) job.Terminate(); child.WaitForExit(2000); } }
                        if (child.HasExited) { if (child.ExitCode != 0 || helperCleanupFault) clean = false; } else clean = false;
                    }
                } catch { clean = false; }
                finally {
                    if (job != null) { try { job.Drain(); } catch { clean = false; } job.Dispose(); job = null; }
                    if (child != null) { try { if (!child.HasExited) child.Kill(); } catch { } child.Dispose(); child = null; }
                }
                try {
                    if (!NetworkChecks.NoTunnel(timeout.Token)) clean = false;
                    if (baseline != null && NetworkChecks.Baseline(timeout.Token) != baseline) clean = false;
                } catch { clean = false; }
            }
            if (clean && File.Exists(BaselineFile)) { try { File.Delete(BaselineFile); } catch { clean = false; } }
            baseline = null; return clean;
        }
        public void Abort() { ProcessJob current = job; if (current != null) current.Dispose(); }
    }
}
