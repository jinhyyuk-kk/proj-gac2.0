# CHANGE-001 — Client lockdown, install order, auto LAN setup, no client login, background running, period presets & PC lock, PC aliases, accounts, full demo

> Change order from the owner. Put this file in the repository root and apply it **immediately, without asking questions**.
> These changes **override** `CLAUDE.md` wherever they conflict.
> When done: update `CLAUDE.md` so it matches this document, move this file to `docs/changes/CHANGE-001.md`, and add a summary to `docs/PROGRESS.md`.
> If some of this is already built, refactor it to match. If it is not built yet, build it this way from the start.

---

## 1. Client install: desktop PCs only — laptops blocked

The Agent (client) must only run on **desktop PCs**. Laptops, and **especially teacher laptops**, must be blocked.

**Why:** a student could install the Agent on a teacher's laptop, or use an enrolled machine as a foothold to attack the GAC Server. Assume the attacker can read GAC's source code (it is MIT-licensed), so **client-side checks alone are never enough — the server must enforce everything.**

### 1.1 Laptop detection — distributed, hard to remove

The laptop check must **not** live in one place. If it were one `IsLaptop()` function, a student could delete one line and rebuild. Spread it out so that removing it means finding and changing many unrelated places — and even then, the server still rejects the device (section 1.2).

**Independent probes (each implemented separately, in different projects, with different code):**

| Probe | Source | Laptop signal |
| --- | --- | --- |
| A | WMI `Win32_ComputerSystem.PCSystemType` | `2` (Mobile) |
| B | WMI `Win32_SystemEnclosure.ChassisTypes` | `8` Portable, `9` Laptop, `10` Notebook, `11` Hand Held, `14` Sub Notebook, `30` Tablet, `31` Convertible, `32` Detachable |
| C | Raw SMBIOS table via `GetSystemFirmwareTable` (`'RSMB'`), parse the type-3 (System Enclosure) structure yourself | same chassis values as B |
| D | `GetPwrCapabilities` (`powrprof.dll`) → `SYSTEM_POWER_CAPABILITIES` | `LidPresent` = true, or `SystemBatteriesPresent` = true |
| E | WMI `Win32_Battery` | any instance present |

Verify every API, struct field, and value against current Microsoft documentation before coding.

**Where the checks run (each site calls a *different* subset of probes through its own code, not a shared helper):**

1. `GAC-Agent-Setup.exe` before installing (Inno Setup `[Code]` section, probes A + D).
2. Agent Service startup (probes B + C).
3. Agent Service, before every heartbeat (probe D + one rotating other probe).
4. Tray App startup (probes A + E).
5. Each module's pre-run check inside its own module project (each module picks a different probe).
6. The enrollment client, which collects **all five probes' raw values** and sends them to the server.

**Rules for the distributed code:**

- No single shared `IsLaptop`, `HardwareCheck`, or similar class that everything calls. No config flag, environment variable, command-line switch, registry key, or `#if DEBUG` path that disables any check in Release builds.
- Each site fails **closed**: if a probe throws, times out, or returns nothing, treat it as a laptop.
- Each site reacts in its own way (installer refuses; service stops; tray exits; module refuses to run; heartbeat reports "blocked" and stops) — but all show the same Korean message: **"이 프로그램은 데스크톱 PC에만 설치할 수 있습니다. 노트북에는 설치할 수 없습니다."**
- Send **raw probe values** to the server, never a single "isLaptop: false" boolean. The server makes the decision.
- **Integrity report:** the agent sends the SHA-256 of its own executables and module DLLs with every heartbeat. The server knows the hashes of every official release (written into the server at build time) and **rejects any agent whose hashes don't match** — a rebuilt agent with the checks removed is refused.
- Add a **tamper test** (`tests/Gac.Tamper.Tests`) that scans the solution and fails the build if any of the six check sites above is missing or no longer calls its probes. Removing a check therefore also breaks the test suite.

