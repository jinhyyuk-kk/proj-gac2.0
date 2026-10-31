# Progress

Status as of 2026-10-31. Automated checks: `dotnet build` (0 errors), `dotnet test` — 121 tests passing
(Shared 29, Server 8, Modules 43, Agent 13 incl. end-to-end, Console 28).
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

## Known limitations
- Binaries are not code-signed (SmartScreen warning documented).
- Teacher personal presets in `%APPDATA%` are kept on uninstall (user data).
