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

## CHANGE-002 — demo first ✅ (demo) / real implementation in progress

Done so far: `tools/Gac.IconTool` + icon assets; shared contracts (exemption days, trusted clock, server shutdown,
overload); Console 캘린더 / 알림 / 서버 관리 screens; `Gac.ServerAdmin.UI` (GAC 서버 관리); everything wired to the demo.
The real server/agent/installer parts of CHANGE-002 are **not** implemented yet (the Console's HTTP client already calls
the planned endpoints, which the real server does not serve yet).

## Demo (GAC-Demo.exe) — what can be tried, where, and how

`GAC-Demo.exe` is one portable file (single-file, self-contained, no install, no admin rights, no network). Start it,
pick a view; switch at any time with **화면 전환** (server/client views) or **보기** (console view). Every view shows
"체험 모드 · 실제 PC에는 아무 변화도 없습니다". All three views share one fake school, so a change in one shows in the others.

### 관리자 화면 (콘솔) — admin icon
| Feature | How |
| --- | --- |
| Sign-up → Owner, then 회원가입 disappears | First visit shows the sign-up screen; any setup code works (or the one shown on the 서버 PC 화면). If the server PC view left the install "설치 미완료", sign-up is refused until [방화벽 열기] is done. |
| Accounts, roles, permission matrix, ownership transfer | 관리 → 계정 관리 (matrix includes **캘린더 관리** and **서버 PC 종료 설정**). |
| PC list, groups, aliases, revoke, 업무용 해제 | PC 목록 tab toolbar; 관리 → PC 그룹 관리. |
| 차단된 기기 / 확인 필요 | 관리 → 기기 관리 (sample laptops, a console-first PC, a UPS desktop awaiting approval). |
| All modules (clean, shutdown, errors, update, deploy, health, inspect, netdiag, classmode sync) | Select PCs → ▶ 선택한 PC에 작업 실행 (or the 작업 menu); results in 작업 결과 / 보고서. |
| Group policies (auto shutdown, clean) | 관리 → 그룹별 정책. |
| Shared presets, deploy packages, audit log | 공용 프리셋 / 배포 패키지 / 감사 로그 tabs (no file is read or written). |
| Period presets (기간제) with lock_pc | 관리 → 기간제 프리셋; also visible as bars in 캘린더. |
| **캘린더** with `기말고사 기간` and `내PC지키미 점검일` | 캘린더 tab: month view, ◀ ▶ 오늘. Click a bar → details (targets, who started it, [기간 중지]). Click a day → info / add / "이 날만 건너뛰기". [잠금 해제일 추가…]: name dropdown (내PC지키미 점검일) or free text; one-off / 매월 N일 / 매월 N번째 요일; 편집·삭제·이 회차 건너뛰기 on the right. All changes go to the audit log. |
| **알림** (overload alerts) | 알림 tab: sample alert, newest first, ● = unread, [읽음으로 표시] / [모두 읽음]; new alerts also appear as a Windows notification. |
| **서버 관리** | 관리 → 서버 관리: 서버 PC 자동 종료 (weekday on/off + time, exception dates, 오늘은 끄지 않기, 30분 연장) and 과부하 차단 (limits, blocked PCs, 차단 해제 / 영구 차단). |
| "서버 PC가 10분 후 종료됩니다." notice | On the 서버 PC 화면 press "자동 종료 10분 전 상황 만들기", then open the console view: the notice appears with [오늘은 끄지 않기] / [30분 연장]. |
| Rollout export, guided tour, tray preview, LED test | 관리 → 클라이언트 설치 파일 만들기 (explains, writes nothing); 보기 menu. |
| Easter egg | As in EASTER_EGG.md (click the top-left logo). |

### 서버 PC 화면 — GAC icon
| Feature | How |
| --- | --- |
| Server installer | Opens automatically the first time (or "서버 설치 과정 보기"): pages → **[다음]** (not 마침) → **방화벽 열기** (detected range + port editable, [다음] disabled until the rule is "created and verified") → **설치 완료** with address, port, range, setup code and the only **[마침]**. Cancel is hidden after installation. |
| "설치 미완료" | Close the installer before the firewall step and confirm: GAC 서버 관리 shows the big **"설치를 완료하려면 방화벽을 여세요"** panel; console sign-up is refused; the setup code appears only after [방화벽 열기]. |
| **GAC 서버 관리** – 상태 | Service start/stop/restart, address, port, LAN range, firewall status, [대역 다시 감지], [대역 추가], connected PCs, last enrollment, blocked count, setup code, [클라이언트 설치 파일 만들기]. |
| – 자동 종료 | Same setting as the console's 서버 관리; "자동 종료 10분 전 상황 만들기" shows the Admin PC popup with [오늘은 끄지 않기] / [30분 연장]. |
| – 과부하 차단 | Limits; "교실 PC 과부하 상황 만들기" blocks a PC (doubling for repeats) and sends an alert to the console's 알림; [차단 해제] / [영구 차단]. |
| – 로그 | Live fake log lines, level filter, [로그 폴더 열기] (explains only). |

### 클라이언트(교실 PC) 화면 — GAC icon
| Feature | How |
| --- | --- |
| Tray menu, class-mode preset editor + runner, network self-diagnosis | GAC icon in the fake taskbar, or the side buttons. |
| Auto-shutdown warning (30분 연장 / 지금 종료) | "자동 종료 경고 창". |
| Full-screen PC lock with countdown and admin unlock | "기간 잠금 실행 (PC 잠금 화면)". |
| **Exemption day** | Tick "오늘을 잠금 해제일로 체험 (내PC지키미 점검일)": an open lock window closes (explorer restart simulated) and the lock no longer appears; untick and it runs again. The toggle adds/removes a one-off day in the shared calendar, and a day added in the console calendar has the same effect. |
| Agent installer refusals | "에이전트 설치 화면 보기": **"노트북에는 설치할 수 없습니다"** and **"관리자 프로그램이 설치된 PC에는 클라이언트를 설치할 수 없습니다"**. |

## Known limitations
- Binaries are not code-signed (SmartScreen warning documented).
- Teacher personal presets in `%APPDATA%` are kept on uninstall (user data).
