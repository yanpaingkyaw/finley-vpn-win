# Finley

Finley is a Windows desktop VPN client under development, based on `PROJECT_SEPC.md`.

**Development preview:** Finley now has experimental Shadowsocks networking using sing-box and a Windows shutdown helper. A real-server connection and normal disconnect were tested on the current PC. Application bypass passed a controlled test with a selected app and an unselected app; DNS, IPv6, recovery, and the full Windows 10 and 11 release test plan remain unverified. Do not rely on it for privacy or distribute it as a production VPN until those checks pass. There is no kill switch; traffic may use the normal connection when the tunnel stops.

## Requirements and development

- 64-bit Windows 10 or 11, Node.js, npm, and Windows user-scoped secure storage.
- This development build requires administrator privileges to create the Windows tunnel. A separate least-privilege elevation mechanism is not yet implemented.
- `bin/sing-box.exe` and `bin/finley-tunnel-helper.exe` are required; the latter can be rebuilt with `powershell -NoProfile -ExecutionPolicy Bypass -File native/build-helper.ps1`.

```powershell
npm install
npm run dev
npm test
npm run build
```

`npm run dist` creates a development installer. Do not distribute it until networking, cleanup, Windows compatibility, signing, and third-party license obligations have been reviewed. The engine is sing-box 1.14.4, GPL-3.0-or-later with additional naming terms; the binary and accompanying notice are in `bin`. The official release archive was checked against its published SHA-256 digest.

## Networking and privacy

Finley sends the decrypted server configuration to the local networking helper through a pipe, not command-line arguments. Saved secrets use Windows secure storage. Server names, addresses, cipher names, and selected executable paths are local metadata. Do not paste access keys into issues or logs.

The helper requests a graceful engine shutdown on disconnect and when the parent closes its input. An abrupt helper crash may leave a child process or networking changes behind. Broader app coverage, child processes, IPv6, and recovery after crashes require further testing. Bypass changes take effect after reconnecting. Check the project specification's real-server, Windows 10 and 11, bypass, DNS, and cleanup acceptance criteria before release.

While connected, Finley currently returns no IPv6 DNS answers and rejects IPv6 traffic inside the tunnel as a compatibility measure. IPv6-only destinations and IPv6 LAN access may not work. Browser secure DNS and hard-coded addresses can behave differently; no IPv6 leak guarantee has been independently audited.

Bypass supports domain names (including subdomains) and IPv4 addresses or CIDR ranges. Changes apply after reconnecting. Domain rules match visible HTTP, TLS, and QUIC hostnames; encrypted or unavailable hostnames may remain on the VPN. Application rules match executable paths; child processes may need separate rules. IPv6 ranges are not supported. DNS requests are handled by Finley.

## Bypass: current approach and verification

Enable **Bypass** and add executable files, domains, IPv4 addresses, or IPv4 CIDR ranges. For example, `example.com` includes `example.com` and `mail.example.com`; `192.0.2.1` is a single address and `192.0.2.0/24` is a range. Reconnect after changing rules.

Matching traffic uses the regular connection. Finley matches executable paths case-insensitively and binds direct traffic to the active IPv4 network interface selected at connection time. Domain rules identify visible HTTP, TLS, or QUIC hostnames; traffic with hidden or unavailable hostnames may stay on the VPN. DNS requests are handled by Finley, including for bypassed apps. IPv6 bypass is not supported in the current IPv4 compatibility mode. Applications with helper or child processes may require additional executable rules. If the network interface changes, reconnect to refresh the direct route.

On this PC, controlled tests confirmed selected app, domain, subdomain, and IPv4 CIDR traffic used the normal connection while unrelated traffic used the VPN. The user also confirmed Telegram, domain, and single IPv4 bypass work. These checks do not establish behavior for every app, protocol, DNS configuration, network, or supported Windows version.

## Licensing

Finley's own source code is MIT licensed. The Windows installer also includes sing-box under GPL-3.0-or-later and its additional naming condition; other dependencies have their own terms. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for source and notices. Review third-party licensing before redistributing the combined installer.
