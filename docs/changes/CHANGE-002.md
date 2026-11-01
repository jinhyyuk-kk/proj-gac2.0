# CHANGE-002 — Icons, lock exemption calendar, server install firewall step, new server UI

> Change order from the owner. Put this file in the repository root and apply it **immediately, without asking questions**.
> Apply it on top of `CLAUDE.md` + `CHANGE-001`. Where this file conflicts with them, **this file wins**.
> When done: update `CLAUDE.md` to match (section 8 of this file), move this file to `docs/changes/CHANGE-002.md`, and add a summary to `docs/PROGRESS.md`.
> If something is unclear, choose the simplest reasonable option, log it in `docs/DECISIONS.md`, and keep going.

---

## 1. Icons

There are **two** icons:

| Icon | Source | Used by |
| --- | --- | --- |
| **Admin icon** → `assets/icons/gac-admin.ico` | Extracted from the old admin setup file **`GAC_ADMIN_SETUP.exe`** | **Console (관리자 exe) only** |
| **GAC icon** → `assets/icons/gac.ico` | Made from the owner's **background-removed image** (transparent PNG) | **Everything else** |

Both source files are in a folder named **`icon`**. Look in `../icon/` (parent of the repository) first, then `./icon/` (repository root).

### 1.1 Admin icon — extract from `GAC_ADMIN_SETUP.exe`

- Extract the **full icon group** (every size and colour depth) from the resources of `GAC_ADMIN_SETUP.exe` into `assets/icons/gac-admin.ico`, and the largest frame as `assets/icons/gac-admin-256.png`.
- Do **not** use `System.Drawing.Icon.ExtractAssociatedIcon` — it returns only one small size. Read the resources directly: load the file as a data file (`LoadLibraryEx` with `LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE`), find the `RT_GROUP_ICON` resource, read each `RT_ICON` it lists, and rebuild a valid `.ico`. Verify the APIs and structures against Microsoft documentation before coding.
- If the file name differs only in case, accept it. If it is missing, stop this part, log it in `docs/DECISIONS.md`, use `gac.ico` for the Console temporarily, and continue with the rest.

### 1.2 GAC icon — build from the background-removed image

