# GAC 2.0 — Project Instructions for Claude Code

> Place this file at the repository root as `CLAUDE.md`. Claude Code loads it automatically at the start of every session.
> Owner: 진혁 (NeoFlows). Everything in this document is already decided. **Do not ask the owner questions — start working immediately** and follow this document unless a later order overrides it.

---

## 1. What we are building

GAC 2.0 is a **school PC management platform** that replaces the old GAC and GAC LITE with one system.

- One **Agent** is installed on every school PC (classrooms, staff room).
- One **Server + Console** is used by the owner to manage all PCs from a single screen.
- Every feature is a **module** that plugs into the Agent. New features = new modules, never new programs.

All user-facing text (tray menus, console UI, popups, log messages shown to people) must be in **Korean**. Code, identifiers, comments, and commit messages are in English.

---

## 2. Final module list (9 modules)

These are confirmed by the owner. Do not add or remove modules without an explicit order.

### A. Maintenance (runs automatically)

| Module | ID | What it does |
| --- | --- | --- |
| Auto Clean | `clean` | Deletes temp files, browser caches, Recycle Bin contents, and old files in Downloads. |
| Auto Shutdown | `shutdown` | Shuts PCs down at a scheduled time after a warning popup with an "extend" button. |

### B. Teaching convenience (teacher-facing, via tray icon)

| Module | ID | What it does |
| --- | --- | --- |
| One-Click Class Mode | `classmode` | **Fully user-configurable presets.** Teachers build their own presets from a list of actions and run them with one click. |
| Network Self-Diagnosis | `netdiag` | Checks IP / gateway / DNS / GAC Server reachability / proxy, tries safe automatic repairs, reports failures to the console. |

### C. Management & inspection (owner-facing, via console) — carried over from old GAC / GAC LITE

| Module | ID | What it does |
| --- | --- | --- |
| Error Check | `errors` | Collects error-level events and failed services from each PC. |
| Batch Update | `update` | Reports Windows Update status and triggers updates in bulk. |
| Batch Deploy & Run | `deploy` | Installs programs and runs commands/scripts on selected PCs. |
| PC Health Report | `health` | Reports disk free space, disk health, RAM usage, uptime, boot time. |
| End-of-Term Inspection | `inspect` | Old GAC LITE: reset wallpaper to default, monitor / interactive-whiteboard LED test pattern, update & Windows edition check, unwanted-program cleanup list. |

**Removed by the owner — do not implement:** desktop restore, automatic account logout, wallpaper timetable/notices, printer queue cleanup.

---

## 3. Architecture (decided)

```
Operator PC                 Admin PC                       Client PCs (every school PC)
[Console .exe]  ──LAN──>  [GAC Server]  <──LAN poll──  [Agent Service (SYSTEM)] ──named pipe── [Tray App (user session)]
```

**No internet.** GAC runs entirely inside the school LAN. No component may require internet access, call external URLs, use CDNs, cloud services, telemetry, or online package feeds at runtime. Everything ships as local `.exe` files.

| Component | Runs on | Tech (default) | Responsibility |
| --- | --- | --- | --- |
| **Console** | Operator PC (owner) | **Desktop `.exe`** — .NET 10 WinForms | Owner UI: PC list, send commands, view reports, manage shared presets and deploy packages. Talks to the GAC Server over the LAN. |
| **GAC Server** | Admin PC — **an ordinary Windows 10/11 desktop PC** | .NET 10 (LTS) Windows Service, ASP.NET Core minimal API (Kestrel), SQLite | Stores PCs, commands, results, logs, presets. Hosts the deploy package store. Decides desktop-vs-laptop and install-order for every client. No web UI. |
| **GAC 서버 상태** | Admin PC (same PC as the server) | .NET 10 WinForms `.exe` | Local admin window installed with the server: shows service state and live numbers, starts/stops the service, re-detects or adds the LAN range, and exports the client rollout package. Talks only to the local service/DB — it is not a second console. |
| **Agent Service** | Windows Service, `LocalSystem` | .NET 10 Worker Service (`UseWindowsService()`) | Runs privileged modules, polls the server, executes signed commands. Keeps the Tray App running (watchdog) and holds lock/preset state so killing the tray clears nothing. Client PCs have **no login**. |
| **Tray App** | Logged-in user's session | .NET 10 WinForms `NotifyIcon` | Runs user-context work (class mode, per-user cleanup, popups) and the full-screen period lock. Talks to the Agent Service over a named pipe. Always running: no "exit" item; the service relaunches it if it disappears. |

