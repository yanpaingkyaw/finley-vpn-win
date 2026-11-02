'use strict';
// Generates the sing-box 1.14.x tunnel configuration. Windows routing, DNS,
// bypass and recovery still need release verification on Windows 10 and 11.
// The returned object contains a secret; send it only through the helper pipe.
const { normalizeBypass } = require('./bypass.cjs');
const METHODS = new Set(['aes-128-gcm', 'aes-192-gcm', 'aes-256-gcm', 'chacha20-ietf-poly1305', 'xchacha20-ietf-poly1305']);
function createCandidateConfig(profile, bypass, directInterface) {
  if (!profile || typeof profile !== 'object' || typeof profile.server !== 'string' || !profile.server || /[\s\/@]/.test(profile.server) || !Number.isInteger(profile.port) || profile.port < 1 || profile.port > 65535 || !METHODS.has(profile.method) || typeof profile.password !== 'string' || !profile.password) throw new Error('Invalid Shadowsocks connection configuration.');
  if (!bypass || typeof bypass !== 'object') throw new Error('Invalid bypass settings.');
  const safe = normalizeBypass(bypass.enabled, bypass.paths, bypass.domains ?? [], bypass.cidrs ?? []);
  if (directInterface !== undefined && (typeof directInterface !== 'string' || !directInterface.trim() || directInterface.length > 256 || /[\r\n\x00]/.test(directInterface))) throw new Error('Invalid network interface.');
  const rules = [];
  // Inspect supported HTTP/TLS/QUIC hostnames. Do not infer a domain from
  // cached DNS IPs because shared addresses can belong to unrelated sites.
  if (safe.enabled && safe.domains.length) rules.push({ inbound: 'tun-in', action: 'sniff' });
  // DNS may be sent by a Windows service or the selected application itself.
  // Intercept it before the tunnel-subnet guard and application bypass rules.
  rules.push({ inbound: 'tun-in', network: ['tcp', 'udp'], port: 53, action: 'hijack-dns' });
  rules.push({ protocol: 'dns', action: 'hijack-dns' });
  // Never send the tunnel's own addresses to the direct outbound.
  rules.push({ inbound: 'tun-in', ip_cidr: ['172.19.0.0/30'], action: 'reject', no_drop: true });
  // Keep unsupported IPv6 inside the tunnel and reject before direct rules.
  rules.push({ inbound: 'tun-in', ip_version: 6, action: 'reject', no_drop: true });
  // Windows paths are case-insensitive; sing-box process_path is case-sensitive.
  // Escape regex metacharacters and anchor each path to avoid matching other apps.
  if (safe.enabled && safe.paths.length) rules.push({ process_path_regex: safe.paths.map(p => '(?i)^' + p.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$'), outbound: 'direct' });
  // A suffix without a leading dot matches both the root and its subdomains.
  if (safe.enabled && safe.domains.length) rules.push({ domain_suffix: safe.domains, outbound: 'direct' });
  if (safe.enabled && safe.cidrs.length) rules.push({ ip_cidr: safe.cidrs, outbound: 'direct' });
  return {
    log: { disabled: true },
    dns: { servers: [
      { type: 'local', tag: 'bootstrap' },
      { type: 'https', tag: 'secure-dns', server: '1.1.1.1', tls: { enabled: true, server_name: 'cloudflare-dns.com' }, detour: 'proxy' }
    ], rules: [{ query_type: 'AAAA', action: 'predefined', rcode: 'NOERROR' }], final: 'secure-dns' },
    inbounds: [{ type: 'tun', tag: 'tun-in', address: ['172.19.0.1/30', 'fdfe:dcba:9876::1/126'], mtu: 1400, auto_route: true, strict_route: true }],
    outbounds: [
      { type: 'shadowsocks', tag: 'proxy', server: profile.server.replace(/^\[|\]$/g, ''), server_port: profile.port, method: profile.method, password: profile.password },
      { type: 'direct', tag: 'direct', ...(directInterface ? {bind_interface: directInterface} : {}) }
    ],
    route: { auto_detect_interface: true, default_domain_resolver: 'bootstrap', rules, final: 'proxy' }
  };
}
module.exports = { createCandidateConfig };