- The image is the image file in the `icon` folder (`.png`, or `.webp`/`.jpg` if no PNG). If there are several images, use the PNG with transparency; otherwise the newest. Log the choice.
- Keep the transparency. If the image is not square, centre it on a transparent square canvas (do not stretch). Trim fully transparent borders first so the shape fills the icon.
- Generate a multi-size `assets/icons/gac.ico` with **16, 24, 32, 48, 64, 128, 256** px frames using high-quality downscaling (bicubic/high-quality, with premultiplied alpha so edges don't get a dark fringe). Store the 256 px frame PNG-compressed and the smaller frames as 32-bit BGRA bitmaps, so Windows, Explorer, the taskbar, and Inno Setup all accept it.
- Also save `assets/icons/gac-256.png`.
- Check: the `.ico` parses, every frame size is present, and transparency is kept (corner pixels of each frame are fully transparent if the source corners are).

### 1.3 Tool

- Put both jobs in one small console tool `tools/Gac.IconTool` with two commands: `extract <setup.exe> <out.ico>` and `build <image> <out.ico>`. It can be re-run later when the owner swaps the files.
- **Commit only** `gac-admin.ico`, `gac-admin-256.png`, `gac.ico`, `gac-256.png`. Do **not** commit `GAC_ADMIN_SETUP.exe` or the source image.

### 1.4 Where each icon goes

| Program | Icon |
| --- | --- |
| **Console** (관리자 exe): `<ApplicationIcon>`, every Console window, and the **top-left logo** (`gac-admin-256.png` — the logo the Easter egg counts clicks on) | **Admin icon** |
| **`GAC-Console-Setup.exe`**: `SetupIconFile`, `UninstallDisplayIcon` | **Admin icon** |
| **Client** — Tray App (`<ApplicationIcon>`, tray `NotifyIcon` using the 16/32 frames, every Tray window including the lock window and shutdown warning) | GAC icon |
| **Client** — Agent Service | GAC icon |
| **Server** — GAC Server service, GAC 서버 관리 app (window + tray) | GAC icon |
| **`GAC-Agent-Setup.exe`**, **`GAC-Server-Setup.exe`** | GAC icon |
| **Demo** — `GAC-Demo.exe` file icon, the view picker, server PC view, client PC view, fake tray icon | GAC icon |
| **Demo** — inside the 관리자 화면 view | Admin icon (it is the real Console UI, so it looks exactly like the real Console) |
| Developer-only tools (`tools/*`) | Default icon is fine |

---

## 2. Calendar and lock exemption days

Add a **캘린더** screen to the Console.

### 2.1 What it shows

- A month view showing:
    - **Active and scheduled period presets** from `CHANGE-001` §2.3 (e.g. `기말고사 기간`), as bars across their days.
    - **Lock exemption days (잠금 해제일)**, as highlighted days.
- Clicking a period opens its details (targets, who started it, stop button).

### 2.2 Lock exemption days

- An exemption day has a name, a date, and an optional recurrence. Provide the preset name **`내PC지키미 점검일`** in a dropdown, plus free text.
- Recurrence options: one-off date, every month on a given day, or every month on the Nth weekday (e.g. 3rd Wednesday). Plus "skip this occurrence".
- On an exemption day, **`lock_pc` does not run at all, no matter what** — across every PC and every active period. No explorer shutdown, no lock window, no countdown. This overrides every period preset, including one started on the same day.
- The day runs from **00:00 to 23:59:59 local time (Asia/Seoul)**.
- If a PC is **already showing the lock window** when an exemption day starts (e.g. it was left on overnight), close the lock window and restart `explorer.exe` immediately.
- Other modules keep working as normal on exemption days (auto shutdown, clean, etc.). Only `lock_pc` is suspended.
- Permission: new matrix entry **"캘린더 관리"**. Default: Owner and Admin. Everyone with Console access can view the calendar.
- Adding, editing, or deleting an exemption day is recorded in the audit log.

### 2.3 Agents must know even offline

- The server sends agents the exemption days for the **next 60 days** (expanded from recurrences), and every change immediately on the next poll. Agents keep this list locally (in `ProgramData`, protected from non-admin users).
- **Clock tampering:** a student might change the PC clock to an exemption day to dodge a lock. The agent decides "today" from **server time** when it is online. When offline, it uses the last server time it received plus elapsed monotonic time since then (`Environment.TickCount64` or `Stopwatch`), not the local wall clock. If the local clock differs from that estimate by more than 10 minutes, ignore the local clock and log it.

---

## 3. Server installer: firewall step at the end

This **replaces** the pre-install LAN detection page from `CHANGE-001` §2. The server install is **not finished** until the firewall step is done.

### 3.1 Wizard flow

1. Normal pages → files are installed → service registered.
2. The page after installation shows **[다음]** — **not** [마침] / [종료].
3. **방화벽 열기** page:
    - Shows the detected school LAN range (CIDR, from the adapter with the default gateway, wired first — rules from `CHANGE-001` §2) and the port. Both are editable.
    - Text: **"교내망에서만 서버에 접속할 수 있도록 방화벽을 엽니다."**
    - Button **[방화벽 열기]** creates the inbound rule: TCP, that port only, remote address = that LAN range only, all profiles.
    - Show success ✓ or the error. **[다음]** stays disabled until the rule is created and verified (read it back after creating it).
4. **설치 완료** page: server address, port, LAN range, and the **setup code** (`CHANGE-001` §4), with a note to write the code down. Only now is there a **[마침]** button.

Use Inno Setup custom pages placed **after** installation (`CreateCustomPage` after `wpInfoAfter`). Verify the API in the Inno Setup documentation. Hide or disable Cancel on these pages so the user cannot skip them.

### 3.2 If the firewall step was not finished

If the wizard is closed or killed before the firewall step completes:

- The service starts in a **"설치 미완료"** state: it accepts no enrollment, no agent traffic, and no Console sign-up.
- The server status app (section 4) opens with a big **"설치를 완료하려면 방화벽을 여세요"** panel and the same **[방화벽 열기]** button. Once done, it shows the setup code and the server becomes active.
- The setup code is shown only after the firewall step is done.

---

## 4. New server UI (GAC 서버 관리)

Rebuild the server status app from `CHANGE-001` §6 as **GAC 서버 관리**, a WinForms window + tray icon on the Admin PC. Korean UI, plain layout, left-side menu with these sections. Changing settings requires Windows administrator rights (the app asks for elevation when needed).

### 4.1 상태 (Status)

- Service running/stopped with start / stop / restart.
- Address, port, LAN range(s), firewall rule status, **[대역 다시 감지]**, **[대역 추가]**.
- Connected PCs, last enrollment, currently blocked count.
- Setup code (until the first sign-up) and **[클라이언트 설치 파일 만들기]**.

### 4.2 자동 종료 (Server PC auto shutdown)

When the Admin PC itself turns off automatically.

- **Who decides:** the server PC shutdown schedule can be set by **Admin (관리자) and above — Admin and Owner** — from the Console, as well as on the Admin PC itself (Windows administrator). Permission matrix entry **"서버 PC 종료 설정"**, default **Owner + Admin**; the Owner can change who has it.
- Per weekday: on/off and a time (e.g. Mon–Fri 18:00, weekends off).
- Exception dates (e.g. a day the server must stay on).
- 10 minutes before: a warning popup on the Admin PC with **[오늘은 끄지 않기]** and **[30분 연장]**, and a notice to logged-in Consoles: **"서버 PC가 10분 후 종료됩니다."** Accounts with "서버 PC 종료 설정" can also press **[오늘은 끄지 않기]** / **[30분 연장]** from that Console notice.
- Every schedule change, skip, and extension is recorded in the audit log with the account name.
- If a deploy or update job is still running, wait for it to finish (max 30 minutes), then shut down. Log the delay.
- **Missed runs are skipped, never caught up** (same rule as `CHANGE-001` §2.5): if the Admin PC was off at the scheduled time, do nothing at next boot.
- Agents already handle the server being off (queue and retry, `CLAUDE.md` §3).

### 4.3 과부하 차단 (Overload blocking)

If a client sends too many requests, block it for a while and alert the operator.

- Settings (defaults in brackets):
    - Max requests per agent per minute [60]
    - Max failed authentications per source per 10 minutes [10]
    - Max enrollment attempts per source per hour [5]
    - Block duration [10 minutes], doubling for repeat offenders within 24 hours, up to [24 hours]
- When a limit is exceeded:
    - Block that agent (by fingerprint) **and** source address for the block duration. Blocked requests get HTTP 429 with no detail.
    - Rate-limit by connection before doing any expensive work (no database writes or TLS-heavy work per blocked request beyond the minimum).
    - **Send an alert to the operator PC:** the Console shows a Windows notification and an entry in a new **알림** list — PC alias, Windows name, IP, which limit, request count, start time, block end time. If no Console is open, alerts are queued and shown at the next Console login.
- The 과부하 차단 section lists currently blocked clients with **[차단 해제]** and **[영구 차단]** (영구 차단 = the 차단된 기기 list from `CHANGE-001` §1.2).
- Console logins are **not** affected by these limits except the failed-authentication limit (Console login lockout from `CLAUDE.md` §5 still applies).
- Every block, unblock, and alert is recorded in the audit log.

### 4.4 로그 (Logs)

- Recent server log lines with a level filter and **[로그 폴더 열기]**.

### 4.5 Console side

- New Console screen **알림** with the overload alerts, newest first, and a "read" state.
- New Console screen **서버 관리** mirrors the server settings. Changes from the Console and from the Admin PC are the same setting.
    - **Server PC auto shutdown** (4.2): view and change with **"서버 PC 종료 설정"** — default **Owner + Admin**.
    - **Overload limits** (4.3): view with Owner/Admin, change with **"서버 설정"** — default Owner only.

---

## 5. Demo updates

- Icons applied as in section 1.4 (demo file and picker = GAC icon; the 관리자 화면 view = admin icon).
- **관리자 화면:** the 캘린더 screen with a sample `기말고사 기간` and a sample `내PC지키미 점검일`; the 알림 screen with a sample overload alert.
- **서버 PC 화면:** the installer flow including the **[다음] → 방화벽 열기 → 설치 완료** pages (fake), and the new **GAC 서버 관리** window with all four sections and fake data.
- **클라이언트 PC 화면:** a toggle to simulate "today is an exemption day" showing that the lock window does not appear (and closes if already open).
- The demo safety rule still applies: nothing touches the real firewall, clock, services, or network.

---

## 6. Tests required

- Icons: `gac-admin.ico` and `gac.ico` exist and parse. The Console exe and `GAC-Console-Setup` embed the admin icon; Tray, Agent, Server, server-management app, Demo exe, `GAC-Agent-Setup`, and `GAC-Server-Setup` embed the GAC icon.
- `Gac.IconTool extract`: rebuilds a valid `.ico` from a sample `.exe` with multiple icon sizes.
- `Gac.IconTool build`: from a non-square transparent PNG, produces all 7 sizes, square, with transparent corners and no dark edge fringe.
- Exemption day: `lock_pc` does not run on that day even with an active period; an open lock window closes when the day starts; `lock_pc` runs again the next day.
- Recurrence: "3rd Wednesday every month" and "skip this occurrence" expand to the right dates.
- Clock tampering: local clock set to an exemption day while server time says otherwise → lock still runs; offline estimate uses monotonic time.
- Server installer: service stays "설치 미완료" (no enrollment, no sign-up) until the firewall rule exists; setup code hidden until then.
- Server auto shutdown: fires at the scheduled time; waits for a running job (max 30 min); missed run skipped at next boot; [오늘은 끄지 않기] cancels only today; Admin can change the schedule from the Console, Operator and Viewer cannot.
- Overload: agent over the limit → 429 and blocked for the duration; doubling on repeat; alert queued for the Console; unblock works; Console login not affected except by failed-auth limit.

---

## 7. Updates to `CLAUDE.md`

After applying this change, edit `CLAUDE.md` so it matches:

- Section 3: the GAC 서버 관리 app on the Admin PC; server auto shutdown; overload blocking with alerts to the operator PC.
- Section 4.3: `lock_pc` never runs on lock exemption days (section 2 of this file).
- Section 5: new permissions **"캘린더 관리"** (Owner + Admin), **"서버 PC 종료 설정"** (Owner + Admin), and **"서버 설정"** (Owner).
- Section 6: two icons (admin icon for the Console and its installer; GAC icon for everything else); server installer ends with the firewall step.
- Section 7: demo additions from section 5 of this file.
- Section 12: add "Icons: Console = icon extracted from `GAC_ADMIN_SETUP.exe`; everything else = icon built from the owner's transparent image", "Lock exemption days override every lock period", "Server install finishes only after the firewall step".

---

## 8. Summary for the owner (Korean)

- 아이콘: `GAC_ADMIN_SETUP.exe`에서 추출한 아이콘은 관리자(콘솔)에만, 배경 지운 사진으로 만든 아이콘은 클라이언트·서버·체험판·나머지 설치 파일 전부에
- 캘린더: 기간 프리셋 표시 + `내PC지키미 점검일` 같은 잠금 해제일 등록 → 그날은 어떤 경우에도 잠금 안 함
- 서버 설치: 설치 후 [다음] → [방화벽 열기](교내망만) → 설치 완료 + 설치 코드
- 서버 관리 화면: 서버 PC 자동 종료 시간, 과도한 요청 일시 차단 + 운영자 PC로 알림
