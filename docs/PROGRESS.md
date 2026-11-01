# Progress

Status as of 2026-11-01. Automated checks: `dotnet build` (0 errors), `dotnet test` — 199 tests passing
(Shared 29, Server 54, Modules 45, Agent 17 incl. end-to-end, Console 35, Tamper 19).
`build/build-installers.ps1` produces `dist/GAC-Server-Setup.exe`, `GAC-Console-Setup.exe`, `GAC-Agent-Setup.exe`, `GAC-Demo.exe`.

Items marked **manual** need real hardware or a clean VM and have **not** been verified yet.

## Phase 1 – Skeleton ✅ (automated) / manual pending
- Solution layout, Shared contracts, module interface, Agent service, Tray, Server, Console.
- Accounts and roles (Owner/Admin/Operator/Viewer), PBKDF2 600k, lockout, 8 h sessions, audit log, server-side checks.
- Enrollment with HMAC proof bound to the pinned self-signed certificate; HMAC-signed commands (5 min expiry, replay guard).
- First modules: `errors`, and `deploy.run_script` (old "run command").
- Verified by `EndToEndTests`: real HTTPS server on loopback → agent enrolls → appears in `/api/pcs` → signed `run_script` and `errors.collect` run → results and audit visible through the console API.
- **Manual:** one physical test PC with the installed service and the Console UI.

## Phase 2 – Auto maintenance ✅ (automated) / manual pending
- `clean`: hard-coded whitelist, dry-run default, handle-pinned deletion (junction-swap safe), tests with real junctions.
- `shutdown`: schedule per group, T−5 popup with 30분 연장 / 지금 종료, extension limit, no popup without a user, normal then forced shutdown, logged.
- **Manual:** dry-run report on a test PC; scheduled shutdown with extend on a real session.

## Phase 3 – Management ✅ (automated) / manual pending
- `update` (WUA COM, no auto reboot, unreachable source reported), `deploy` (LAN package store, SHA-256, server-side payload),
  `health` (volumes, disk reliability, RAM, uptime, boot time), `inspect` (wallpaper, LED test, update/edition, allowlist + uninstall, CSV export).
- **Manual:** each module on two test PCs; every install method under LocalSystem on a real PC (CLAUDE.md 4.7).

## Phase 4 – Teacher features ✅ (automated) / manual pending
- `classmode`: tray preset editor (fixed action list), runner (continues on failure), login trigger, desktop shortcuts,
  ECDSA-signed shared presets (only they may contain `run_command`).
- `netdiag`: ordered checks, APIPA detection, flush DNS / DHCP renew / DNS-to-DHCP / proxy reset, Winsock reset only after consent, report to console.
  Unit test: broken DNS is fixed by flush → reset-to-DHCP.
- **Manual:** teacher builds a preset without help; netdiag fixes a deliberately broken DNS on a real PC.

## Phase 5 – Ship ✅ (built) / manual pending
- Inno Setup installers (Korean, language file vendored in `installer/lang`), silent agent install with `/SERVER` `/CODE`,
  upgrade in place, uninstall removes services, firewall rule, files; server keeps data unless "데이터도 삭제".
- `GAC-Demo.exe` single file; smoke-tested: it starts and stays running on the development PC. Demo safety tests pass.
- `LICENSE` (MIT, JH, 2026); `docs/INSTALL.md`.
- **Manual:** clean Windows 10/11 VM without internet: demo from USB, install all three from `/dist`, uninstall leaves nothing behind.

## CHANGE-001 ✅ (automated) / manual pending

Spec archived at `docs/changes/CHANGE-001.md`. All items applied; 199 automated tests passing.
- **Desktop-only, server-enforced (1.1–1.3):** five hardware probes + report collector; server `DeviceGuard` decides at
  enrollment, heartbeat and command dispatch; fails closed on missing/inconsistent data; integrity-hash check; wired-only
  enrollment; permanent block for mobile/portable; battery-only → "확인 필요"; per-agent key bound to fingerprint; revoke.
- **Distributed client checks + tamper test (1.1, 8):** installer (A+D), service startup (B+C), tray startup (A+E),
  Clean module pre-run (SMBIOS), enrollment collector (all five), heartbeat report. `Gac.Tamper.Tests` fails if any site is removed.
- **Install order (1.4):** console markers + tombstone (Console installer), agent installer refusal, server enforcement.
- **Accounts (4–5):** single-use setup code, first sign-up = Owner (transactional/concurrent-safe), sign-up closes,
  ownership transfer, Owner-editable permission matrix (effective next request), unlock authorization + permission.
- **PC aliases (3):** server-side alias, never renames Windows; shown large with the Windows name.
- **Period presets + lock_pc (2.3–2.4, server):** PeriodService create/stop/deliver, ECDSA-signed, `lock_pc` stripped
  for work PCs; `lock_pc` rejected in teacher presets; unlock endpoint authorizes on the server and audits the account.
- **Missed shutdown (2.5):** skipped and logged, never caught up at boot.
- **Server LAN range + firewall (2):** `LanDetection` CIDR detect; `netsh` rule for exactly that range + port, fail-closed
  to localsubnet; server CLI `install --port --range`, `detect-range`, `apply-firewall`. **GAC 서버 상태** app (6) with
  service controls, range re-detect/add, rollout export. Rollout package (`gac-agent.json`) export (2).
- **Client no-login + per-PC presets (2.1):** presets/periods stored per PC under ProgramData (DACL-locked), no client login.
- **Tray watchdog (2.2):** the SYSTEM service relaunches the tray (`CreateProcessAsUser`) when no verified tray is connected;
  lock/preset state in the service survives tray kill, reboot, and the server being offline.
- **`lock_pc` client execution (2.4):** full-screen lock window with the fixed text + live countdown, admin-unlock dialog
  via pipe → service → server; countdown-to-zero requests shutdown.
- **Console UI (3–7):** sign-up/login screen, account dashboard + permission-matrix editor + ownership transfer,
  기기 관리 (차단 / 확인 필요), alias editing, period create/stop, 업무용 PC handling, rollout export.
- **Demo three-view (7):** picker + 화면 전환 menu over one shared fake server — console (sign-up→owner), server PC
  (fake installer + GAC 서버 상태), client PC (tray, lock, agent-install refusals).

Remaining: **manual** verification only (real hardware / clean VM) — hardware probe accuracy on real laptops & desktops,
firewall rule on a real LAN, tray watchdog and lock across reboot, silent rollout install.

## Known limitations
- Binaries are not code-signed (SmartScreen warning documented).
- Teacher personal presets in `%APPDATA%` are kept on uninstall (user data).
