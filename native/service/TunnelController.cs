using System;
using System.Threading;
using System.Threading.Tasks;

namespace Finley {
    // All network mutations are serialized here. The backend owns Windows resources;
    // the controller owns the connection lease and cancellation/state transitions.
    internal sealed class TunnelController : IDisposable {
        readonly object sync = new object();
        readonly SemaphoreSlim lifecycle = new SemaphoreSlim(1, 1);
        readonly Func<ITunnelBackend> backendFactory;
        ITunnelBackend backend;
        string phase = "disconnected", faultCode;
        long revision, lease;
        object owner;
        int ownerSession;
        bool cleanupConfirmed = true, stopping;
        CancellationTokenSource attempt;
        Task<object> disconnecting;
        internal event Action Changed = delegate { };
        internal TunnelController() : this(() => new NativeTunnelBackend()) { }
        internal TunnelController(Func<ITunnelBackend> factory) { backendFactory = factory; }
        internal void Initialize() {
            if (!NativeTunnelBackend.Initialize()) {
                lock (sync) { phase = "error"; faultCode = "CLEANUP_UNCONFIRMED"; cleanupConfirmed = false; revision++; }
            }
        }
        internal object Status(object client) {
            lock (sync) {
                bool busy = owner != null && owner != client;
                return Json.Obj("state", busy ? "busy" : phase, "owned", owner == client, "cleanupConfirmed", cleanupConfirmed, "revision", revision, "errorCode", busy ? null : faultCode, "engineReady", true);
            }
        }
        internal Task<object> Connect(object client, WindowsSecurity.Caller caller, object profile, object bypass) {
            Configuration.Build(profile, bypass, null);
            ITunnelBackend current; CancellationTokenSource cancellation; long currentLease;
            lock (sync) {
                if (stopping) throw new Fault("SERVICE_STOPPING", "The Finley service is stopping.");
                if (!cleanupConfirmed) throw CleanupFault();
                if (owner != null) throw new Fault(owner == client ? "OPERATION_IN_PROGRESS" : "VPN_IN_USE", "A VPN connection is already active or changing.");
                if (!lifecycle.Wait(0)) throw new Fault("OPERATION_IN_PROGRESS", "The previous VPN operation is still finishing.");
                try { current = backend = backendFactory(); } catch { lifecycle.Release(); throw; }
                cancellation = attempt = new CancellationTokenSource(150000);
                currentLease = ++lease; owner = client; ownerSession = caller.Session;
                disconnecting = null; faultCode = null; phase = "connecting"; revision++;
                current.Exited += () => { lock (sync) { if (lease != currentLease || owner != client || phase != "connected") return; } Release(client, true); };
            }
            Changed();
            return Task.Run<object>(() => {
                try {
                    cancellation.Token.ThrowIfCancellationRequested();
                    current.Connect(profile, bypass, cancellation.Token);
                    lock (sync) {
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (current.HasExited) throw new Fault("ENGINE_EXIT", "The VPN engine stopped during connection.");
                        cancellation.CancelAfter(Timeout.Infinite); phase = "connected"; revision++;
                    }
                    Changed(); return Status(client);
                } catch (Exception error) {
                    bool clean = Clean(current);
                    lock (sync) {
                        owner = null; backend = null; cleanupConfirmed = clean;
                        phase = error is OperationCanceledException && clean ? "disconnected" : "error";
                        faultCode = clean ? (error is OperationCanceledException ? "CANCELLED" : (error is Fault ? ((Fault)error).Code : "STARTUP_FAILED")) : "CLEANUP_UNCONFIRMED";
                        revision++; attempt = null; cancellation.Dispose();
                    }
                    Changed();
                    if (!clean) throw CleanupFault();
                    if (error is Fault) throw;
                    throw new Fault(error is OperationCanceledException ? "CANCELLED" : "STARTUP_FAILED", error is OperationCanceledException ? "VPN connection was cancelled or timed out." : "Could not start or verify the VPN. Check your network and repair Finley if needed.");
                } finally { lifecycle.Release(); }
            });
        }
        static bool Clean(ITunnelBackend current) { try { return current.Cleanup(); } catch { current.Abort(); return false; } }
        static Fault CleanupFault() { return new Fault("CLEANUP_UNCONFIRMED", "VPN cleanup could not be confirmed. Check Windows network settings and ask an administrator to repair Finley."); }
        internal Task<object> Disconnect(object client) { return StopOwner(client, false); }
        Task<object> StopOwner(object client, bool unexpected) {
            Task<object> operation;
            lock (sync) {
                if (owner == null) return Task.FromResult(Status(client));
                if (owner != client) throw new Fault("VPN_IN_USE", "The VPN is in use by another app or Windows session.");
                if (attempt != null) attempt.Cancel();
                if (disconnecting != null) return disconnecting;
                phase = "disconnecting"; revision++;
                long currentLease = lease;
                operation = disconnecting = Task.Run<object>(() => {
                    lifecycle.Wait();
                    try {
                        ITunnelBackend current;
                        lock (sync) { if (owner != client || lease != currentLease) return Status(client); current = backend; }
                        bool clean = Clean(current);
                        lock (sync) {
                            owner = null; backend = null; cleanupConfirmed = clean;
                            phase = clean && !unexpected ? "disconnected" : "error";
                            faultCode = !clean ? "CLEANUP_UNCONFIRMED" : unexpected ? "ENGINE_EXIT" : null;
                            revision++; if (attempt != null) { attempt.Dispose(); attempt = null; }
                        }
                        Changed(); if (!clean) throw CleanupFault(); return Status(client);
                    } finally { lifecycle.Release(); }
                });
            }
            Changed(); return operation;
        }
        internal void Release(object client, bool unexpected) {
            lock (sync) { if (owner != client) return; }
            try { StopOwner(client, unexpected).ContinueWith(t => { var ignored = t.Exception; }); } catch (Fault) { }
        }
        internal void Logoff(int session) { object client; lock (sync) { client = ownerSession == session ? owner : null; } if (client != null) Release(client, false); }
        public void Dispose() {
            object client; lock (sync) { stopping = true; client = owner; }
            if (client != null) { try { StopOwner(client, false).Wait(25000); } catch { } }
            lock (sync) { if (backend != null) backend.Abort(); }
        }
    }
}
