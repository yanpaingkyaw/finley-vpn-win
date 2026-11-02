using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace Finley {
    internal sealed class TunnelService : ServiceBase {
        readonly TunnelController tunnel = new TunnelController();
        readonly object sync = new object();
        readonly HashSet<Connection> clients = new HashSet<Connection>();
        volatile bool stopping;
        NamedPipeServerStream listener;
        internal TunnelService() { ServiceName = Wire.Service; CanStop = true; CanShutdown = true; CanHandleSessionChangeEvent = true; AutoLog = true; }
        protected override void OnStart(string[] args) {
            WindowsSecurity.ConfigureServiceProcessSecurity();
            tunnel.Initialize();
            listener = WindowsSecurity.CreatePipe(true);
            var thread = new Thread(Accept) { IsBackground = true }; thread.Start();
        }
        void Accept() {
            try {
                while (!stopping) {
                    NamedPipeServerStream connected = listener;
                    connected.WaitForConnection();
                    // Keep at least one service-owned instance open for the entire service lifetime.
                    listener = WindowsSecurity.CreatePipe(false);
                    lock (sync) {
                        if (clients.Count >= 8) { connected.Dispose(); continue; }
                        var client = new Connection(connected, tunnel, c => { lock (sync) clients.Remove(c); }); clients.Add(client);
                        ThreadPool.QueueUserWorkItem(_ => client.Run());
                    }
                }
            } catch {
                if (!stopping) { ExitCode = 1; Stop(); }
            }
        }
        protected override void OnSessionChange(SessionChangeDescription change) { if (change.Reason == SessionChangeReason.SessionLogoff) tunnel.Logoff(change.SessionId); }
        protected override void OnShutdown() { OnStop(); }
        protected override void OnStop() {
            stopping = true; RequestAdditionalTime(30000);
            if (listener != null) listener.Dispose();
            tunnel.Dispose();
            Connection[] copy; lock (sync) { copy = new Connection[clients.Count]; clients.CopyTo(copy); }
            foreach (Connection client in copy) client.Close();
        }
        static int Main(string[] args) {
            if (args.Length == 1) return Maintenance.Run(args[0]);
            if (args.Length != 0) return 1;
            ServiceBase.Run(new TunnelService()); return 0;
        }
    }

    internal sealed class Connection {
        readonly NamedPipeServerStream pipe;
        readonly TunnelController tunnel;
        readonly Action<Connection> removed;
        readonly object writer = new object();
        readonly HashSet<string> pending = new HashSet<string>();
        WindowsSecurity.Caller caller;
        int closed, eventPending;
        internal Connection(NamedPipeServerStream p, TunnelController t, Action<Connection> r) { pipe = p; tunnel = t; removed = r; }
        void Send(object value) { lock (writer) { if (closed != 0) return; try { Wire.Write(pipe, value); } catch { Close(); } } }
        void Changed() {
            if (Interlocked.CompareExchange(ref eventPending, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ => { try { Send(Json.Obj("version", Wire.Version, "event", "status", "value", tunnel.Status(this))); } finally { Interlocked.Exchange(ref eventPending, 0); } });
        }
        internal void Run() {
            try {
                Dictionary<string, object> hello = Wire.Read(pipe, 5000);
                Json.Fields(hello, "version", "id", "method");
                string id = RequestId(hello);
                if (Json.Number(hello, "version", 0, 65535) != Wire.Version) { Send(Wire.Error(id, "VERSION_MISMATCH", "Finley and its service have different versions. Ask an administrator to repair Finley.")); return; }
                if (Json.Text(hello, "method", 32) != "hello") throw Json.Invalid();
                caller = WindowsSecurity.Authenticate(pipe);
                Send(Json.Obj("version", Wire.Version, "id", id, "ok", true, "value", tunnel.Status(this)));
                tunnel.Changed += Changed;
                while (closed == 0) {
                    var request = Wire.Read(pipe, 15000);
                    Json.Fields(request, "version", "id", "method", "profile", "bypass");
                    string requestId = RequestId(request);
                    if (Json.Number(request, "version", 0, 65535) != Wire.Version) throw Json.Invalid();
                    string method = Json.Text(request, "method", 32);
                    lock (pending) { if (pending.Count >= 4 || !pending.Add(requestId)) throw Json.Invalid(); }
                    Dispatch(requestId, method, request);
                }
            } catch { } finally { Close(); removed(this); }
        }
        static string RequestId(Dictionary<string, object> request) {
            string id = Json.Text(request, "id", 64);
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-zA-Z0-9_-]+$")) throw Json.Invalid(); return id;
        }
        void Dispatch(string id, string method, Dictionary<string, object> request) {
            Task<object> operation;
            try {
                switch (method) {
                    case "getStatus": Json.Fields(request, "version", "id", "method"); operation = Task.FromResult(tunnel.Status(this)); break;
                    case "connect": operation = tunnel.Connect(this, caller, Json.Get(request, "profile"), Json.Get(request, "bypass")); break;
                    case "disconnect": Json.Fields(request, "version", "id", "method"); operation = tunnel.Disconnect(this); break;
                    default: throw Json.Invalid();
                }
            } catch (Fault fault) { lock (pending) pending.Remove(id); Send(Wire.Error(id, fault.Code, fault.Message)); return; }
            operation.ContinueWith(result => {
                lock (pending) pending.Remove(id);
                if (result.IsFaulted) {
                    Fault fault = result.Exception.GetBaseException() as Fault;
                    Send(Wire.Error(id, fault == null ? "SERVICE_FAILURE" : fault.Code, fault == null ? "The Finley service could not complete the request." : fault.Message));
                } else if (result.IsCanceled) Send(Wire.Error(id, "CANCELLED", "VPN connection was cancelled."));
                else Send(Json.Obj("version", Wire.Version, "id", id, "ok", true, "value", result.Result));
            });
        }
        internal void Close() {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            tunnel.Changed -= Changed; pipe.Dispose(); tunnel.Release(this, false);
        }
    }
}
