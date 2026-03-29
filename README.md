# ZoomCheck

ZoomCheck is a Windows-oriented Zoom attendance tool for classes and training sessions.

It loads a roster from Excel, accepts Zoom join/leave events, classifies attendance match confidence, and gives the operator a simple dashboard for reviewing only the ambiguous cases.

## What it does

- Imports roster Excel files
- Tracks Zoom participant join/leave events
- Computes live attendance status per meeting
- Classifies matches into:
  - `Verified`
  - `AliasVerified`
  - `NameOnly`
  - `Possible`
  - `Unmatched`
- Shows a review queue for weaker matches
- Saves operator-confirmed aliases for future meetings
- Exports attendance as CSV

## Current architecture

- `src/ZoomCheck.Core` — domain models and matching logic
- `src/ZoomCheck.Infrastructure` — Excel parsing and SQLite persistence
- `src/ZoomCheck.Backend` — ASP.NET Core API for roster import, Zoom webhooks, board projection, and export
- `src/ZoomCheck.App` — Avalonia desktop app for the operator dashboard

## Current real-time behavior

The current implementation is **event-driven on the backend** and **refresh-based on the desktop UI**.

- Zoom webhook events can be received immediately by the backend
- Those events are stored in SQLite as participant event logs
- The desktop dashboard reads `GET /api/meetings/{meetingId}/board`
- The screen updates when the operator presses **Refresh**

So today:

- **event collection** can be real-time
- **screen refresh** is currently manual, not push-based

## Requirements

### For normal Windows users

- Use the packaged Windows installer
- No separate .NET installation is needed when using the self-contained Windows package

### For developers running from source

- .NET 8 SDK
- A Zoom Server-to-Server OAuth app for real Zoom integration
- Zoom webhook configuration for live participant events
- Windows is the intended operator runtime, but the backend also runs on Linux

## Windows install for beginners

The intended beginner flow is:

1. Run `ZoomCheck-Setup-x64.exe`
2. Finish the installer wizard
3. Double-click the **ZoomCheck** shortcut
4. ZoomCheck starts its local service automatically
5. Open or join the Zoom meeting
6. Enter the meeting ID, load the roster, refresh during class, review flagged people, and export at the end

> The installer assets for this flow live under `deploy/windows/`.

## Build the Windows installer

On a Windows build machine:

1. Install .NET 8 SDK
2. Install Inno Setup 6
3. Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\windows\build-installer.ps1
```

This produces:

- self-contained app publish output
- self-contained backend publish output
- packaged install folder
- `dist\installer\ZoomCheck-Setup-x64.exe`

You can also build the installer from GitHub Actions on a Windows runner through:

- `.github/workflows/build-windows-installer.yml`

## Quick start from source

### 1. Clone the repository

```bash
git clone https://github.com/ianlyoo/zoomcheck.git
cd zoomcheck
```

### 2. Run the backend

```bash
dotnet run --project src/ZoomCheck.Backend/ZoomCheck.Backend.csproj --urls http://0.0.0.0:5078
```

Useful test endpoints:

- `http://localhost:5078/health`
- `http://localhost:5078/api/zoom/settings-status`

### 3. Run the desktop app

```bash
dotnet run --project src/ZoomCheck.App/ZoomCheck.App.csproj
```

The desktop app currently targets:

```text
http://127.0.0.1:5078/
```

When running from the packaged Windows build, the desktop app starts the backend automatically.

## Simple operator workflow

1. Start the backend
2. Start the desktop app
3. Enter the Zoom meeting ID
4. Load the roster Excel file once
5. Press **Refresh** during class
6. Check only the **Review queue**
7. Save aliases for nickname/device-name cases
8. Export the final attendance CSV

## API overview

### Health and configuration

- `GET /health`
- `GET /api/zoom/settings-status`

### Roster

- `POST /api/roster/import`
- `GET /api/roster`
- `POST /api/roster/alias`

### Zoom events

- `POST /api/zoom/webhooks/events`
- `POST /api/zoom/webhooks/participant-event`
- `POST /api/zoom/webhooks/raw`

### Zoom recovery

- `GET /api/zoom/recovery/last`
- `POST /api/zoom/recovery/run`

### Meeting board and export

