# Finley VPN Client — Project Specification

**Status:** Planning baseline · **Version:** 1.0 · **Date:** 2026-10-30

## 1. Product overview

Finley is a Windows desktop VPN client inspired by Outline's straightforward connection experience. A user brings an access key from an existing Outline-compatible server, connects their PC, and can exclude selected applications from the tunnel. Finley does not provide accounts, sell access, or host VPN servers.

**MVP outcome:** A user can import a supported key, connect reliably, see the active protocol, choose apps, domains, or IPv4 addresses and ranges to bypass the tunnel, and disconnect cleanly.

**Target users:** Individuals using Windows PCs who already have an Outline server access key and want a simple way to exclude particular apps from VPN routing.

## 2. Scope and success criteria

- Support 64-bit Windows 10 and Windows 11, subject to the chosen engine's supported releases. Confirm the exact minimum Windows builds during the feasibility spike.
- Accept standard Shadowsocks `ss://` access keys compatible with the selected engine, including standard URI encoding and optional labels. Report unsupported ciphers or key forms clearly; never silently change the configuration.
- Store and manage multiple named server entries; connect to one at a time. Paste a key, preview its non-secret details, add it, rename it, and remove it with confirmation.
- Connect and disconnect with clear Connecting, Connected, Disconnecting, Disconnected, and Error states. Show actionable error messages and allow retry.
- Display the actual configured proxy protocol as Shadowsocks and show cipher and TCP/UDP transport availability only when verified from the active configuration and engine. Do not claim an independently verified encryption or transport state without evidence.
- Provide optional bypass rules for selected applications, domains and subdomains, and IPv4 addresses or CIDR ranges; matching supported traffic uses the ordinary network path and other supported traffic goes through the tunnel. Bypass is off by default and applies only while connected.
- Deliver an installable Windows application with a simple interface, accessible labels, keyboard operation, and clear progress feedback.
- Acceptance requires real-server tests proving connection, app-specific routing, correct protocol display, disconnect cleanup, and recovery on supported Windows versions. A working UI alone is not sufficient.

## 3. Out of scope for v1

- Hosting, provisioning, or administering servers; account, subscription, billing, or key issuance services.
- Dynamic `ssconf://` or HTTPS access keys, Outline advanced YAML transports, mobile or macOS clients, and automatic server selection.
- Kill switch, automatic updates, traffic statistics, and localization; these may be considered after the MVP. The UI must not imply kill-switch protection.

## 4. Core user stories and flows

1. **Add a server:** The user pastes an `ss://` key, reviews a name and non-secret connection details, and saves it. Invalid, unsupported, or dynamic keys receive a clear explanation without revealing credentials.
2. **Connect:** The user selects a saved server and chooses Connect. Finley shows progress, then the confirmed connection state and protocol details; failures show a useful message and Retry.
3. **Configure bypass:** The user enables bypass, selects an installed application or browses for its executable, and saves the list. Finley shows which apps bypass the VPN and explains that their traffic uses the regular network. Apply changes on reconnect unless the chosen engine can safely apply them live and this is verified.
4. **Disconnect and manage:** The user disconnects, edits a server name, or removes a server. Removing the active server first disconnects it. Closing the app disconnects and cleans up networking unless a future tray mode is explicitly designed.

## 5. Functional requirements

### Connection and key handling

- Parse and validate the key locally. Verify scheme, endpoint, port, supported cipher, and required credentials without printing or displaying secrets. Accept common encoded and plaintext standard Shadowsocks URI forms supported by the selected engine.
- Treat `ssconf://`, HTTPS dynamic keys, advanced configuration, unknown methods, and malformed keys as unsupported or invalid with specific, safe feedback. Do not fetch untrusted URLs as an alternate import path.
- Keep profiles and preferences locally; encrypt credentials at rest using Windows user-scoped DPAPI or an equivalent Windows credential store. Do not place raw keys in logs, diagnostics, or crash reports.
- Connect only after required permissions and networking dependencies are available. Distinguish invalid configuration, unreachable server, permission failure, and networking-engine failure when the engine provides sufficient evidence.
- Show Connected only after the engine reports an active tunnel and a connectivity check succeeds; a check failure must be reported honestly rather than proof of server compromise. Do not claim that an IP address or geographic location has changed unless measured.
- On disconnect, application exit, failed setup, or recovery from a crash, remove only Finley's own routes, interfaces, and processes. Preserve unrelated network configuration. Provide a recoverable error if cleanup fails.

### Bypass rules

