# Decisions

One line each: decision — reason.

- All projects target `net10.0-windows` (Windows-only product) — simplifies use of Windows APIs; CA1416 suppressed globally.
- Data access: `Microsoft.Data.Sqlite` + `Dapper`, no EF Core — small footprint and no migrations tooling on the Admin PC.
- Server TLS certificate: RSA-2048 self-signed, 10-year validity, SAN = host name + all IPv4 addresses; pin = SHA-256 of the DER certificate — no CA available on a school LAN.
- Enrollment proof: server answers `HMAC-SHA256(code, thumbprint || nonce)` before the client sends the code — prevents a LAN MITM from learning the code or substituting its certificate.
- Commands are signed when delivered to the polling agent, not when created — otherwise a PC that is off for more than 5 minutes would reject every queued command.
- Replay protection: agent persists the IDs of the last 2,000 executed commands plus 5-minute expiry — IDs older than the expiry window are rejected anyway.
- Shared class-mode presets are signed with an ECDSA P-256 server key (public key given to agents at enrollment) — the tray runs in user context and must not hold the agent's HMAC secret.
- Default server port: 8443 — unprivileged, unlikely to collide on a desktop PC.
- Default poll interval 30 s with ±20% jitter; result upload backoff 30 s doubling to 15 min — keeps the Admin PC load low.
- `run command` from old GAC lives inside the `deploy` module (action `run_script`) — the spec groups script execution under Batch Deploy & Run.
- Auto clean: Downloads is always `<profile>\Downloads` (relocated Known Folders are ignored) — SYSTEM cannot reliably resolve per-user Known Folders; conservative.
- Auto clean: Recycle Bin is emptied by deleting entries under `<drive>:\$Recycle.Bin\<SID>` — works from SYSTEM for all users and supports dry-run.
- Auto clean: reparse points (symlinks/junctions) are never followed or deleted — safest interpretation of the whitelist rule.
- Auto shutdown uses `shutdown.exe /s /t 0` (normal), then `/s /f /t 0` after the grace period — documented flags; `/t 0` does not imply `/f`.
- Updates: GAC never reboots automatically at all (only reports reboot-required) — strictly satisfies "never reboot during school hours".
- Net-diag: user proxy reset is done by the tray itself (it is the user's own HKCU setting); DNS flush / DHCP renew / DNS-to-DHCP reset / Winsock reset go through the service enum.
- Net-diag: added `ResetDnsToDhcp` repair, applied only when DNS fails and the adapter already gets its IP from DHCP — needed to fix a deliberately broken static DNS setting.
- Deploy packages are uploaded to the server through the API and stored under `%ProgramData%\GAC\Server\packages\<sha256>` — content-addressed, hash checked on both sides.
- Console session token stored only in memory (re-login each launch) — no secrets on disk on the operator PC.
- Demo export buttons do not write files — "nothing touches the real machine" in demo mode.
- Clean runs as SYSTEM in user-writable folders, so it pins every directory by handle (no share-delete), checks reparse flags on the handle, verifies final paths and deletes through the same handle; read-only files are skipped — prevents junction-swap races (security review).
- Re-enrollment reuses a PC record only with an HMAC proof made with that record's current agent key; otherwise a new record is created (an old offline duplicate can be deleted in the console) — prevents identity takeover (security review).
- `%ProgramData%GAC` and its Agent/Server folders get a protected SYSTEM+Administrators DACL and are recreated if a non-admin pre-created them — protects agent keys, queue and server data (security review).
- Package-install commands are rebuilt on the server from the stored package record (file, hash, silent args) — Operators cannot inject installer arguments for SYSTEM installs (security review).
- Operators see only command batches/policies of their assigned groups; Operators also only see PCs in their groups.
- First-run Owner creation requires the enrollment code shown only on the Admin PC — prevents a LAN race to claim the Owner account (security review).

## CHANGE-001
- Desktop-only and install-order are decided only by the server (`DeviceGuard`) from raw probe values; client checks are defense-in-depth and fail closed.
- Hardware fingerprint = SHA-256 of sorted wired MACs (else manufacturer+model+name); shared by agent/console/server (`HardwareId`) so the same machine maps to one id.
- ProbeD reads LidPresent from GetPwrCapabilities via an over-sized native buffer (offset 2) and the battery signal from GetSystemPowerStatus — avoids SYSTEM_POWER_CAPABILITIES struct-size corruption across Windows revisions.
- Module pre-run laptop check (site #5) is implemented in the destructive Clean module (own SMBIOS probe); documented as the representative per-module site rather than duplicating into all nine.
- Tray startup trusts a server-approval marker over a local battery signal, so a desktop with a UPS is not blocked once the server has accepted it (reconciles probe E with the server's battery-review rule).
- Setup code (single-use, first sign-up = Owner) is separate from the agent enrollment secret; sign-up fails closed when no setup code is provisioned.
- Work-PC lock exemption is granted only by a staff (Owner/Admin) Console login from that fingerprint, never by a Viewer/Operator — a student cannot exempt a classroom PC.
- Revoked PCs keep the revoked flag across re-enrollment; every heartbeat must carry a device report (missing = revoke).
- Permission matrix is Owner-editable and resolved into each session on every request; Owner-only powers (account/server management) can never be granted to another role.
- `lock_pc` is stripped from a period preset by the server before delivery to a work PC; presets are signed with the server ECDSA key so the agent can trust a preset containing lock_pc.
- Missed scheduled shutdowns are skipped (1-minute tolerance) and never caught up at boot.

## CHANGE-002
- Icon sources found in ./icon (repo root): GAC_ADMIN_SETUP.exe → gac-admin.ico (9 frames, first RT_GROUP_ICON); gaclogo.png (only image, transparent PNG) → gac.ico — ./icon is git-ignored.
- IconTool extract test injects a known icon group into a copy of the test host with UpdateResource — a self-contained multi-size sample exe without committing binaries.
- Demo first (owner order 2026-11-01): CHANGE-002 is built and tried in GAC-Demo.exe before the real server/agent/installer work.
- Exemption "Nth weekday": N = 1–4 plus 5 = "마지막" (last) — covers "last Friday" without a separate rule type.
- Exemption expansion and lock suppression use Asia/Seoul via "Korea Standard Time" (fallback fixed UTC+9) — the day boundary must not depend on the PC's time zone setting.
- Trusted clock after a reboot with the server unreachable: the wall clock is accepted only within [last server time − 10 min, last server time + 1 day]; otherwise the last server time is used — the monotonic counter restarts at boot, so this is the conservative choice (locks stay in force).
- GAC 서버 관리 UI lives in a UI-only library Gac.ServerAdmin.UI behind IServerAdminBackend — the demo drives the real window without referencing Gac.Server.
- Console screens: 캘린더 and 알림 are main-window tabs; 서버 관리 is a dialog under 관리 — tabs for things checked daily, a dialog for settings.
- Server-shutdown schedule editor and overload editor live in Gac.Tray.UI — shared by the Console and the server-management app.
