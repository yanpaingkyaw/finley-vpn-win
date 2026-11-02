# Ticket #1 implementation plan

Source: [Run Finley as a standard user using a privileged Windows service](https://github.com/yanpaingkyaw/finley-vpn-win/issues/1).

Prepared November 3, 2026. **Status: implemented in the working tree on `feat/standard-user-service`; installed Windows acceptance verification remains pending.** See [verification evidence](ticket-1-verification.md).

## Outcome and scope

After an administrator installs Finley, an ordinary Windows user can launch the app, manage settings, connect, and disconnect without UAC prompts. One machine-wide tunnel belongs to the initiating Windows user, logon session, and client connection. Closing that connection or logging off stops the tunnel.

Preserve full-device routing, encrypted per-user profiles, application/domain/IPv4 bypass, DNS behavior, and current IPv6 restrictions. Kill switch, automatic reconnect, unattended VPN, and connect-at-boot remain outside this ticket.

## Findings in this checkout

- `electron/network.cjs` formerly owned configuration generation, physical-interface discovery, helper startup, SOCKS/TLS probing, two-half IPv4 route verification, and shutdown. These responsibilities were moved together into the native service (`native/service/TunnelController.cs`, `Configuration.cs`, `NetworkChecks.cs`), while `electron/network.cjs` now delegates directly to `electron/service-client.cjs`.
- `native/FinleyTunnelHelper.cs` continues to provide hidden-console CTRL_BREAK shutdown and configuration over stdin, and is now supervised inside a non-inheritable Windows Job Object (`native/service/ProcessJob.cs`) managed by the service.
- `electron/main.cjs` decrypts profile secrets with safeStorage and reconciles service state on launch using `network.reconcile()`. It enforces single-instance locking (`app.requestSingleInstanceLock()`) and terminates cleanly on exit.
- `native/service/Configuration.cs` ports configuration generation and validation to C# with strict schema parity against `electron/network-config.cjs` and `electron/bypass.cjs`.
- `package.json` packages helper, client, and service binaries with `requestedExecutionLevel: "asInvoker"`, mandatory per-machine NSIS installation (`perMachine: true`), and custom installer hooks (`build/installer.nsh`). Native compilation runs via `npm run build:native`.
- Automated test suites (`tests/service-client.test.cjs`, `tests/service-native.test.cjs`, `tests/supervisor.test.cjs`) verify privileged IPC, token authentication, anti-spoofing, Job Object containment, and graceful shutdown.

## Implemented architecture

```text
Renderer (sandboxed, context-isolated)
  -> validated Electron IPC
  -> Electron main (normal user, asInvoker)
  -> unprivileged native pipe client (finley-service-client.exe, redirected stdio)
  -> authenticated local Windows named pipe (\\.\pipe\Finley.Tunnel.v1)
  -> Finley Tunnel Service (finley-tunnel-service.exe, LocalSystem)
  -> Windows Job Object (kill-on-close, breakaway disallowed)
  -> console helper (finley-tunnel-helper.exe) -> sing-box.exe
```

The system retains the .NET Framework compiler workflow (`csc.exe` targeting x64). Separate service (`finley-tunnel-service.exe`) and client bridge (`finley-service-client.exe`) executables share protocol definitions (`native/shared/Protocol.cs`) and Windows security helpers (`native/shared/WindowsSecurity.cs`).

The client bridge verifies the pipe server through Windows APIs before credentials leave Electron; ordinary Node pipe transport alone does not provide that verification. The bridge remains unprivileged and terminates its service connection on Electron stdin EOF, parent termination, or output failure.

The service generates the engine configuration from validated connection fields and bypass rules. It derives helper and engine locations strictly from its protected installation directory. Application bypass paths are match-only data and never become executable targets or filesystem operations.

## Delivery status and sequence

### 1. Freeze compatibility and define the protocol

**Status: code implemented; installed acceptance checks remain pending where applicable.**

Files: `native/shared/Protocol.cs`, `tests/fixtures/ServiceHarness.cs`, `tests/network-reference.cjs`.

- Defined protocol version 1 using four-byte little-endian length-prefixed UTF-8 JSON frames with a 1 MiB ceiling checked before memory allocation.
- Implemented strict framing and serialization rejecting unknown fields, duplicate keys, malformed JSON, invalid types, excessive nesting, and invalid request IDs.
- Commands implemented: `hello`, `connect`, `disconnect`, `getStatus`, and asynchronous `status` events with monotonic revisions.
- Responses correlate request IDs, report protocol version, and return safe typed results or stable error codes (`VPN_IN_USE`, `UNAUTHORIZED`, `INVALID_REQUEST`, `SERVICE_FAILURE`, `OPERATION_IN_PROGRESS`, `STARTUP_FAILED`, `CLEANUP_UNCONFIRMED`, `CANCELLED`, `ENGINE_EXIT`).
- Status events include lifecycle phase, monotonically increasing status revision, service availability, and cleanup confirmation; never credentials or raw configurations.
- Defined deadlines: 5-second handshake/status deadline, 150-second connect deadline, 30-second client disconnect deadline with a 20-second cleanup-check budget, with bounded write queues.

### 2. Build the service host and authenticated transport

**Status: code implemented; installed acceptance checks remain pending where applicable.**

Files: `native/service/Service.cs`, `native/client/Client.cs`, `native/shared/WindowsSecurity.cs`, `native/build-service.ps1`.

- Implemented `TunnelService` (`System.ServiceProcess.ServiceBase`) with bounded startup, shutdown, and session logoff tracking (`OnSessionChange`). Machine-wide state is kept inside the service; user profile stores are never read as LocalSystem.
- Created local pipe (`\\.\pipe\Finley.Tunnel.v1`) with an explicit DACL and `PIPE_REJECT_REMOTE_CLIENTS`. Interactive clients are granted read/write data rights while explicitly excluding `FILE_CREATE_PIPE_INSTANCE`.
- Reserved the first pipe instance using `FILE_FLAG_FIRST_PIPE_INSTANCE` and retained server ownership across connections.
- Authenticated clients from the connected pipe token via impersonation: captured user SID, logon-session authentication LUID, and session ID. Rejected network, anonymous, session-0, and non-interactive callers. Implemented token revert before executing privileged operations.
- The native client bridge (`finley-service-client.exe`) validates the pipe server PID against the active SCM service PID, verifies protected SCM LocalSystem account configuration and the expected executable path and protected `%ProgramFiles%` directory location before passing credentials.
- Concurrent requests from other logon sessions receive a generic `busy` status without leaking server endpoints, profiles, or owning user identity.

### 3. Move networking and process supervision into the service

**Status: code implemented; installed acceptance checks remain pending where applicable.**

Files: `native/service/TunnelController.cs`, `native/service/Configuration.cs`, `native/service/NetworkChecks.cs`, `native/service/ProcessJob.cs`, `native/service/Maintenance.cs`, `native/service/NativeTunnelBackend.cs`.

- Ported sing-box configuration generation and schema validation to C# (`Configuration.cs`), maintaining full parity with existing JavaScript fixtures. TUN names, probe credentials, and local SOCKS probe ports are generated inside the service.
- Implemented network discovery and route checks in C# (`NetworkChecks.cs`): outbound default interface discovery, SOCKS-to-TLS connectivity checks against Google 204 endpoint, and two-half IPv4 route verification (`9.9.9.9` and `200.1.1.1`).
- Implemented Windows Job Object isolation (`ProcessJob.cs`): starts the helper, which waits for configuration on stdin; assigns it to a non-inheritable kill-on-close Job Object before sending configuration. Service crashes close the job; helper crashes trigger service cleanup. This removes the child-spawn race while preserving the helper console behavior.
- Validated protected executable paths and rejected reparse points (symlinks/junctions).
- Managed machine-wide serialized lifecycle: `Disconnected` -> `Connecting` -> `Connected` -> `Disconnecting`, with `CleanupUnconfirmed` fault state on incomplete restoration.
- Preconnection network baselines (`finley-network-baseline.json`) record default routes and per-interface DNS settings to verify restoration on disconnect, logoff, or crash recovery.
- Provided maintenance operations in `Maintenance.cs`: `--install`, `--stop`, `--uninstall`, and `--acknowledge-cleanup`.

### 4. Integrate the normal-user Electron app

**Status: code implemented; installed acceptance checks remain pending where applicable.**

Files: `electron/service-client.cjs`, `electron/network.cjs`, `electron/main.cjs`.

- Refactored `electron/network.cjs` to delegate all tunnel lifecycle operations to `electron/service-client.cjs`. Direct spawning from Electron is completely removed.
- Electron main process retains safeStorage DPAPI decryption and profile persistence; transmitted payloads contain only necessary connection parameters and validated bypass rules.
- Added single-instance enforcement (`app.requestSingleInstanceLock()`) and startup service reconciliation (`network.reconcile()`).
- Correlated responses and status push events using request IDs and connection generations.
- Preserved context isolation, sandboxing, and narrow preload IPC boundaries. Bounded quit handler waits for clean disconnect if owning the active tunnel.

### 5. Install, upgrade, repair, and develop safely

**Status: code implemented; installed acceptance checks remain pending where applicable.**

Files: `package.json`, `build/installer.nsh`, `native/build-service.ps1`, `native/install-dev-service.ps1`, `README.md`.

- Configured `package.json` with `requestedExecutionLevel: "asInvoker"`, NSIS `perMachine: true`, and `allowToChangeInstallationDirectory: false` (locking install to `%ProgramFiles%\Finley`).
- Created NSIS installer hooks in `build/installer.nsh`:
  - `customInstall`: Invokes `finley-tunnel-service.exe --install`. Aborts install cleanly if service registration fails.
  - `customUnInstall`: Invokes `finley-tunnel-service.exe --uninstall` to cleanly stop active tunnels and deregister the service.
- Created `native/build-service.ps1` (`npm run build:native`) compiling service, client, helper, and test harnesses.
- Created `native/install-dev-service.ps1` for development: copies binaries to `%ProgramFiles%\Finley Development\resources\bin` with strict Administrator-only ACLs and starts the service, allowing `npm run dev` to run as a standard non-elevated user.
- Documented administrator commands (`--acknowledge-cleanup`, `--install`, `--stop`, `--uninstall`) in `README.md`.

### 6. Acceptance verification and test matrix

**Status: Automated test suite passing; manual VM acceptance matrix defined.**

#### Automated tests (41 passing checks)

| Test File | Coverage | Status |
|:---|:---|:---:|
| `tests/key.test.cjs` | Key parsing, unsupported URI handling, invalid inputs | Passed |
| `tests/network-config.test.cjs` | Config generation, bypass rules, cipher methods, IPv6 rejection | Passed |
| `tests/network-probe.test.cjs` | SOCKS-to-TLS probe validation against mock servers | Passed |
| `tests/network-route.test.cjs` | Two-half IPv4 routing verification and competing route diagnostics | Passed |
| `tests/service-client.test.cjs` | Service framing, chunked streams, handshake, client authentication, out-of-order correlation, multi-session busy mapping | Passed |
| `tests/service-native.test.cjs` | C# config parity, schema validation, JSON limits, pipe DACL/token auth, Job Object containment, lifecycle serialization | Passed |
| `tests/supervisor.test.cjs` | Helper private console, graceful STOP signal handling, pipe EOF shutdown | Passed |

#### Windows 10 & 11 x64 manual integration matrix (for final release qualification)

| Scenario | Objective | Verification Method | Status |
|:---|:---|:---|:---:|
| Standard user execution | Launch, connect, bypass, disconnect without UAC prompts | Clean Windows standard user account | Pending Release VM Run |
| Real Outline server | Verify end-to-end routing, DNS resolution, IPv6 rejection | Real Outline Shadowsocks server | Pending for installed-service implementation |
| Process termination & crash | Helper/service kill leaves no orphan engine; routes recover | Task Manager / Process Hacker kill test | Fake-engine containment passes; real networking pending |
| Multi-session contention | Second interactive user receives busy; cannot disrupt owner | Two concurrent Windows user sessions | Controller/pipe unit checks pass; genuine two-user run pending |
| Installer lifecycle | Clean install, upgrade, uninstall, rollback on service failure | Fresh Windows 10/11 VMs | Pending Release VM Run |
| Unconfirmed cleanup recovery | Service restarts into unconfirmed state; `--acknowledge-cleanup` restores | Abrupt reboot during active tunnel | Pending Release VM Run |

## Implementation references

- [Named-pipe security and access rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights): explicit ACLs and the overlap between generic write and pipe-instance creation rights.
- [CreateNamedPipe](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea): first-instance reservation and remote-client rejection.
- [GetNamedPipeServerProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeserverprocessid): server-process identification for the native client.
- [Windows Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects): inherited process containment and kill-on-close behavior.
- [electron-builder NSIS configuration](https://www.electron.build/v26/docs/nsis/): per-machine installer options and custom installer hooks.