Rules:

1. **Agents only make outbound connections.** Agents poll the server; the server never connects to agents. No inbound firewall ports on school PCs.
2. **Agents work offline.** If the server is unreachable, scheduled local modules (`clean`, `shutdown`) still run, and results are queued and uploaded later.
3. **User-context work runs in the Tray App, never in the SYSTEM service.** Teacher presets therefore never get admin rights.
4. **The Tray App may only ask the service for a fixed, enumerated set of privileged actions** (e.g. `NetRepair.FlushDns`). Never pass free-form commands over the pipe.
5. Use .NET 10 (LTS, supported until November 2028). Do not target .NET 8 or 9 — both reach end of support in November 2026.
6. **LAN-only transport.** Use TLS with a self-signed certificate generated by the GAC Server at install time; agents and the console pin that certificate's thumbprint during enrollment. No public CA, no internet-based validation.
7. **Offline deployment.** Publish every component as a self-contained `.exe` (`dotnet publish -r win-x64 --self-contained`) so school PCs do not need the .NET runtime downloaded from the internet. All NuGet packages are restored on the development PC only, never on school PCs.
8. **Clients are desktop PCs only; the server decides.** Laptops are blocked. The decision is made **on the server** from several hardware probes the agent reports (system type, chassis, firmware, lid, battery); the client-side checks are only defense-in-depth and fail closed. **No subnet- or IP-based device decisions** — being on a given subnet never makes a PC a client or a laptop.
9. **Install order is one-way.** A PC that ever had the Console installed can never become a client, even after the Console is uninstalled (a tombstone records it). Installing the Console on a client-capable desktop is still fine.
10. **The server opens only the detected LAN range + port.** At install the server auto-detects the school LAN range (CIDR) and opens the firewall for exactly that range and the one port, fail-closed to the local subnet if detection fails. Only the Admin PC opens an inbound port; client PCs open none.
11. **The Tray App always runs in the background.** It has no "exit" item, and the SYSTEM service relaunches it (in the logged-in user's session) if it is missing. Lock and preset state live in the service and survive a tray kill, reboot, or the server being offline.

**The school has no server hardware.** The Admin PC is a normal desktop PC like every other school PC: Windows 10/11 (not Windows Server), no Active Directory, no domain, no WSUS, no IIS, no SQL Server. It may be turned off at night and may be used for other work during the day. Therefore:

- The GAC Server must install with one `.exe` and run as a normal Windows Service on Windows 10/11.
- Keep its CPU/RAM use low; SQLite only, no external database.
- Agents must handle the server being off (queue results, retry with backoff).
- Do not rely on any Windows Server feature.

---

## 4. Module specifications

### 4.1 `clean` — Auto Clean

- **Triggers:** user logoff / system shutdown, scheduled time, console command.
- **Whitelist of targets (hard-coded, nothing else is ever deleted):**
  - `%WINDIR%\Temp`
  - Each user's `%LOCALAPPDATA%\Temp`
  - Chrome and Edge cache folders under each user profile (`...\User Data\<profile>\Cache`, `Code Cache`) — **only if that browser is not running**
  - Recycle Bin
  - Each user's `Downloads` — **only files older than the retention period** (default 14 days, configurable)
- Skip files that are locked or in use; never fail the whole run because of one file.
- Never follow symlinks or junctions out of a whitelisted folder.
- **Dry-run mode** lists what would be deleted without deleting. Dry-run is the default for the first deployment.
- Report: bytes freed, file count, skipped count, duration.

### 4.2 `shutdown` — Auto Shutdown

- Schedule set per PC group in the console (e.g. weekdays 17:30).
- At T−5 minutes the Tray App shows a Korean warning popup with **"30분 연장"** and **"지금 종료"**.
- Extensions are allowed up to a configurable limit. If nobody is logged in, shut down without a popup.
- Use a normal shutdown (not forced) first; force only after a configurable grace period.
- Log whether the shutdown happened, was extended, or was cancelled.
- **Missed schedules are skipped, never caught up.** If a PC is off at the scheduled time, it does **not** shut down at the next boot; the agent logs the slot as "skipped". Only a PC running at the scheduled time runs the shutdown flow.

### 4.3 `classmode` — One-Click Class Mode (fully configurable)

Teachers create, edit, and delete their own presets in a Tray App editor. Each preset is an ordered list of actions plus a trigger.

**Allowed action types (teachers can only pick from this list):**

| Action | Parameters |
| --- | --- |
| `set_volume` | 0–100 |
| `mute` / `unmute` | — |
| `display_mode` | `clone` / `extend` / `internal` / `external` (via `DisplaySwitch.exe`) |
| `set_resolution` | width, height (validate against supported modes before applying) |
| `open_url` | URL (http/https only) |
| `launch_app` | executable path + optional arguments (runs with the user's own rights) |
| `close_app` | process name |
| `wait` | seconds (max 30) |
| `lock_pc` | — (**console-distributed period presets only**, never a teacher action) |

- **Triggers:** manual (tray menu or desktop shortcut) or automatically at user login.
- Presets are stored as JSON **per PC** (client PCs have no login), not per user. Validate the schema on load; reject invalid presets with a Korean error message.
- The owner can push **shared presets** from the console. Only console-distributed presets may contain a `run_command` or `lock_pc` action, and only if signed by the server key.
- **Period presets (기간제):** the Console can run a preset for a fixed period (start + N days) on chosen PCs/groups. While active, the server includes it in the agent's poll response; the agent runs it, and a `lock_pc` action shows the full-screen lock (`지금은 "{기간 이름}" 이기에 사용이 불가합니다.`) with a live countdown. A period can be stopped early from the Console.
- **Work-PC exemption:** `lock_pc` is stripped **by the server** for any PC that has had a successful Console login from its hardware fingerprint (so a staff/operator PC is never locked). A PC where the Console was installed but never logged in is still locked. The Owner can remove the exemption.
- Every action runs in the Tray App (user context). Failure of one action is logged and the preset continues.

### 4.4 `netdiag` — Network Self-Diagnosis

- Started by the teacher from the tray menu.
- **Checks in order:** adapter up → valid IP (detect `169.254.x.x` APIPA) → gateway ping → DNS resolution → GAC Server reachable on the LAN → proxy settings. Checking an external internet address is **off by default** and only runs if the owner sets a target address in the console.
- **Safe automatic repairs** (requested from the service through the fixed action enum): DNS flush, DHCP release/renew, reset user proxy settings. Repairs that need a reboot (e.g. Winsock reset) must ask the user first.
- Show results in plain Korean ("네트워크 연결 정상", "DNS 문제를 고쳤습니다" …).
- If still failing, send the full diagnostic report to the console.

### 4.5 `errors` — Error Check

- Collect Error/Critical events from the System and Application logs since the last report.
- Collect services set to Automatic that are not running.
- Deduplicate and summarize before sending.

### 4.6 `update` — Batch Update

- Report pending updates, last successful install time, and reboot-required state using the Windows Update Agent API.
- Console can trigger "search + install" on selected PCs. Updates come from whatever source Windows on each PC is already configured to use; GAC itself never downloads anything from the internet. Do not assume WSUS exists. If no update source is reachable, report that clearly instead of failing silently.
- Never reboot automatically during school hours.

### 4.7 `deploy` — Batch Deploy & Run

- Console sends a signed job: install package or run script, target PCs, timeout.
- **Packages are local files only.** The owner adds an installer (`.exe` / `.msi`) plus its silent-install arguments in the console; it is stored on the Admin PC and agents download it from the GAC Server over the LAN. No winget, no online package feeds, no download URLs.
- Verify the hash (SHA-256) of every downloaded package before running it.
- Test each install method under `LocalSystem` on a real PC before marking it done.
- Capture exit code, stdout/stderr (truncated), duration. Show per-PC results in the console.

### 4.8 `health` — PC Health Report

- Disk: free space per volume, health/reliability counters where available.
- RAM usage, uptime, last boot duration.
- Send on a schedule (default every 6 hours) and on demand. Flag thresholds (e.g. free space < 10%) in the console.

### 4.9 `inspect` — End-of-Term Inspection (old GAC LITE)

Run from the console or locally by the owner. Produces a pass/fail checklist per PC:

1. Wallpaper reset to the school default.
2. Monitor / interactive whiteboard LED test: full-screen solid colors (red, green, blue, white, black) the owner advances by click or key.
3. Windows Update status and Windows edition check.
4. Unwanted-program list (compared against a console-managed allowlist) with optional removal.

Results are stored on the server and exportable as a table.

---

## 5. Operator accounts & permissions

The Console requires login. Accounts and roles live in the GAC Server database.

**Roles (fixed set):**

| Role | Korean label | Can do |
| --- | --- | --- |
| `Owner` | 최고 관리자 | Everything, including managing accounts, roles, and server settings. Exactly one Owner must always exist. |
| `Admin` | 관리자 | All modules on all PCs, manage PC groups and shared presets. Cannot manage accounts or server settings. |
| `Operator` | 운영자 | Run modules on the PC groups assigned to them. Cannot run `deploy` scripts or uninstall programs. |
| `Viewer` | 조회 전용 | View PCs, reports, and logs only. |

Rules:

- **Sign-up / login (first run):** the Console shows a **회원가입** (sign-up) screen when no account exists. The **first sign-up becomes the Owner**; it requires the single-use **설치 코드 (setup code)** shown at the end of server setup (and saved in `enrollment.txt`). After the first account exists, sign-up closes — further sign-ups are refused on the server (403), and new accounts are added only from the account dashboard. No default password ever ships, and two concurrent first sign-ups still yield exactly one Owner.
- **계정 관리 dashboard:** the Owner can add, edit, disable, delete accounts, reset passwords, change roles, and assign PC groups. The Owner **cannot delete or demote themselves**; exactly one Owner always exists.
- **Permission matrix:** the Owner can edit a role → permission matrix (within the editable permissions) from the dashboard; the change takes effect on the **next request** because every check is on the server. `Operator` can unlock a period-locked PC by default (the "PC 잠금 해제" permission).
- **Ownership transfer:** the Owner can hand ownership to another account; afterwards there is still exactly one Owner (the old Owner becomes `Admin`).
- Every permission check happens **on the server**, not only in the Console UI.
- Passwords: PBKDF2-SHA256 via `Rfc2898DeriveBytes.Pbkdf2` with a per-user random salt and at least 600,000 iterations. Never store or log plain passwords.
- Lock an account for 5 minutes after 5 failed logins. Sessions expire after 8 hours of inactivity.
- **Audit log:** every login, account change, command, and unlock records who, what, which PCs, and when. Owner and Admin can view and export it.

## 6. Packaging & installers

Everything ships as **installer `.exe` files** built on the development PC. School PCs never build anything.

| Installer | Installs | Notes |
| --- | --- | --- |
| `GAC-Server-Setup.exe` | GAC Server as a Windows Service on the Admin PC, plus the **GAC 서버 상태** app | Generates the TLS certificate; shows a **감지된 교내망** page and auto-detects the LAN range (CIDR); opens the firewall for **only that range + the one port** on the Admin PC; shows the setup code at the end. |
| `GAC-Console-Setup.exe` | Console on the operator PC | Start-menu and desktop shortcuts. Records an install tombstone so this PC can never later become a client. |
| `GAC-Agent-Setup.exe` | Agent Service + Tray App on a client PC | Registers the service, starts the Tray App at every user logon, and keeps it running. **Refuses to install on a laptop** or on a PC where the Console was ever installed. Must support fully silent install for mass rollout; the server address, port, certificate thumbprint, and enrollment code come from the **rollout package** (`gac-agent.json`) the server/console exports, or on the command line. |

- Use **Inno Setup** (free, offline). Scripts live in `/installer`. One build script `build/build-installers.ps1` runs `dotnet publish` (self-contained, `win-x64`) for every component, then compiles every installer into `/dist`, and also publishes the portable `GAC-Demo.exe` (single-file, self-contained) into `/dist`.
- Installer UI is in **Korean**. Make sure the Korean translation file is vendored into `/installer/lang` so builds work offline.
- Every installer has a working uninstaller that stops and removes its services, scheduled tasks, firewall rule, and program files. Keep the server database on uninstall unless the user ticks "데이터도 삭제".
- Upgrades: running a newer installer over an older one upgrades in place and keeps settings and data.
- The binaries are not code-signed. Document in `docs/INSTALL.md` that Windows SmartScreen may warn, and how to proceed.
- Clarification to section 3 rule 1: only the **Admin PC** opens an inbound port; client PCs still open none.

## 7. Demo program (try before installing)

GAC is **free (0원)**. There is no license, no payment, no license key, no time limit, and no feature or PC-count limit anywhere in the product. Never add licensing code.

Instead, ship a separate **demo program** so a school can see what GAC does *before* installing anything.

**`GAC-Demo.exe`**

- A **single portable `.exe`** — no installer, no admin rights, no service, no network. Double-click and it runs, even from a USB stick.
- Built from the same Console/Tray UI code so the demo always matches the real product, backed by one in-memory fake server (`IGacServerClient` → `DemoServerClient`) shared across all views.
- **On start it shows a picker for all three sides of GAC**, and the user can switch between them at any time from a **화면 전환** menu. The fake data is shared (a command sent in the console view shows up in the server/client view).
  - **관리자 화면 (콘솔):** the full console, **starting from the sign-up/login screen** — sign up as the first user → become Owner → the 회원가입 button disappears. Then the account dashboard (create, change role, edit permission matrix, delete, transfer ownership), PC alias editing, the 차단된 기기 list with sample blocked laptops / console-first machines, and every module (clean, shutdown, error check, update, deploy, health, inspect, netdiag) with realistic simulated results.
  - **서버 PC 화면:** the server installer steps (fake), including the **감지된 교내망** page and the end-of-setup screen with address and setup code, plus the **GAC 서버 상태** window with live-looking fake numbers and log lines.
  - **클라이언트(교실) PC 화면:** a simulated classroom desktop with the **Tray App** (tray menu, class-mode preset editor and runner, network self-diagnosis), the auto-shutdown warning popup (**30분 연장 / 지금 종료**), the full-screen **PC 잠금** screen, and the agent installer screen with the **"노트북에는 설치할 수 없습니다"** and **"관리자 프로그램이 설치된 PC에는 클라이언트를 설치할 수 없습니다"** refusals.
- Pre-filled with a believable fake school: about 20 virtual PCs in groups like `1학년 1반`, `컴퓨터실`, `교무실`, with varied states (online/offline, low disk, pending updates, recent errors).
- A persistent banner on **every view**: **"체험 모드 · 실제 PC에는 아무 변화도 없습니다"**.
- An optional guided tour (skippable) is available from the console view's 보기 menu. The Easter egg works in the console view as before.
- **Hard safety rule:** in demo mode nothing touches the real machine — no file deletion, no shutdown, no registry, no service, no process launch, no network sockets, no firewall. A test fails if any real module implementation, the agent, the server, or the tray service is reachable from the demo build.
- Nothing is saved between runs; closing the demo discards everything.

---

## 8. Security rules (non-negotiable)

1. **Every command from the server is signed** (HMAC-SHA256 with a per-agent key issued at enrollment). Agents reject unsigned, expired (> 5 min), or replayed commands.
2. All LAN traffic uses TLS with the pinned self-signed certificate (see section 3). Never disable certificate validation in release builds.
3. The named pipe between Tray and Service has an ACL; the service accepts only the fixed action enum from it.
4. No secrets in the repository. Keys and connection settings live in config files outside source control.
5. Destructive operations (delete, uninstall, shutdown) are always logged with who triggered them and when.
6. Code that deletes files must have unit tests proving that paths outside the whitelist are never touched.
7. Assume antivirus or school security software may flag the agent. Keep behavior conservative and document what the agent does so it can be whitelisted.
8. **Desktop-only is enforced by the server** from several hardware probes the agent reports, re-checked on every heartbeat (a PC that turns into a laptop, or whose hardware fingerprint changes, is revoked). The client-side desktop checks are **distributed across several code sites** and every one **fails closed**; they are defense-in-depth, not the authority.
9. **Integrity hashes:** the agent reports hashes of its own binaries; the server records them and flags mismatches.
10. **Tamper test:** a test fails if any one of the client desktop-check sites, the server check sites, or the install-order check sites is removed — the checks cannot be quietly deleted.
11. **Enrollment is bound to hardware.** Agent enrollment proves possession of the pinned certificate (HMAC over thumbprint + nonce) and binds to the hardware fingerprint; a fingerprint first seen as a Console is refused as an agent. Agent credentials cannot call any console or account endpoint.

---

## 9. Development phases

Work through the phases in order **without stopping for approval**. When a phase meets its "Done when" criteria, write a short summary in `docs/PROGRESS.md` and continue straight into the next phase.

| Phase | Scope | Done when |
| --- | --- | --- |
| 1. Skeleton | Solution layout, Agent Service, Tray App, Server, Console, **operator login, accounts and roles (section 5)**, enrollment, signed command channel, module plugin interface. Port old GAC "error check" and "run command" as the first modules. | One test PC enrolls, appears in the console, runs a signed command, and the result shows in the console. |
| 2. Auto maintenance | `clean` (with dry-run), `shutdown` | Dry-run report matches expectations on a test PC; scheduled shutdown with extend works. |
| 3. Management | `update`, `deploy`, `health`, `inspect` | Each module produces console-visible results on at least two test PCs. |
| 4. Teacher features | `classmode` (preset editor + runner), `netdiag` | A teacher creates a preset without help, and netdiag fixes a deliberately broken DNS setting. |
| 5. Ship | Installers (section 6), `GAC-Demo.exe` (section 7), Easter egg (`EASTER_EGG.md`), `LICENSE`, `docs/INSTALL.md` | Clean Windows 10/11 VM with no internet: `GAC-Demo.exe` runs from a USB stick and every module can be tried with no system change; Server, Console, Agent install from `/dist`; uninstall leaves nothing behind. |

Suggested solution layout:

```
/src
  Gac.Shared        (contracts, command signing, module interface)
  Gac.Agent         (Windows Service)
  Gac.Tray          (WinForms tray app)
  Gac.Server        (Windows Service: ASP.NET Core API + SQLite + package store)
  Gac.Console       (WinForms desktop .exe for the operator PC)
  Gac.Modules.*     (one project per module)
/tests
  Gac.*.Tests       (xUnit)
/docs
```

---

## 10. How to work in this repo

- **Never ask the owner questions. Start immediately.** All product decisions are in this file. If something is unspecified, choose the simplest reasonable option, record it in `docs/DECISIONS.md` (one line: decision + reason), and keep going.
- **Plan, then build — no approval step.** For non-trivial work, write a short plan in `docs/plans/`, then implement it with tests right away. When using Superpowers skills, skip their question/brainstorming-with-user steps and any "wait for approval" gates; answer those questions yourself from this file.
- **Verify, don't guess.** For Windows APIs, .NET APIs, and command-line flags, check current documentation (use Context7 or Microsoft Learn) before writing code. If something can't be verified, say so in the PR/commit message.
- **Test what you claim.** Run `dotnet build` and `dotnet test` before saying a task is done. Report failures honestly.
- **Small commits** with clear English messages. One logical change per commit.
- **The only time to stop:** an action outside this repository that cannot be undone (e.g. deleting files elsewhere on the machine). Everything inside the repo — adding NuGet dependencies, creating projects, refactoring — just do it and log it in `docs/DECISIONS.md`.
- Keep modules independent: a module must not reference another module's project.
- Logs: structured, English in code, Korean only in messages shown to teachers/owner.
- **The development PC may use the internet** (Claude Code, NuGet restore, docs). **The shipped product may not.** Before finishing any task, check that no new code path makes an outbound connection outside the school LAN.

---

## 11. Claude Code plugins for this project

Run these inside Claude Code. The official marketplace (`claude-plugins-official`) is registered automatically on the first interactive session.

```text
# Core workflow
/plugin install superpowers@claude-plugins-official
/plugin install feature-dev@claude-plugins-official
/plugin install code-review@claude-plugins-official
/plugin install security-guidance@claude-plugins-official
/plugin install commit-commands@claude-plugins-official
/plugin install code-simplifier@claude-plugins-official

# C# code intelligence (requires: dotnet tool install --global csharp-ls)
/plugin install csharp-lsp@claude-plugins-official

# Up-to-date library/API docs
/plugin install context7@claude-plugins-official

# GitHub integration (if the repo is on GitHub)
/plugin install github@claude-plugins-official

# Anthropic skills (optional: xlsx/docx/pdf export for inspection reports)
/plugin marketplace add anthropics/skills
/plugin install document-skills@anthropic-agent-skills
/plugin install example-skills@anthropic-agent-skills
```

After installing, run `/reload-plugins` if prompted.

---

## 12. Decided (do not ask again)

- Stack: C# / .NET 10, self-contained `.exe` builds
- Console: desktop `.exe` on the operator PC
- Network: school LAN only, no internet at runtime
- Server: ordinary Windows 10/11 desktop PC (Admin PC), no server hardware or Windows Server features
- Modules: the 9 in section 2, all five phases ship
- Distribution: installer `.exe` files (Inno Setup) + portable `GAC-Demo.exe` for trying it before installing
- Price: free (0원). No license, no payment, no limits, no licensing code
- Accounts: Owner / Admin / Operator / Viewer roles, enforced on the server
- Accounts: first sign-up = Owner, then sign-up closes; sign-up needs the single-use setup code
- Clients: desktop PCs only, laptops blocked (server-enforced)
- Console: installable anywhere including laptops; console-first machines can never become clients
- Server setup: auto-detects the LAN range, opens only that range + port
- Client PCs: no login; presets stored per PC
- License: MIT (`LICENSE` at repo root, copyright holder "JH")
- Easter egg: see `EASTER_EGG.md` — implement exactly as written
- Name: GAC 2.0