### 1.2 Server-side enforcement (the real protection)

- Every enrollment request **and every heartbeat** carries the raw values of all five probes, plus manufacturer, model, wired MAC addresses, Windows computer name, and the integrity hashes.
- The server applies the laptop rules itself, in **three separate places**: enrollment, heartbeat processing, and command dispatch. A device that turns out to be a laptop at any point is revoked immediately.
- **Missing or inconsistent probe data = laptop.** If any probe value is absent, or probes disagree (e.g. B says desktop but C says laptop), reject.
- Hash mismatch with every known official release = reject and log as tampering.
- **No subnet-based device decisions.** The school network cannot reliably separate desktops, laptops, and Wi-Fi by IP range, so never use an IP range to decide whether a device is a desktop or a laptop. Device identity comes from the hardware checks, enforced on the server. (The firewall range in section 2 only controls who can reach the port.)
- The agent must enroll over a **wired Ethernet** adapter. If its only active connection is Wi-Fi, enrollment is rejected.
- Machines where only the battery signal fires (probe E, or `SystemBatteriesPresent` without `LidPresent`, with a desktop chassis everywhere else) go to a **"확인 필요"** list — some desktops report a UPS as a battery. Only the Owner can approve them. Machines rejected for mobile type or portable chassis **cannot be approved by anyone** — there is no override.
- Every rejected attempt is shown in the console under **"차단된 기기"** with time, IP, reported name, model, and reason. The Owner can permanently block a device (by hardware fingerprint).

### 1.3 Limit the damage if an agent is compromised

- Agent credentials can only call **agent endpoints**, and only for **their own PC**. They can never call console/account endpoints, read other PCs' data, or create commands.
- The server never executes, evaluates, or deserializes into types anything an agent sends. Treat all agent data as untrusted: validate size and schema, and escape it when the console displays it.
- Rate-limit enrollment and agent endpoints per agent and per source address.
- Bind each agent key to its hardware fingerprint. A key used with a different fingerprint is revoked and the event is logged.
- The Owner can **revoke** any PC from the console; its key stops working immediately.
- Console endpoints and agent endpoints are separate route groups with separate authentication schemes.

---

### 1.4 Console vs. client install order

The Console (관리자 exe) and the Agent (클라이언트) follow an **install-order rule**. Reason: the owner works *and* studies at the school and uses one school desktop for both, so Console-on-top-of-client is legitimate. But a machine that gets the Console **first** is treated as a teacher's laptop / admin device and must never become a client.

| Situation | Result |
| --- | --- |
| Client installed, then Console installed | **Allowed.** |
| Console installed, then client install attempted | **Refused.** |
| Client already installed, Console installed later, client upgraded/reinstalled over itself | **Allowed** (the client registration existed first). |
| Console installed, client was uninstalled, client install attempted again | **Refused** (the client is no longer first). |
| Console on a laptop | **Allowed.** The Console has no hardware restriction. |

**Console install markers.** `GAC-Console-Setup.exe` leaves several independent markers, and the Console re-creates any missing ones on every start:

