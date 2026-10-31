# GAC 2.0

학교 PC 관리 플랫폼. 모든 학교 PC에 에이전트 하나, 관리 PC에 서버 하나, 관리자 PC에 콘솔 하나.
무료(0원), MIT 라이선스, 학교 LAN 전용(인터넷 불필요).

- 설치 방법: [docs/INSTALL.md](docs/INSTALL.md)
- 진행 상황: [docs/PROGRESS.md](docs/PROGRESS.md) · 결정 사항: [docs/DECISIONS.md](docs/DECISIONS.md)

## Modules

`clean` 자동 정리 · `shutdown` 자동 종료 · `classmode` 원클릭 수업 모드 · `netdiag` 네트워크 자가 진단 ·
`errors` 오류 점검 · `update` 일괄 업데이트 · `deploy` 일괄 배포·실행 · `health` PC 상태 보고 · `inspect` 학기말 점검

## Build (development PC)

```powershell
dotnet build Gac.slnx
dotnet test Gac.slnx
powershell -ExecutionPolicy Bypass -File build\build-installers.ps1   # → dist\
```

Requires the .NET 10 SDK and Inno Setup 6.

## Layout

```
src/Gac.Shared        contracts, signing, roles, pipe protocol, preset schema
src/Gac.Server        Windows service: ASP.NET Core API + SQLite + package store
src/Gac.Agent         Windows service (LocalSystem): poll, signed commands, schedules
src/Gac.Tray          teacher tray app (user session)
src/Gac.Tray.UI       shared WinForms windows (preset editor, net-diag, popups)
src/Gac.Console       operator console
src/Gac.Demo          GAC-Demo.exe (console UI + in-memory fake server)
src/Gac.Modules.*     one project per module
tests/                xUnit
installer/            Inno Setup scripts
```
