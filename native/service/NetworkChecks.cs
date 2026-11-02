using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Finley {
    internal static class NetworkChecks {
        static async Task<string> ReadBounded(StreamReader reader) {
            var result = new StringBuilder(); var buffer = new char[1024]; int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)) != 0) {
                if (result.Length + count > 65536) throw new IOException("Network check output exceeded its limit.");
                result.Append(buffer, 0, count);
            }
            return result.ToString();
        }
        internal static string PowerShell(string script, CancellationToken cancel) {
            cancel.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop';" + script)),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
            };
            using (var job = new ProcessJob()) using (var process = new Process { StartInfo = start }) {
                process.Start();
                try {
                    job.Assign(process);
                    var output = ReadBounded(process.StandardOutput); var errors = ReadBounded(process.StandardError);
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (!process.WaitForExit(100)) { cancel.ThrowIfCancellationRequested(); if (DateTime.UtcNow >= deadline) throw new IOException(); }
                    string result = output.Result.Trim();
                    if (process.ExitCode != 0 || result.Length > 65536 || errors.Result.Length > 65536) throw new IOException(); return result;
                } finally { if (!process.HasExited) { try { process.Kill(); } catch { } } }
            }
        }
        internal static string DefaultInterface(CancellationToken cancel) {
            string value = PowerShell("$r=Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' | ForEach-Object { $i=Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue; if ($i.ConnectionState -eq 'Connected' -and $_.InterfaceAlias -notlike 'Finley-*') { [pscustomobject]@{Name=$_.InterfaceAlias;Metric=($_.RouteMetric+$i.InterfaceMetric)} } } | Sort-Object Metric | Select-Object -First 1 -ExpandProperty Name; if (-not $r) {exit 2}; [Console]::Out.Write($r)", cancel);
            if (value.Length == 0 || value.Length > 256 || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new Fault("ROUTE_CHECK_FAILED", "Could not determine the current internet interface. Check your network."); return value;
        }
        internal static void Routes(string name, CancellationToken cancel) {
            foreach (string address in new[] { "9.9.9.9", "200.1.1.1" }) {
                string route = PowerShell("$r=Find-NetRoute -RemoteIPAddress '" + address + "' | Where-Object {$_.DestinationPrefix} | Select-Object -First 1 -ExpandProperty InterfaceAlias; if (-not $r) {exit 2}; [Console]::Out.Write($r)", cancel);
                if (!String.Equals(route, name, StringComparison.OrdinalIgnoreCase)) throw new Fault("ROUTE_CONFLICT", "Another network route is taking precedence. Disconnect any other VPN and try again.");
            }
        }
        // Snapshot only stable IPv4 default routes and per-interface DNS, excluding Finley's adapter.
        internal static string Baseline(CancellationToken cancel) {
            return PowerShell("$r=@(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' | Where-Object {$_.InterfaceAlias -notlike 'Finley-*'} | Sort-Object InterfaceIndex,NextHop,RouteMetric | ForEach-Object { 'R|'+$_.InterfaceIndex+'|'+$_.NextHop+'|'+$_.RouteMetric }); $d=@(Get-DnsClientServerAddress | Where-Object {$_.InterfaceAlias -notlike 'Finley-*'} | Sort-Object InterfaceIndex,AddressFamily | ForEach-Object {'D|'+$_.InterfaceIndex+'|'+$_.AddressFamily+'|'+($_.ServerAddresses -join ',')}); [Console]::Out.Write(($r+$d) -join ';')", cancel);
        }
        internal static bool NoTunnel(CancellationToken cancel) {
            return PowerShell("$a=@(Get-NetAdapter -IncludeHidden | Where-Object {$_.Name -like 'Finley-*' -and $_.Status -eq 'Up'}); $r=@(Get-NetRoute | Where-Object {$_.InterfaceAlias -like 'Finley-*'}); if ($a.Count -or $r.Count) {[Console]::Out.Write('stale')} else {[Console]::Out.Write('clean')}", cancel) == "clean";
        }
        internal static string Secret() { using (var random = System.Security.Cryptography.RandomNumberGenerator.Create()) { byte[] bytes = new byte[18]; random.GetBytes(bytes); return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); } }
        internal static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
        static void Send(Stream stream, byte[] value) { stream.Write(value, 0, value.Length); }
        static byte[] Read(Stream stream, int count) {
            byte[] value = new byte[count]; int at = 0;
            while (at < count) { int n = stream.Read(value, at, count - at); if (n == 0) throw new IOException(); at += n; } return value;
        }
        internal static void Probe(int port, string username, string password, CancellationToken cancel) {
            using (var socket = new TcpClient())
            using (cancel.Register(() => socket.Close()))
            using (var timer = new Timer(_ => socket.Close(), null, 8000, Timeout.Infinite)) {
                socket.Connect(IPAddress.Loopback, port); var stream = socket.GetStream(); stream.ReadTimeout = stream.WriteTimeout = 8000;
                Send(stream, new byte[] { 5, 1, 2 }); byte[] reply = Read(stream, 2);
                if (reply[0] != 5 || reply[1] != 2) throw new IOException();
                byte[] user = Encoding.ASCII.GetBytes(username), pass = Encoding.ASCII.GetBytes(password);
                using (var auth = new MemoryStream()) { auth.WriteByte(1); auth.WriteByte((byte)user.Length); Send(auth, user); auth.WriteByte((byte)pass.Length); Send(auth, pass); Send(stream, auth.ToArray()); }
                reply = Read(stream, 2); if (reply[0] != 1 || reply[1] != 0) throw new IOException();
                byte[] host = Encoding.ASCII.GetBytes("www.gstatic.com");
                using (var request = new MemoryStream()) { Send(request, new byte[] { 5, 1, 0, 3, (byte)host.Length }); Send(request, host); Send(request, new byte[] { 1, 187 }); Send(stream, request.ToArray()); }
                reply = Read(stream, 4); if (reply[0] != 5 || reply[1] != 0 || reply[2] != 0) throw new IOException();
                if (reply[3] == 1) Read(stream, 6); else if (reply[3] == 4) Read(stream, 18); else if (reply[3] == 3) Read(stream, Read(stream, 1)[0] + 2); else throw new IOException();
                using (var tls = new SslStream(stream, false)) {
                    tls.AuthenticateAsClient("www.gstatic.com", null, SslProtocols.Tls12, true);
                    Send(tls, Encoding.ASCII.GetBytes("GET /generate_204 HTTP/1.1\r\nHost: www.gstatic.com\r\nConnection: close\r\n\r\n"));
                    var line = new StringBuilder();
                    while (line.Length < 4096) { char c = (char)Read(tls, 1)[0]; line.Append(c); if (c == '\n') break; }
                    if (!System.Text.RegularExpressions.Regex.IsMatch(line.ToString(), @"^HTTP/1\.[01] 204(?:\s|$)")) throw new IOException();
                }
            }
        }
    }
}
