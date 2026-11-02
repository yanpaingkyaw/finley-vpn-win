using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Finley {
    internal static class Configuration {
        static object[] Strings(Dictionary<string, object> input, string key) {
            object[] values = Json.Get(input, key) as object[];
            if (values == null || values.Length > 200 || values.Any(v => !(v is string))) throw Json.Invalid(); return values;
        }
        internal static Dictionary<string, object> NormalizeBypass(object value) {
            var b = Json.Map(value); Json.Fields(b, "enabled", "paths", "domains", "cidrs");
            object enabled = Json.Get(b, "enabled"); if (!(enabled is bool)) throw Json.Invalid();
            var paths = new List<string>(); var domains = new List<string>(); var cidrs = new List<string>();
            foreach (string p in Strings(b, "paths")) {
                // Device namespaces and alternate streams are never meaningful bypass targets.
                if (p.Length > 1024 || p.IndexOfAny(new[] { '\0', '\r', '\n', '"', '<', '>', '|', '*', '?' }) >= 0 || p.StartsWith(@"\\?\") || p.StartsWith(@"\\.\") || !Path.IsPathRooted(p) || !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw Json.Invalid();
                if (p.Length >= 2 && p[1] == ':' && (p.Length < 3 || (p[2] != '\\' && p[2] != '/'))) throw Json.Invalid();
                string normalized; try { normalized = (p[0] == '\\' || p[0] == '/') && p.Length > 1 && p[1] != '\\' && p[1] != '/' ? Path.GetFullPath("C:" + p).Substring(2) : Path.GetFullPath(p); } catch { throw Json.Invalid(); }
                if (normalized.Substring(2).Contains(":")) throw Json.Invalid();
                int index = paths.FindIndex(x => String.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
                if (index < 0) paths.Add(normalized); else paths[index] = normalized;
            }
            foreach (string d in Strings(b, "domains")) {
                if (d.Length > 253) throw Json.Invalid(); string domain;
                try { domain = new IdnMapping().GetAscii(d.Trim().TrimEnd('.')).ToLowerInvariant(); } catch { throw Json.Invalid(); }
                IPAddress ip;
                if (domain.Length > 253 || !domain.Contains(".") || IPAddress.TryParse(domain, out ip) || domain.Split('.').Any(label => label.Length > 63 || !Regex.IsMatch(label, @"^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$"))) throw Json.Invalid();
                if (!domains.Contains(domain)) domains.Add(domain);
            }
            foreach (string raw in Strings(b, "cidrs")) {
                if (raw.Length > 64) throw Json.Invalid(); string[] parts = raw.Trim().Split('/');
                if (parts.Length > 2 || !Regex.IsMatch(parts[0], @"^(?:0|[1-9][0-9]{0,2})(?:\.(?:0|[1-9][0-9]{0,2})){3}$")) throw Json.Invalid();
                string[] octets = parts[0].Split('.'); uint address = 0;
                foreach (string octet in octets) { uint n = UInt32.Parse(octet); if (n > 255) throw Json.Invalid(); address = (address << 8) | n; }
                int bits = 32;
                if (parts.Length == 2 && (!Regex.IsMatch(parts[1], @"^(?:0|[1-9][0-9]?)$") || !Int32.TryParse(parts[1], out bits) || bits > 32)) throw Json.Invalid();
                uint network = address & (bits == 0 ? 0 : UInt32.MaxValue << (32 - bits));
                string cidr = String.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}/{4}", network >> 24, (network >> 16) & 255, (network >> 8) & 255, network & 255, bits);
                if (!cidrs.Contains(cidr)) cidrs.Add(cidr);
            }
            return Json.Obj("enabled", enabled, "paths", paths.ToArray(), "domains", domains.ToArray(), "cidrs", cidrs.ToArray());
        }
        internal static Dictionary<string, object> Build(object profileValue, object bypassValue, string directInterface) {
            var p = Json.Map(profileValue); Json.Fields(p, "server", "port", "method", "password");
            string server = Json.Text(p, "server", 253), method = Json.Text(p, "method", 64), password = Json.Text(p, "password", 4096);
            int port = Json.Number(p, "port", 1, 65535);
            if (Regex.IsMatch(server, @"[\s/@\\\x00-\x1f]") || Array.IndexOf(new[] { "aes-128-gcm", "aes-192-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305" }, method) < 0) throw Json.Invalid();
            string host = server.TrimStart('[').TrimEnd(']'); IPAddress parsed;
            if (!IPAddress.TryParse(host, out parsed) && Uri.CheckHostName(host) != UriHostNameType.Dns) throw Json.Invalid();
            var b = NormalizeBypass(bypassValue); bool enabled = (bool)b["enabled"];
            string[] paths = (string[])b["paths"], domains = (string[])b["domains"], cidrs = (string[])b["cidrs"];
            if (directInterface != null && (String.IsNullOrWhiteSpace(directInterface) || directInterface.Length > 256 || directInterface.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)) throw Json.Invalid();
            var rules = new List<object>();
            if (enabled && domains.Length > 0) rules.Add(Json.Obj("inbound", "tun-in", "action", "sniff"));
            rules.Add(Json.Obj("inbound", "tun-in", "network", new[] { "tcp", "udp" }, "port", 53, "action", "hijack-dns"));
            rules.Add(Json.Obj("protocol", "dns", "action", "hijack-dns"));
            rules.Add(Json.Obj("inbound", "tun-in", "ip_cidr", new[] { "172.19.0.0/30" }, "action", "reject", "no_drop", true));
            rules.Add(Json.Obj("inbound", "tun-in", "ip_version", 6, "action", "reject", "no_drop", true));
            if (enabled && paths.Length > 0) rules.Add(Json.Obj("process_path_regex", paths.Select(path => "(?i)^" + Regex.Replace(path, @"[.*+?^${}()|\[\]\\]", "\\$0") + "$").ToArray(), "outbound", "direct"));
            if (enabled && domains.Length > 0) rules.Add(Json.Obj("domain_suffix", domains, "outbound", "direct"));
            if (enabled && cidrs.Length > 0) rules.Add(Json.Obj("ip_cidr", cidrs, "outbound", "direct"));
            var direct = Json.Obj("type", "direct", "tag", "direct"); if (directInterface != null) direct.Add("bind_interface", directInterface);
            return Json.Obj(
                "log", Json.Obj("disabled", true),
                "dns", Json.Obj("servers", new object[] {
                    Json.Obj("type", "local", "tag", "bootstrap"),
                    Json.Obj("type", "https", "tag", "secure-dns", "server", "1.1.1.1", "tls", Json.Obj("enabled", true, "server_name", "cloudflare-dns.com"), "detour", "proxy")
                }, "rules", new[] { Json.Obj("query_type", "AAAA", "action", "predefined", "rcode", "NOERROR") }, "final", "secure-dns"),
                "inbounds", new List<object> { Json.Obj("type", "tun", "tag", "tun-in", "address", new[] { "172.19.0.1/30", "fdfe:dcba:9876::1/126" }, "mtu", 1400, "auto_route", true, "strict_route", true) },
                "outbounds", new object[] { Json.Obj("type", "shadowsocks", "tag", "proxy", "server", host, "server_port", port, "method", method, "password", password), direct },
                "route", Json.Obj("auto_detect_interface", true, "default_domain_resolver", "bootstrap", "rules", rules, "final", "proxy"));
        }
    }
}