- Select applications from a best-effort installed-app list or by browsing to an `.exe`; identify rules by normalized executable path and show a readable name and path. Avoid duplicates and allow removal. Preserve the list across restarts.
- Present bypass as direct routing with a privacy warning next to the setting. Do not claim it covers child processes, packaged apps, elevated processes, or all traffic until verified for the selected engine; document confirmed limitations in the UI and release notes.
- Preserve normal tunnel routing for other supported traffic. Clearly state DNS and IPv6 behavior verified during the feasibility spike; prevent accidental silent fallback to direct routing if a required routing rule cannot be enforced.
- App rules are local to this Windows user and do not change the server. Existing connections may need to be restarted; tell the user when a reconnect is needed.

### Experience and privacy

- The main screen shows the selected server, state, primary Connect/Disconnect action, protocol details, and navigation to servers and bypass settings. Never display the server password or full key after import.
- Include empty, loading, permission, unsupported-key, connection-failure, and cleanup-failure states. Provide concise recovery steps.
- Store no usage analytics or remote logs by default. Local diagnostic logs must omit credentials and be safe to clear; show a user-readable error without exposing secrets.

## 6. Architecture and technical decisions

- **Desktop shell:** Electron with React, TypeScript, and Vite; local UI only. Use Electron security defaults: context isolation, sandbox where supported, no Node integration in the renderer, and a narrow validated preload/IPC API.
- **Networking:** Integrate a maintained Windows-capable tunnel engine as a separate native process or service; do not implement cryptography or packet routing from scratch. Evaluate license, redistribution, Windows support, Shadowsocks compatibility, TUN routing, per-process matching, DNS, IPv6, privilege and cleanup behavior before selection. Engine choice is gated by evidence and is not yet a promise of compatibility.
- **Process boundary:** Renderer requests validated actions from the desktop main process. Main process owns profile persistence, Windows credential protection, engine lifecycle, and status events. The engine receives only the configuration it needs; restrict access to generated configuration and remove temporary secrets promptly.
- **Data:** Local profile metadata contains an ID, display name, endpoint, method, and reference to a protected secret; preferences contain the bypass enabled flag and executable paths. No database or backend is required for MVP. Never include credentials in UI event payloads or logs.
- **Proposed internal interface:** `listProfiles`, `addProfile(key, name)`, `renameProfile`, `removeProfile`, `connect(profileId)`, `disconnect`, `getStatus`, `getBypassSettings`, `setBypassSettings`, and status-change events. Validate all inputs and return typed, sanitized errors. This is an internal IPC contract, not a public network API.
- **Privileges:** Determine the minimum privilege and installation model during the spike. Explain any Windows UAC prompt before it appears; do not run the entire UI elevated by default. Restrict any privileged service or helper to authenticated, validated local requests.

## 7. Feasibility gate and delivery tasks

1. **Networking spike:** Compare candidate engines against the requirements above. With a real Outline server, verify supported standard keys and ciphers, full-tunnel routing, executable matching, TCP/UDP, DNS, IPv4/IPv6, existing and new connections, cleanup, and behavior under privilege changes. Record versions, license obligations, findings, and limitations.
2. **Gate decision:** Proceed only if the engine can demonstrate reliable app bypass and safe cleanup on both target Windows versions. If not, document the failed cases and revise scope or engine selection before implementation; do not market a partial result as full bypass.
3. **Foundation:** Set up the desktop shell, strict TypeScript, formatting, build pipeline, local persistence, protected secrets, and a narrow IPC boundary.
4. **Connection:** Add key validation and profile management, integrate the selected engine, implement connection states, protocol display, sanitized errors, and cleanup.
5. **Bypass and UX:** Add app discovery and executable selection, persisted rules, clear privacy and reconnect notices, and accessible states.
6. **Release:** Perform the checks below, package a Windows installer, review license notices and signing, and publish only after real-machine acceptance.

## 8. Testing and acceptance checklist