- `GET /api/meetings/{meetingId}/board`
- `POST /api/meetings/{meetingId}/seed-demo`
- `GET /api/meetings/{meetingId}/export`

## Example: import a roster

```bash
curl -X POST http://127.0.0.1:5078/api/roster/import \
  -H "Content-Type: application/json" \
  -d '{"filePath":"/absolute/path/to/roster.xlsx"}'
```

## Example: seed demo events

```bash
curl -X POST http://127.0.0.1:5078/api/meetings/demo-meeting/seed-demo
```

## Example: get the live board

```bash
curl http://127.0.0.1:5078/api/meetings/demo-meeting/board
```

## Example: export attendance CSV

```bash
curl http://127.0.0.1:5078/api/meetings/demo-meeting/export
```

## Zoom configuration

Set these values in `src/ZoomCheck.Backend/appsettings.json` or with environment variables:

```json
"Zoom": {
  "WebhookSecretToken": "your-webhook-secret-token",
  "ClientId": "your-s2s-client-id",
  "ClientSecret": "your-s2s-client-secret",
  "AccountId": "your-zoom-account-id",
  "RequestTimestampToleranceSeconds": 300
}
```

What this enables:

- Zoom webhook signature validation
- Zoom `endpoint.url_validation` challenge handling
- Zoom S2S OAuth token acquisition and caching
- Basic configuration status checking through `/api/zoom/settings-status`

## Beginner operator guide

See:

- `docs/windows-operator-guide.md`

## How match confidence works

- `Verified` — strongest match, usually email-based
- `AliasVerified` — operator confirmed this Zoom name before
- `NameOnly` — name matches, but should still be checked quickly
- `Possible` — weak match candidate, review recommended
- `Unmatched` — no safe roster match found

The goal is to let the operator ignore most attendees and focus only on the few people that need review.

## Data storage

The backend currently stores data in SQLite.

Default database path:

```text
data/zoomcheck.db
```

Stored data includes:

- imported roster entries
- manual alias mappings
- participant event logs

## Zoom late-start recovery

If the backend starts after a meeting has already begun, Zoom does not resend previous participant events. The new recovery path helps avoid that gap.

- Configure `ZoomRecovery` in `appsettings.json` (example included):

  ```json
  "ZoomRecovery": {
    "Enabled": true,
    "StartupDelaySeconds": 8,
    "PeriodicScanIntervalSeconds": 0,
    "UsersPageSize": 100,
    "MeetingsPageSize": 300,
    "ParticipantsPageSize": 300,
    "EnableAccountWideUserDiscovery": false,
    "HostUserIds": [],
    "IncludeFallbackMeUser": true
  }
  ```

  - `Enabled`: turns automatic startup recovery on/off (default `true`)
  - `StartupDelaySeconds`: delay before first recovery run
  - `PeriodicScanIntervalSeconds`: optional polling interval; `0` disables periodic scans
  - `HostUserIds`: explicit host IDs to query live meetings for
  - `EnableAccountWideUserDiscovery`: include all active users (requires broader account scope)
  - `IncludeFallbackMeUser`: add `me` as final fallback user
- Recovery uses Zoom `GET /users/{userId}/meetings?type=live` for discovery.
- For each live meeting it calls `GET /metrics/meetings/{meetingId}/participants?type=live` and inserts missing `joined` events with `source = zoom-live-recovery`.
- Endpoint `POST /api/zoom/recovery/run` triggers recovery on demand.
- Endpoint `GET /api/zoom/recovery/last` returns the last completed run summary.
- Dedupe rule: recovery skips participants that already have a currently active event for the same normalized name+email.
- Permissions/scope mismatch on live participant metrics is surfaced as warnings in `POST /api/zoom/recovery/run` response.

## Known limitations

- Desktop UI still uses manual refresh instead of auto-refresh or push updates
- Real Zoom reconciliation after meeting end is not implemented yet
- Packaged installer generation is prepared, but should be built on Windows for the final `setup.exe`

## Recommended next steps

- add automatic dashboard refresh
- add post-meeting reconciliation with Zoom participant/report APIs
- add a backend URL/settings screen in the desktop app
- package the Avalonia app for Windows deployment

## License

No license has been added yet.
