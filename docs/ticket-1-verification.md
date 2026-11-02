# Ticket #1 verification

Implementation branch: `feat/standard-user-service`. Keep the issue open until installed Windows acceptance checks are complete.

## Implemented

- LocalSystem service owns configuration generation, physical-interface discovery, route/connectivity verification, engine lifecycle, and cleanup checks.
- Versioned framed JSON protocol: bounded messages, strict validation, request IDs, deadlines, status revisions, cancellation, and heartbeat-bounded ownership.
- Explicit local pipe ACL, remote rejection, first-instance protection, token/session authentication, and client verification of SCM PID/account/path before credentials are forwarded.
- One tunnel per client connection/session; other clients receive generic busy status and cannot disconnect the owner.
- Kill-on-close Job Object contains the helper before configuration is sent, preserving its console/CTRL_BREAK behavior. This replaces the plan's suspended-start approach.
- Protected non-secret network baseline survives service crashes. Unconfirmed restoration blocks new connections and requires explicit administrator recovery when necessary.
- Electron retains per-user encrypted profiles and uses an unprivileged native bridge. No direct-engine fallback or automatic Electron elevation remains.
- asInvoker desktop app; per-machine fixed Program Files installer; directory checks before extraction; service install/start/upgrade/uninstall hooks; protected development installation.

## Local automated evidence

Run `npm run build:native`, `npm test`, `npm run build`, and `npm run dist` on Windows. Packaging includes compilation and the complete test suite.

The full local suite has 41 passing tests. Installer output is `release`, separate from Vite assets; archive inspection verifies that previous `win-unpacked` runtimes/installers are not included in `app.asar`.

Final local run (November 3, 2026, Windows build 10.0.26200): native compilation, all 39 tests, TypeScript/Vite build, and NSIS packaging passed. Packaged Electron source and all four native binaries match the working-tree inputs byte-for-byte. Desktop/client manifests specify asInvoker. The actual native client returned `SERVICE_MISSING` with empty stderr when no service was installed.

Artifact: `release/Finley Setup 1.0.2.exe`, 143,712,501 bytes. SHA-256: `b67e40bffac042e3aa9660ee291c5c2ae6e2b2ffb9d083f4537ccd0f849e6d44`. The application archive is 27,064,173 bytes and contains no previous packaged runtime or installer.

- Existing key, bypass, JavaScript configuration, route/probe reference, and helper graceful-shutdown tests.
- C#/JavaScript configuration parity across every cipher, interface binding, escaped executable paths, IDN domains, CIDRs, and DNS/IPv6 rule order.
- Native rejection of invalid types/settings, unknown fields, duplicate keys, trailing JSON, invalid numbers, and deep nesting without credential disclosure.
- Native pipe tests use the current Windows token to check caller identity/session, first-instance reservation, denied ordinary-user server-instance creation, spoofed-service rejection, and invalid requests leaving state unchanged.
- Native Job Object test starts a fake engine, kills its helper, and verifies engine termination.
- Controller tests isolate Windows operations to exercise ownership, concurrent requests, non-owner disconnect, unexpected exit, owner logoff, cancellation with deliberately late startup, repeated disconnect, monotonic revisions, and blocked reconnection after failed cleanup.
- Client tests cover fragmented/coalesced frames, invalid length/UTF-8/version, handshake ordering, out-of-order responses, stale revisions, service rejection, bridge failure, fresh generations, and honest service-loss status.

These tests do not install a Windows service or create a TUN adapter. The existing supervisor test runs sing-box with a SOCKS-only configuration and changes no routes.

## Installed acceptance checks — pending

### 1.1.0 client service-verification correction

After the user's installation retry, SCM reports `FinleyTunnelService` running as LocalSystem at the expected protected executable path. A native diagnostic from the normal-user token passed SCM account/type/PID/path and file ACL checks, then failed `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` with Windows error 5. The LocalSystem process's default DACL did not grant that query permission to interactive clients.

The service now sets its process DACL before opening the pipe: SYSTEM and Administrators retain full access; interactive clients receive only `PROCESS_QUERY_LIMITED_INFORMATION` (0x1000). Verification still compares the pipe server PID to SCM, checks account/configuration and protected paths, retains a process handle against PID reuse, and checks the running executable path. Verification failures now identify the failed stage and Windows error code.