- `HKLM\SOFTWARE\GAC\Console` with install time and machine fingerprint.
- Its uninstall entry under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`.
- Its program folder and its `ProgramData` folder.

Uninstalling the Console removes its program files but **keeps a tombstone** (`HKLM\SOFTWARE\GAC\Console\EverInstalled` with the date). A machine that ever had the Console first stays "Console-first" — the order cannot be reset by uninstalling. Only the Owner can clear this, from the console's device list, for a specific machine fingerprint.

**Agent side.**

- `GAC-Agent-Setup.exe` refuses if **any** Console marker or tombstone exists **and** no existing client registration (`HKLM\SOFTWARE\GAC\Agent` with an earlier install time) is present. Message: **"관리자 프로그램이 설치된 PC에는 클라이언트를 설치할 수 없습니다."** Silent mode exits with its own distinct exit code.
- Like the laptop check, this check is **distributed**: it runs in the installer, at Agent Service startup, and in the enrollment client, each with its own code. Missing or unreadable markers when the registry is inaccessible = fail closed.
- The enrollment client sends the raw marker data (which markers exist, their timestamps, the agent's own first-install time) to the server.

**Server side.**

- The server rejects **new enrollment** from any machine reporting a Console marker or tombstone older than its agent install time, or with no agent install time.
- The server also remembers which machine fingerprints have ever logged in **as a Console**. A fingerprint seen as a Console *before* it was ever seen as an agent can never enroll as an agent (unless the Owner clears it).
- An already-enrolled agent that later gets the Console installed keeps working normally.
- Add both checks to the tamper test (section 1.1).

## 2. Server install: detect the LAN range, open the port, auto-accept clients

The GAC Server runs on an ordinary desktop PC, so `GAC-Server-Setup.exe` must set everything up by itself:

- **Detect the LAN range automatically** during setup: find the network adapter that has the default gateway, read its IPv4 address and prefix length, and compute the network range in CIDR form (e.g. `10.20.0.0/22`). If several adapters qualify, prefer wired Ethernet, then the lowest interface metric.
- Show a setup page: **"감지된 교내망: 10.20.0.0/22 · 포트: 50443"**, with both values editable before continuing. Pick a fixed default port and fall back to the next free port if it is taken.
- Create a Windows Firewall **inbound** rule allowing **only that range** on **only that port** (TCP). Apply it to all firewall profiles, because school networks are often classified as Public. Remove the rule on uninstall.
- The server status app (section 6) shows the current range and lets a Windows administrator add another range or re-detect it if the school network changes.
- This range only controls **who can reach the port**. It is never used to decide desktop vs. laptop (section 1.2).
- Generate the TLS certificate and a random **setup code** (used once, see section 4).
- At the end of setup, show: the server address and port, and the setup code, with a note to write it down.
- Clients that pass every check in section 1 are **accepted automatically** — no manual approval step.

**Agent rollout package:** the console has a button **"클라이언트 설치 파일 만들기"** that exports a folder containing `GAC-Agent-Setup.exe` plus `gac-agent.json` (server address, port, certificate thumbprint, enrollment secret). The agent installer reads that JSON from its own folder, so installing on a classroom PC is just double-click (or silent install) and the PC appears in the console by itself. The Owner can **regenerate the enrollment secret** at any time, which invalidates old rollout packages.

### 2.1 Client PCs: no login, ever

School client PCs already sign in to Windows **automatically** with a shared account. GAC must add **no login of any kind** on client PCs.

- The Agent and Tray App start and work with **no GAC username, password, PIN, or sign-in screen**. Teachers just use the tray icon.
- GAC accounts (section 4–5) exist **only for the Console**. Nothing on a client PC ever asks for them.
- Because everyone on a client PC shares one Windows account, **class-mode presets are stored per PC** (in `ProgramData`), not per Windows user, and are visible to whoever uses that PC. Update `CLAUDE.md` section 4.3 accordingly ("stored as JSON per user" → "per PC").
- Per-user paths in `clean` still work as written; on these PCs that is simply the one shared profile.
- `shutdown`: a logged-in session is the normal case, so the warning popup always shows. Keep the "nobody logged in → shut down without popup" path for PCs where auto-login is off.
- GAC must **not** change the PCs' Windows auto-login settings — not enable, disable, or read the stored password.
- The demo's client view starts directly on the desktop with the tray icon, with no sign-in step.

### 2.2 Always running in the background

- The Agent Service starts with Windows and runs all the time.
- The Tray App starts at every logon and stays running. If it is closed or killed, the Agent Service **restarts it within a few seconds** (watchdog). The tray menu has no "exit" item.
- Any active lock or scheduled preset state is held by the **service**, not the tray, so killing the tray or rebooting does not clear it.

### 2.3 Console-run presets with a period (N days)

Presets can now also be created and run **from the Console** (운영 서비스), not only from the Tray App.

- In the Console the Owner/Admin creates a preset, picks target PCs or groups, sets a **period name** (e.g. `기말고사 기간`), a **start date/time**, and a **number of days**, then presses **시작하기**.
- The server sends the preset to the target agents. During the period, the agent runs it automatically at every logon and keeps enforcing it. When the period ends it stops by itself. The Console can also **stop** a running period early.
- Running periods are listed in the Console with targets, start, end, and who started them. All of this goes in the audit log.

### 2.4 New preset action: `lock_pc` (lock the PC)

Add `lock_pc` to the class-mode action list. **Only Console presets can contain it** (teachers' own Tray presets cannot), so a student cannot lock PCs for fun.

When `lock_pc` runs:

1. End `explorer.exe` (this removes the desktop, taskbar, and Start menu). While locked, if `explorer.exe` comes back, end it again.
2. Show a **plain rectangular window** (no fancy design), centered, always on top, no close/minimize buttons, `Alt+F4` ignored. It contains exactly:

```
지금은 "{기간 이름}" 이기에 사용이 불가합니다.
3분 후 자동 종료됩니다.