- **Keys and profiles:** Test valid supported encoded and plaintext `ss://` keys, optional labels, invalid ports, malformed encoding, unsupported methods, and dynamic keys. Verify multiple profiles, rename, remove, persistence, and no plaintext credentials in storage or logs.
- **Connection:** Against a real Outline server, connect, browse through the tunnel, confirm displayed protocol/cipher against the active configuration, disconnect, reconnect, and test unreachable server, wrong credential, missing permission, and engine crash. Ensure state and errors reflect actual results.
- **Bypass:** Compare observed traffic for a selected app and an unselected app using controlled endpoints. Test TCP and UDP where supported, DNS and IPv6 behavior, app restart, rule changes, duplicate and missing executables, multiple apps, and the documented limitations. Verify a failed rule does not silently route protected traffic directly.
- **Lifecycle:** Test app exit, interrupted connection, reboot or process termination, and recovery; verify Finley removes its own routes, processes, and temporary files without damaging unrelated networking.
- **Windows and UX:** Test clean install, upgrade, uninstall, keyboard navigation, permission prompts, and normal and elevated apps on supported Windows 10 and 11 builds. Test both Wi-Fi and Ethernet where practical.
- **Security:** Review IPC validation, secret storage and redaction, local helper access, dependency licenses, and absence of unnecessary remote services. Verify no credentials appear in error reports or installer artifacts.

## 9. Deployment and operations

- Build and test a versioned 64-bit Windows installer on a clean Windows machine; include third-party notices and dependency versions.
- Sign the executable and installer with a trusted certificate when available. Until then, clearly document Windows reputation warnings and provide a checksum through the distribution channel.
- Verify install, connect, bypass, disconnect, upgrade, and uninstall on both supported Windows versions. Uninstall must remove Finley's components and restore its network changes; ask before deleting saved user data.
- Distribute through a controlled download location. No backend infrastructure or recurring hosting is required for the client; server and code-signing costs depend on the operator and distribution choices.

## 10. Risks and next improvements

- **Per-app bypass is the main technical risk:** Windows process attribution, helper processes, DNS, IPv6, and network changes can complicate accurate routing. Verify actual behavior before promising coverage.
- **Compatibility:** Outline advanced and dynamic configurations are not equivalent to every ordinary Shadowsocks key. Explicitly show unsupported formats and cipher restrictions.
- **Privacy:** Bypassed applications expose their traffic to the normal network. Without a kill switch, connectivity may revert when the tunnel fails; make the behavior clear and test it.
- **Maintenance:** Networking drivers, privileged helpers, Windows updates, and unsigned binaries increase support costs. Pin and review dependencies, track upstream security fixes, and keep recovery simple.
- **Phase 2 candidates:** dynamic Outline keys, broader transport support, optional kill switch, tray controls, automatic updates, localization, and optional usage metrics only with explicit consent.

## References

- [Outline Client and Manager repository](https://github.com/OutlineFoundation/outline-apps)
- [Outline dynamic access keys documentation](https://developer.getoutline.org/vpn/management/dynamic-access-keys/)
- [Outline per-process split tunneling feature request](https://github.com/OutlineFoundation/outline-apps/issues/1206)

## Bypass scope update — 2026-10-30

- Bypass settings include applications, domains, and IPv4 addresses or CIDR ranges. Matching any enabled rule sends supported traffic through the regular connection.
- A domain such as example.com includes example.com and all its subdomains, but not unrelated names such as notexample.com. Normalize case and international domain names; reject URLs, ports, and wildcards.
- A single IPv4 address is stored as /32; CIDR input is normalized to its network address. IPv6 bypass is unavailable while the client blocks IPv6.
- Persist domains and CIDRs alongside the existing app list; older settings migrate to empty lists. Rule changes take effect after reconnecting.
- Domain matching uses visible hostnames from supported HTTP, TLS, and QUIC traffic. Hidden or encrypted hostnames and unsupported protocols may remain tunneled. Test root domains, subdomains, unrelated destinations, CIDR boundaries, restart persistence, and cleanup before release.

## Bypass implementation and validation — 2026-10-30

- Finley routes selected executable paths, domain suffixes, and IPv4 addresses or CIDR ranges through the regular network. A domain includes its subdomains. A single IPv4 address is represented by a /32 rule.
- Executable paths are matched case-insensitively and exactly. The direct outbound is bound to the active IPv4 default interface identified before connecting; reconnect after changing rules or network interfaces.
- DNS is handled by Finley before application rules. Domain matching relies on visible HTTP, TLS, or QUIC hostnames; hidden hostnames and unsupported protocols may remain tunneled. IPv6 bypass is unavailable in the current compatibility mode. Apps using separate helper or child processes may need additional executable rules.
- Controlled tests on this PC confirmed selected app, root domain, subdomain, and IPv4 CIDR traffic used the regular connection while unrelated traffic used the VPN. The user also confirmed Telegram, domain, and single IPv4 bypass work. Further testing across apps, protocols, DNS configurations, network changes, and Windows 10 and 11 remains required.
