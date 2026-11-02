using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Finley {
    internal sealed class Fault : Exception {
        internal readonly string Code;
        internal Fault(string code, string message) : base(message) { Code = code; }
    }

    internal static class Json {
        internal static Dictionary<string, object> Obj(params object[] pairs) {
            var result = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
            return result;
        }
        internal static string Encode(object value) { return new JavaScriptSerializer { MaxJsonLength = Wire.MaxBytes, RecursionLimit = 24 }.Serialize(value); }
        internal static Dictionary<string, object> Map(object value) {
            var map = value as Dictionary<string, object>;
            if (map == null) throw Invalid(); return map;
        }
        internal static Fault Invalid() { return new Fault("INVALID_REQUEST", "Invalid connection settings or service request."); }
        internal static void Fields(Dictionary<string, object> map, params string[] names) {
            foreach (string key in map.Keys) if (Array.IndexOf(names, key) < 0) throw Invalid();
        }
        internal static object Get(Dictionary<string, object> map, string key) {
            object value; if (!map.TryGetValue(key, out value)) throw Invalid(); return value;
        }
        internal static string Text(Dictionary<string, object> map, string key, int limit) {
            var s = Get(map, key) as string;
            if (String.IsNullOrEmpty(s) || s.Length > limit || s.IndexOf('\0') >= 0) throw Invalid(); return s;
        }
        internal static int Number(Dictionary<string, object> map, string key, int min, int max) {
            object value = Get(map, key);
            if (!(value is decimal)) throw Invalid();
            decimal n = (decimal)value;
            if (n != Decimal.Truncate(n) || n < min || n > max) throw Invalid(); return (int)n;
        }
        // A bounded strict parser: JavaScriptSerializer alone silently accepts duplicate keys.
        internal static object Decode(string text) { return new Parser(text).Parse(); }
        private sealed class Parser {
            readonly string input; int at;
            internal Parser(string text) { input = text; }
            void Space() { while (at < input.Length && " \r\n\t".IndexOf(input[at]) >= 0) at++; }
            char Take() { if (at >= input.Length) throw Invalid(); return input[at++]; }
            internal object Parse() { object value = Value(0); Space(); if (at != input.Length) throw Invalid(); return value; }
            string String() {
                int start = at; if (Take() != '"') throw Invalid();
                while (true) {
                    char c = Take(); if (c == '"') break;
                    if (c < 32) throw Invalid();
                    if (c == '\\') { c = Take(); if (c == 'u') { for (int i = 0; i < 4; i++) if (!Uri.IsHexDigit(Take())) throw Invalid(); } else if ("\"\\/bfnrt".IndexOf(c) < 0) throw Invalid(); }
                }
                try { return new JavaScriptSerializer().Deserialize<string>(input.Substring(start, at - start)); } catch { throw Invalid(); }
            }
            object Value(int depth) {
                if (depth > 20) throw Invalid(); Space(); if (at >= input.Length) throw Invalid();
                char c = input[at];
                if (c == '"') return String();
                if (c == '{') {
                    at++; Space(); var map = Obj(); if (at < input.Length && input[at] == '}') { at++; return map; }
                    while (true) { Space(); string key = String(); Space(); if (Take() != ':' || map.ContainsKey(key) || map.Count >= 1000) throw Invalid(); map.Add(key, Value(depth + 1)); Space(); c = Take(); if (c == '}') return map; if (c != ',') throw Invalid(); }
                }
                if (c == '[') {
                    at++; Space(); var list = new List<object>(); if (at < input.Length && input[at] == ']') { at++; return list.ToArray(); }
                    while (true) { if (list.Count >= 1000) throw Invalid(); list.Add(Value(depth + 1)); Space(); c = Take(); if (c == ']') return list.ToArray(); if (c != ',') throw Invalid(); }
                }
                foreach (string literal in new[] { "true", "false", "null" }) {
                    if (input.Substring(at).StartsWith(literal, StringComparison.Ordinal)) { at += literal.Length; return literal == "null" ? null : (object)(literal == "true"); }
                }
                int begin = at; while (at < input.Length && "0123456789.eE+-".IndexOf(input[at]) >= 0) at++;
                string number = input.Substring(begin, at - begin); decimal n;
                if (!System.Text.RegularExpressions.Regex.IsMatch(number, @"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$") || !Decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) throw Invalid();
                return n;
            }
        }
    }

    internal static class Wire {
        internal const int Version = 1, MaxBytes = 1024 * 1024;
        internal const string Service = "FinleyTunnelService", Pipe = "Finley.Tunnel.v1";
        internal static byte[] ReadExact(Stream stream, int count, DateTime deadline) {
            byte[] data = new byte[count]; int offset = 0;
            while (offset < count) {
                int wait = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (wait <= 0) throw new IOException("Service read timed out.");
                IAsyncResult read = stream.BeginRead(data, offset, count - offset, null, null);
                using (read.AsyncWaitHandle) {
                    if (!read.AsyncWaitHandle.WaitOne(wait)) { stream.Dispose(); throw new IOException("Service read timed out."); }
                    int got = stream.EndRead(read); if (got == 0) throw new EndOfStreamException(); offset += got;
                }
            }
            return data;
        }
        internal static Dictionary<string, object> Read(Stream stream, int timeout) {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeout);
            byte[] size = ReadExact(stream, 4, deadline);
            uint length = (uint)(size[0] | size[1] << 8 | size[2] << 16 | size[3] << 24);
            if (length == 0 || length > MaxBytes) throw Json.Invalid();
            return Json.Map(Json.Decode(new UTF8Encoding(false, true).GetString(ReadExact(stream, (int)length, deadline))));
        }
        internal static void Write(Stream stream, object message) {
            byte[] body = new UTF8Encoding(false, true).GetBytes(Json.Encode(message));
            if (body.Length == 0 || body.Length > MaxBytes) throw Json.Invalid();
            byte[] frame = new byte[body.Length + 4]; Array.Copy(BitConverter.GetBytes(body.Length), frame, 4); Array.Copy(body, 0, frame, 4, body.Length);
            IAsyncResult write = stream.BeginWrite(frame, 0, frame.Length, null, null);
            using (write.AsyncWaitHandle) {
                if (!write.AsyncWaitHandle.WaitOne(5000)) { stream.Dispose(); throw new IOException("Service write timed out."); }
                stream.EndWrite(write);
            }
            stream.Flush();
        }
        internal static Dictionary<string, object> Error(string id, string code, string message) { return Json.Obj("version", Version, "id", id, "ok", false, "error", Json.Obj("code", code, "message", message)); }
    }
}