[관리자입니다.]   [종료하기]
```

- `{기간 이름}` is the period name from the preset (e.g. `지금은 "기말고사 기간" 이기에 사용이 불가합니다.`). Keep the text exactly as written, including the quotation marks and spacing.
- The second line counts down live (3분 → 2분 59초 …). At 0 the PC **shuts down** (normal shutdown via the service, forced after a short grace period).
- **[종료하기]** shuts the PC down immediately.
- **[관리자입니다.]** opens a password box (GAC Console account name + password):
    - The credentials are sent over TLS to the GAC Server and checked there. They are never stored or logged on the client.
    - Correct → the lock window closes, `explorer.exe` is restarted, the countdown stops, and the **audit log records the account name**, PC, period, and time. The PC stays unlocked **until it restarts**; at the next logon it locks again if the period is still active.
    - Wrong → error message; 5 wrong attempts lock that account for 5 minutes (same rule as Console login). The countdown keeps running.
    - Only accounts whose role has the **"PC 잠금 해제"** permission (permission matrix, section 5) can unlock. Default: **Owner, Admin, and Operator**.
    - If the server cannot be reached, show **"서버에 연결할 수 없습니다"** and keep the countdown; the event is queued and uploaded later.
- The lock runs in the Tray App (user context: ending the user's own `explorer.exe` needs no admin rights); the shutdown is requested from the service through the fixed action enum.
- **Work PCs are exempt.** If a client PC also has the Console installed (client first, then Console — allowed by section 1.4), that PC is a staff work PC: `lock_pc` is **skipped** there entirely — no explorer shutdown, no lock window, no countdown. The agent logs "skipped: work PC" for the period.
    - The exemption is decided by the **server**, not by the client alone: it applies only if the server has seen a **successful Console login from that PC's fingerprint**. Merely installing the Console (without a valid account) does not exempt a PC, so a student cannot dodge a lock by installing the Console.
    - The Console's PC list marks such PCs as **업무용 PC**. The Owner can remove the exemption for a specific PC.
- Demo client view: show the lock window with a fake period name and a fast countdown; unlocking with a demo account adds a demo audit entry.

### 2.5 Auto shutdown: missed runs are skipped, never caught up

- If a PC was **off** (or the agent was not running) at the scheduled shutdown time, the run is **skipped** — just log "skipped: PC was off at scheduled time" and wait for the next scheduled time.
- **Never** shut a PC down right after it boots because a schedule was missed. Do not queue, persist, or replay missed shutdown runs.
- A scheduled run fires only if the agent is running within a short tolerance window of the scheduled time (default 1 minute).
- This applies to the `shutdown` module's schedule. (The `lock_pc` countdown in 2.4 is different: it is meant to run at logon during an active period.)
- Update `CLAUDE.md` section 4.2 with this rule.

---

## 3. PC names → editable aliases

- In the console, every PC's name can be edited.
- Editing creates or changes an **alias (별명)** stored only on the GAC Server. **Never** rename the actual Windows computer name.
- The console shows the alias in large text and the real Windows name below it in small text. Search matches both.
- Clearing the alias falls back to the Windows name.
- Alias changes are recorded in the audit log.

---

## 4. Admin accounts: sign-up / login

Replace the old "create Owner account on first run" screen with this flow.

**Login screen** has two buttons: **회원가입** and **로그인**.

- **The first account ever created becomes the Owner.**
- Once any account exists, the **회원가입 button disappears** and only **로그인** remains. The console asks the server whether sign-up is open; the server decides.
- After that, new accounts are created **only by the Owner** from the account dashboard.

**Server rules (enforced on the server, not just hidden in the UI):**

- The sign-up endpoint works **only while the account table is empty**, inside a single database transaction, so two people signing up at the same moment cannot both become Owner. Once one account exists, the endpoint returns 403 forever.
- Sign-up requires the **setup code** shown at the end of server installation. This stops a student who reaches the console first from taking the Owner account. The code is single-use and is destroyed after the first sign-up.
- If the Owner forgets the password, recovery is done **only on the Admin PC itself** (a local recovery tool that requires Windows administrator rights). Document this in `docs/INSTALL.md`.

---

## 5. Permissions and the account dashboard

The Owner gets an **계정 대시보드** (account dashboard) listing every account: name, role, assigned PC groups, last login, status.

From it the Owner can:

- **Create** accounts (only way to add accounts after the first sign-up).
- **Change an account's role** (Admin / Operator / Viewer) at any time.
- **Edit what each role is allowed to do**: a permission matrix of modules × actions (view, run, configure) with checkboxes. The Owner role itself is fixed and always has everything.
- Assign PC groups to Operator accounts.
- Reset a password, disable, or **delete** an account.
- **Transfer ownership** to another account; the old Owner becomes Admin. There is always exactly one Owner.

Rules:

- The Owner cannot delete or demote themselves (transfer ownership first).
- Deleting an account keeps its audit-log history (store the account name with each log entry).
- Role and permission changes take effect on the account's **next request** — the server checks permissions on every call.
- All of these actions are recorded in the audit log.

---

## 6. Server status window (new, small)

The Admin PC currently has no screen. Add a small **GAC 서버 상태** app (tray icon + window, installed by `GAC-Server-Setup.exe`) showing:

- Service running / stopped, with start / stop / restart buttons (requires Windows admin).
- Address, port, detected LAN range, firewall rule status, with **"대역 다시 감지"** and **"대역 추가"** buttons.
- Connected PCs count, last enrollment, blocked attempts count.
- The setup code (until the first sign-up), and the "클라이언트 설치 파일 만들기" button.
- Recent server log lines.

It only talks to the local service; it is not a second console.

---

## 7. Demo: try every screen

`GAC-Demo.exe` must now let people try **all three sides**, not only the console. On start it shows a picker:

| Choice | What it simulates |
| --- | --- |
| **관리자 화면 (콘솔)** | Full console as before, **starting from the sign-up/login screen**: sign up as the first user → become Owner → the 회원가입 button disappears. Then the account dashboard (create, change role, edit permission matrix, delete, transfer ownership), PC alias editing, the 차단된 기기 list with sample blocked laptops, and every module. |
| **서버 PC 화면** | The server installer steps (fake), including the "감지된 교내망" page, the end-of-setup screen with address and setup code, and the **GAC 서버 상태** window with live-looking fake numbers and log lines. |
| **클라이언트 PC 화면** | A simulated classroom desktop window with the **Tray App**: tray menu, class-mode preset editor and runner, network self-diagnosis, the auto-shutdown warning popup (**30분 연장 / 지금 종료**), and the agent installer screen, including the **"노트북에는 설치할 수 없습니다"** and **"관리자 프로그램이 설치된 PC에는 클라이언트를 설치할 수 없습니다"** refusals. |

- The user can switch between the three views at any time from a menu, and the fake data is shared (e.g. a command sent in the console shows up in the client view).
- Keep the banner **"체험 모드 · 실제 PC에는 아무 변화도 없습니다"** on every view.
- The hard safety rule from `CLAUDE.md` section 7 still applies to all three views: nothing touches the real machine, no network, no firewall, no services.
- The Easter egg works in the demo's console view as before.

---

## 8. Tests required

- Enrollment and heartbeat rejected for: mobile system type, each portable chassis type, `LidPresent`, each probe missing, probes disagreeing, Wi-Fi-only connection, revoked fingerprint, integrity hash mismatch.
- Tamper test fails if any of the six client check sites or three server check sites is removed, or any install-order check site is removed.
- Install order: client → console = allowed; console → client = refused; console uninstalled then client = still refused (tombstone); existing client reinstalled after console = allowed; console installs on a laptop.
- Server rejects new agent enrollment from a fingerprint first seen as a Console.
- LAN range detection: correct CIDR for sample adapter configurations (/24, /22, multiple adapters, Wi-Fi + Ethernet); firewall rule created with exactly that range and port.
- Battery-flagged desktop goes to "확인 필요", not auto-accepted.
- Agent credentials cannot call any console or account endpoint (expect 401/403).
- Two concurrent sign-ups on an empty database → exactly one Owner.
- Sign-up after the first account → 403, even with a valid-looking request.
- Wrong or reused setup code → rejected.
- Owner cannot delete or demote self; ownership transfer leaves exactly one Owner.
- Permission matrix change takes effect on the next request.
- Alias edit never changes the Windows computer name.
- Demo build: none of the new code paths touch the firewall, services, or network.
- Tray App killed → service restarts it; lock state survives tray kill and reboot.
- Period preset: runs only between start and start + N days; stops early when stopped from the Console.
- `lock_pc`: rejected in a teacher (Tray) preset; lock text renders exactly `지금은 "{기간 이름}" 이기에 사용이 불가합니다.`; countdown reaches 0 → shutdown requested; correct admin password → unlocked, audit entry has the account name; wrong password ×5 → account locked; account without "PC 잠금 해제" permission cannot unlock; Operator can unlock by default.
- Work PC exemption: client + Console with a successful Console login from that fingerprint → `lock_pc` skipped; Console installed but never logged in → still locked; Owner can remove the exemption.
- Missed shutdown: PC off at scheduled time → no shutdown at next boot, "skipped" logged; agent running at scheduled time → shutdown flow runs.

---

## 9. Updates to `CLAUDE.md`

After applying this change, edit `CLAUDE.md` so it matches:

- Section 3: add the server status app, auto-detected LAN range + port firewall rule, desktop-only clients, the console/client install-order rule. No subnet-based device decisions.
- Section 5: replace the first-run flow with sign-up/login + setup code; add the account dashboard, role changes, permission matrix, deletion, ownership transfer.
- Section 6: add the server status app to `GAC-Server-Setup.exe`, the desktop check to `GAC-Agent-Setup.exe`, and the rollout package export.
- Section 7: the three-view demo.
- Section 8: add the rules from sections 1.1–1.3 of this file (distributed checks, integrity hashes, tamper test).
- Section 12: add "Clients: desktop PCs only, laptops blocked (server-enforced)", "Console: installable anywhere including laptops; console-first machines can never become clients", "Server setup: auto-detects LAN range, opens only that range + port", "Accounts: first sign-up = Owner, then sign-up closes", and "Client PCs: no login, presets stored per PC".
- Section 4.3: presets stored per PC, not per user (section 2.1); add Console-run period presets (2.3) and the `lock_pc` action (2.4).
- Section 3: Tray App watchdog and always-on background running (2.2).
- Section 4.2: missed scheduled shutdowns are skipped, never caught up (2.5).
