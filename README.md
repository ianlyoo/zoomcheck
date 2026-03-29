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

- .NET 8 SDK
- A Zoom Server-to-Server OAuth app for real Zoom integration
- Zoom webhook configuration for live participant events
- Windows is the intended operator runtime, but the backend also runs on Linux

## Quick start

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

The desktop app currently assumes the backend is available at:

```text
http://127.0.0.1:5078/
```

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

## Known limitations

- Desktop UI uses manual refresh instead of auto-refresh or push updates
- Real Zoom reconciliation after meeting end is not implemented yet
- Desktop app backend URL is still fixed in code
- Packaging for Windows distribution is not done yet

## Recommended next steps

- add automatic dashboard refresh
- add post-meeting reconciliation with Zoom participant/report APIs
- add a backend URL/settings screen in the desktop app
- package the Avalonia app for Windows deployment

## License

No license has been added yet.
