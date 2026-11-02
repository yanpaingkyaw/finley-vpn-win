# Finley

Finley is a Windows desktop VPN client under development, based on `PROJECT_SEPC.md`.

**Development preview:** Finley uses sing-box through a privileged Windows service, with the desktop app running as the signed-in user. Automated configuration, transport, and lifecycle tests pass; the installed-service flow still requires standard-account, real-server, installer, and Windows 10/11 acceptance testing. Earlier real-server and bypass checks applied to the previous direct-helper implementation. There is no kill switch; traffic may use the normal connection when the tunnel stops. See [ticket #1 verification](docs/ticket-1-verification.md) for evidence and remaining checks.

## Architecture and security model

Finley uses a least-privilege architecture separating the unprivileged user interface from privileged tunnel operations:

```text
Renderer (sandboxed, context-isolated)
  -> validated Electron IPC
  -> Electron main process (normal user, asInvoker)
  -> unprivileged native pipe client (finley-service-client.exe, redirected stdio)
  -> authenticated local Windows named pipe (\\.\pipe\Finley.Tunnel.v1)
  -> Finley Tunnel Service (finley-tunnel-service.exe, LocalSystem)
  -> Windows Job Object (kill-on-close, breakaway disallowed)
  -> console helper (finley-tunnel-helper.exe) -> sing-box.exe
```

- **Standard-user execution:** Once installed by an administrator, the Electron desktop client runs with standard user privileges (`asInvoker`). No administrative rights or UAC prompts are required for daily operation.
- **Mutual pipe authentication:** Before forwarding credentials, the native client checks the pipe server PID against SCM's running service, its LocalSystem account configuration, expected executable path, and protected files. Standard clients need no LocalSystem token-query permission.
- **Caller token verification:** The service inspects the connected client's token, capturing user SID, logon-session authentication LUID, and session ID. Remote, anonymous, non-interactive, and session-0 callers are rejected.
- **Single-instance tunnel ownership:** Only one active machine-wide tunnel is permitted at a time. The tunnel is exclusively owned by the initiating logon session and connection. Other interactive sessions receive a generic busy status without disclosing server metadata or user profiles.
- **Job Object containment:** The helper waits for configuration on stdin. The service assigns it to a non-inheritable kill-on-close Job Object before sending configuration, so its engine cannot escape containment during startup. Service failure closes the job; helper/client loss initiates service cleanup.
- **Baseline route and DNS reconciliation:** After graceful engine shutdown, the service checks remaining adapters/routes and compares default routes/DNS with its protected preconnection baseline. Unconfirmed restoration blocks another connection. The non-secret baseline survives service crashes; managed credential strings cannot guarantee immediate memory erasure.

## Requirements and development

- 64-bit Windows 10 or 11.
- Node.js (v18+) and npm.
- Microsoft .NET Framework 4.8 (Windows built-in `csc.exe` compiler).
- Windows user-scoped secure storage (DPAPI).

### 1. Build native components

All native executables (`finley-tunnel-service.exe`, `finley-service-client.exe`, `finley-tunnel-helper.exe`, and test harnesses) are compiled with:

```powershell
npm run build:native
```

Alternatively, invoke `powershell -NoProfile -ExecutionPolicy Bypass -File native/build-service.ps1`.

### 2. Install the development service (one-time setup)

Because Electron runs as an unprivileged standard user, the privileged tunnel service must be installed in Windows Service Control Manager to create tunnels. To prevent LocalSystem from executing untrusted files directly out of a developer checkout, Finley provides an administrator setup script:

```powershell
# Open an Administrator PowerShell window:
powershell -NoProfile -ExecutionPolicy Bypass -File native/install-dev-service.ps1
```

This script:

1. Copies required binaries into an ACL-protected directory (`%ProgramFiles%\Finley Development\resources\bin`) owned by Administrators with inheritance disabled.
2. Registers and starts `FinleyTunnelService` running as `LocalSystem`.

### 3. Run the development app

From your **normal, non-elevated user prompt**:

```powershell
npm install
npm run dev
```

Electron automatically detects the development service and runs without administrative privileges.

Rerun the administrator development installer after native-code changes. Production and development share one service identity to prevent competing tunnels; uninstall one service before registering another location. The service starts idle and never connects on boot.

### 4. Run tests and production builds

```powershell
# Run the test suite:
npm test

# Build production frontend assets:
npm run build

# Package per-machine NSIS Windows installer:
npm run dist
```

`npm run dist` compiles native binaries, bundles production assets, and packages an installer configured with:

- `perMachine: true` (installed into `%ProgramFiles%\Finley`).
- `requestedExecutionLevel: asInvoker` (unprivileged app execution).
- `build/installer.nsh` hooks that automatically register and start `FinleyTunnelService` on install, and gracefully stop and remove it on uninstall.

Packaging runs the full test suite and writes installers to `release`, separate from Vite's `dist` assets. Only frontend assets and Electron code are selected for the app archive, preventing earlier packaged runtimes from being nested inside later builds. Existing installation directories are validated before extraction. Upgrade/uninstall stop and remove the prior service before replacing/removing files. Failed setup retains files for repair and reports an incomplete installation; full previous-version rollback is not provided. `npm run test:native` rebuilds and tests the native harness without installing a service or creating a tunnel.

## Service management and maintenance

The installed service executable provides administrator maintenance commands. Run these from its protected installation path, not from the checkout:

```powershell
# Run from an Administrator terminal:
& 'C:\Program Files\Finley\resources\bin\finley-tunnel-service.exe' --install
& 'C:\Program Files\Finley\resources\bin\finley-tunnel-service.exe' --stop
& 'C:\Program Files\Finley\resources\bin\finley-tunnel-service.exe' --uninstall
& 'C:\Program Files\Finley\resources\bin\finley-tunnel-service.exe' --acknowledge-cleanup
```

If an abrupt system crash or power outage leaves network routes or adapters unverified (`CLEANUP_UNCONFIRMED`), run `finley-tunnel-service.exe --acknowledge-cleanup` as administrator after confirming your network adapter and routes are restored.

Use the `Finley Development` path for the development service. `--install` repairs registration and starts it; `--stop` leaves it registered. Routine connection actions do not elevate the app. Missing, stopped, or incompatible services have actionable in-app errors.

Administrator acknowledgment requires no remaining active Finley adapter/routes, clears the saved recovery record, and restarts the service. It does not modify routes or DNS. Legitimate network changes can also cause a conservative baseline mismatch. Repair/uninstall refuse to silently discard an unconfirmed baseline.

Profiles stay in `%APPDATA%\finley-vpn\profiles.json` and are not removed by service repair. Keys saved under another administrator account must be re-imported under the intended user. Migration can remove the invoking user's old per-user installation; installations belonging to other accounts may need removal from those accounts.

## Networking and privacy

Finley saves profiles in the user's roaming application data folder and encrypts credentials with Windows DPAPI (`safeStorage`). Credentials are never exposed to the renderer, command-line arguments, or logs. The service has no profile database. Owner connection loss, heartbeat expiry, session logoff, and service stop initiate cleanup; locking the workstation alone does not disconnect.

The service generates the engine configuration internally from validated parameters, binding the TUN adapter and performing SOCKS-to-TLS connectivity probing against Google's 204 endpoint and two-half IPv4 route verification (`9.9.9.9` and `200.1.1.1`) before declaring the tunnel connected.

While connected, Finley currently returns empty AAAA answers for DNS queries and rejects IPv6 traffic inside the tunnel as a compatibility measure. IPv6-only destinations and IPv6 LAN access may not function. Browser secure DNS (DoH) or hard-coded addresses can behave differently; no IPv6 leak guarantee has been independently audited.

## Bypass: current approach and verification

Enable **Bypass** in the application to exclude specific applications, domains, or IP ranges from the VPN:

- **Applications:** Match normalized Windows executable paths case-insensitively. Application bypass binds direct traffic to the active IPv4 network interface discovered at connection time. Child processes spawned by an application may require separate rules.
- **Domains:** Match visible HTTP, TLS, and QUIC SNI hostnames (including subdomains, e.g., `example.com` covers `mail.example.com`). Traffic with encrypted or unavailable hostnames remains in the tunnel.
- **IPv4 addresses / CIDRs:** Direct routing for individual addresses (`192.0.2.1`) or CIDR subnets (`192.0.2.0/24`). IPv6 bypass ranges are not supported in the current IPv4 compatibility mode.

Bypass rules take effect upon connecting or reconnecting.

## Licensing

Finley's own source code is MIT licensed (see [LICENSE](LICENSE)). The Windows installer packages the unmodified sing-box engine (v1.14.4) under GPL-3.0-or-later with additional naming terms, and Wintun under its driver license. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for full licensing details, source code links, and compliance notes.