A restricted native child-process test confirms executable-path querying works while process memory read/write/operation, termination, handle duplication, thread creation, information changes, and suspend/resume access are denied to the non-elevated test token. All 41 tests pass. A separate read-only hello request to the installed service succeeded with `disconnected`, `cleanupConfirmed=true`, and `engineReady=true`; no credentials or tunnel commands were sent by that diagnostic.

Replacement artifact: `release/Finley-Setup-1.1.0-service-fix.exe`. The user completed installation and reported the app working. A subsequent `service-harness.exe --verify-installed-service` run from the non-elevated automation token passed full installed-service identity verification and the authenticated hello exchange. This confirms the process-query permission fix against the running LocalSystem service. It does not replace real-server traffic, genuine standard-account, crash/recovery, or cross-version Windows acceptance testing.

Reference: [Windows process security and access rights](https://learn.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights).

### 1.1.0 installer error 87 correction

The user's first installation attempt reported error 87 and left no registered `FinleyTunnelService`. The registration code read `Marshal.GetLastWin32Error()` after protected-file ACL checks, rather than immediately after `OpenService`. A read-only native reproduction on this machine returned 1060 for the absent service, then 87 after the same permission checks. That stale code incorrectly aborted service creation.

`Maintenance.OpenExistingService` now captures and interprets the error immediately, treating only 1060 as an absent service and preserving other failures. Pipe/Job Object failure paths also preserve error codes before disposing handles. Maintenance failures identify the failing setup operation and Windows error text. The new native regression uses real SCM lookups and verifies that an invalid handle is not mistaken for an absent service.

The rebuilt replacement keeps application version 1.1.0 and uses `release/Finley-Setup-1.1.0-fixed.exe` so an open failed installer need not be overwritten. Native compilation, all 40 tests, and the frontend build pass. An administrator installation retry is still required; the automation token cannot perform an elevated installation.

Reference: [Windows last-error handling](https://learn.microsoft.com/en-us/windows/win32/Debug/last-error-code).

The automation token is not elevated. The user performed the administrator-approved installation; automation performed read-only installed-service checks and did not change privileged networking.

| Scenario | Required evidence |
| --- | --- |
| Windows 10/11 x64 genuine standard accounts | Administrator install, original-user normal-token post-install launch, connect/disconnect/settings without further UAC |
| Real-server parity | Full-device traffic, app/domain/subdomain/CIDR bypass, DNS, IPv6 restrictions, competing routes |
| Crash/session lifecycle | Engine/helper/Electron/bridge/service failure, hung client, owner logoff, shutdown/reboot; no orphan process and verified adapter/routes/DNS recovery |
| Cross-session security | Two genuine users, session-0/network callers, unauthorized access, file/service modification attempts, spoofing, inherited pipe handles |
| Repeated/cancelled commands | Startup/probing/shutdown races, no duplicate engines or stale connected status |
| Installer lifecycle | Clean install, legacy migration, other-account admin credentials, cancelled/failed install, same-version repair, upgrade/uninstall while connected, marked-for-deletion service |
| Recovery faults | Helper timeout, forced termination, baseline mismatch/network change, failed route/DNS checks, unavailable service, administrator acknowledgment |

Record OS build, account/session type, application build, expected/actual result, process/network evidence, and residual failures for each run. Exclude credentials and complete configurations.

## Delivery differences and limits

- Production and development use one service/pipe identity to prevent competing tunnels. Uninstall one service before registering another location.
- Client disconnect responses allow 30 seconds; cleanup checks have a 20-second budget with bounded helper/job waits. Service discovery/verification precedes the protocol handshake.
- LocalSystem identity is established through protected SCM account configuration, the running service PID, and executable path; standard clients need no LocalSystem token-query permission.
- Failed setup disables/removes a newly created service and retains files for repair. Full previous-version binary rollback after failed upgrade is not implemented; verify this failure mode before release.
- Network changes can conservatively trigger a baseline mismatch. Administrator acknowledgment clears the record after verifying no Finley adapter/routes remain; it does not repair arbitrary networking.
- Binaries are unsigned development artifacts. Signing and installed Windows/network acceptance remain outstanding.
