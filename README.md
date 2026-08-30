# ZoomCheck

Windows web dashboard that compares an Excel roster with a live Zoom meeting through either the Business Dashboard API or an in-meeting Zoom App bridge suitable for Pro accounts.

[한국어](README.ko.md) · [Operator guide](docs/windows-operator-guide.md) · [MIT License](LICENSE)

[![CI](https://github.com/ianlyoo/zoomcheck/actions/workflows/ci.yml/badge.svg)](https://github.com/ianlyoo/zoomcheck/actions/workflows/ci.yml)
[![Windows build](https://github.com/ianlyoo/zoomcheck/actions/workflows/build-windows-installer.yml/badge.svg)](https://github.com/ianlyoo/zoomcheck/actions/workflows/build-windows-installer.yml)
[![Latest release](https://img.shields.io/github/v/release/ianlyoo/zoomcheck)](https://github.com/ianlyoo/zoomcheck/releases/latest)

ZoomCheck does not scrape the participant panel or automate Zoom UI. Business accounts can use the Dashboard API; Pro deployments can use the official Zoom Apps SDK from a host/co-host's in-meeting app. Both feed the same local matching and attendance engine.

## Features

- Excel `.xlsx`/`.xls` upload from the browser
- Live Zoom participant sync, manual refresh, and 5–600 second auto-sync
- Email-first matching plus name, alias, and review confidence levels
- Persistent join/leave activity log and current attendance state
- Expandable participant rows with raw/canonical Zoom names and connection history
- Safe Korean name canonicalization, rename activity, and duplicate connection groups
- CSV export, manual full-list fallback, responsive local dashboard
- Self-contained Windows installer and portable ZIP; no separate .NET install
- Local-only listener at `http://127.0.0.1:5078`; data under `%LOCALAPPDATA%\ZoomCheck`
- Auto, Business API, Pro Zoom App, and manual connection modes

## Business Dashboard API

A co-host role by itself does **not** grant API access. A Zoom account owner or admin must create and activate a Server-to-Server OAuth app for the account that owns the meeting. Add the granular scope `dashboard:read:list_meeting_participants:admin`; classic apps use `dashboard_meetings:read:admin`. The account must also have access to the Zoom Dashboard API.

Set these as Windows user environment variables, then restart ZoomCheck:

```powershell
[Environment]::SetEnvironmentVariable("ZOOMCHECK_Zoom__AccountId", "YOUR_ACCOUNT_ID", "User")
[Environment]::SetEnvironmentVariable("ZOOMCHECK_Zoom__ClientId", "YOUR_CLIENT_ID", "User")
[Environment]::SetEnvironmentVariable("ZOOMCHECK_Zoom__ClientSecret", "YOUR_CLIENT_SECRET", "User")
```

Credentials are never bundled in source, the installer, or the portable archive.

## Pro Zoom App bridge

Expose the Windows ZoomCheck listener through a stable HTTPS origin. Create a user-managed Zoom General App, select Meetings, enable Zoom App SDK, and add only `getSupportedJsApis`, `getMeetingContext`, `getMeetingUUID`, `getUserContext`, `getMeetingParticipants`, and `onParticipantChange`. Keep Guest Mode disabled. Set Home URL, OAuth Redirect URL, and OAuth Allow List to `https://YOUR-HTTPS-HOST/zoom-app/`; set Domain Allow List to `YOUR-HTTPS-HOST`. Configure the same URL in ZoomCheck:

```powershell
[Environment]::SetEnvironmentVariable("ZOOMCHECK_ZoomApp__HomeUrl", "https://YOUR-HTTPS-HOST/zoom-app/", "User")
```

ZoomCheck checks the runtime role and available APIs instead of assuming the co-host's subscription. The in-meeting user must actually be host/co-host before opening the app and the client must expose `getMeetingParticipants`. The account running the companion must install/approve the app; cross-account or cross-organization testing may require internal distribution or Marketplace test access. Create a new six-digit code for every meeting and after an app reload. The code is single-use and expires after ten minutes.

The Pro path sends participant UUID, display name, and meeting role only; it does not request email, so matching is name-based. A first empty snapshot is ignored and a second empty snapshot within 20 seconds confirms everyone left. On the public HTTPS host, ZoomCheck serves only the companion and token-protected bridge; dashboard and roster APIs remain local-only.

## Use during a meeting

1. Install and launch ZoomCheck; the local dashboard opens in the default browser.
2. Enter the live meeting ID and upload an Excel roster containing number/name columns.
3. Select **Sync Zoom participants now** and verify the current count.
4. Enable automatic sync; 10 seconds is the recommended default.
5. Select a participant row to inspect raw Zoom names, safe canonicalization, connections, and attendance history.
6. Review ambiguous, duplicate, or unmatched names, then export CSV at the end.
7. In Business mode, confirm a true zero-participant response from the warning bar. The Pro Zoom App bridge confirms two consecutive empty snapshots automatically.

Times in the activity log are polling observation times and may lag Zoom by one polling interval. API errors, incomplete pagination, and unconfirmed empty responses leave the previous attendance snapshot unchanged.

## Architecture

```mermaid
flowchart LR
  A[Business Dashboard API] --> B[ASP.NET Core local server]
  H[Pro Zoom App SDK] --> B
  C[Excel roster] --> B
  B --> D[(SQLite)]
  B --> E[Matching and confidence]
  E --> F[Browser dashboard]
  F --> G[CSV export]
```

| Project | Responsibility |
|---|---|
| `src/ZoomCheck.Core` | Domain models and participant matching |
| `src/ZoomCheck.Infrastructure` | Excel parsing, SQLite persistence, attendance projection |
| `src/ZoomCheck.Backend` | Zoom OAuth/API client, controllers, and local web dashboard |

The live endpoint is `GET /v2/metrics/meetings/{meetingId}/participants?type=live`. ZoomCheck follows pagination, retries one short `429 Retry-After`, and accepts only participants whose status is `in_meeting`.

## Development

```bash
dotnet restore ZoomCheck.sln
dotnet test ZoomCheck.sln -c Release
dotnet run --project src/ZoomCheck.Backend
```

Build Windows artifacts from PowerShell with .NET 8 SDK and Inno Setup 6:

```powershell
.\deploy\windows\build-installer.ps1 -Configuration Release -Runtime win-x64
```

Outputs:

- `dist\installer\ZoomCheck-Setup-x64.exe`
- `dist\portable\ZoomCheck-portable-win-x64.zip`

## Responsible use

Attendance data is sensitive. Verify ambiguous matches and the exported CSV before using it as an official record. ZoomCheck stores its database locally and does not guarantee attendance accuracy.

## License

MIT — see [LICENSE](LICENSE).
