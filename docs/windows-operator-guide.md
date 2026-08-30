# ZoomCheck Windows Operator Guide

## Beginner workflow

For a detailed Korean live-meeting checklist, see [`windows-live-test-ko.md`](windows-live-test-ko.md).

1. Install `ZoomCheck-Setup-x64.exe`
2. Double-click the **ZoomCheck** shortcut
3. Choose Business Server-to-Server OAuth or the Pro Zoom App bridge described in README.ko.md, then open or join the Zoom meeting
4. Type the meeting ID in ZoomCheck
5. Choose the roster Excel file and click **명단 올리기**
6. Click **지금 참가자 불러오기**, then enable real-time synchronization
7. Check the **Review queue** for weak or unmatched names
8. Save aliases when needed
9. Click **Export** at the end of class
10. In Business mode, confirm a true empty snapshot manually; the Pro bridge confirms two consecutive empty snapshots automatically

## Does the user need .NET installed?

No, not when using the packaged self-contained Windows installer.

If you are running from source instead, install .NET 8 SDK first:

- https://dotnet.microsoft.com/en-us/download/dotnet/8.0

## What ZoomCheck starts automatically

The installed backend starts the local web dashboard and opens the browser automatically.

The operator does not need to open a terminal or launch a second program.

## Troubleshooting

### ZoomCheck says the local service could not start

- Close ZoomCheck and open it again
- If the message stays, reinstall ZoomCheck using the Windows installer

### The dashboard opens but attendance does not update

- Check that the meeting ID is correct
- Check that the roster file was loaded
- Check the OAuth status shown in the dashboard
- Verify that the meeting is live and belongs to the OAuth app account
- For 403 responses, verify Dashboard API access and the required scope
- For Pro mode, confirm the companion was opened by a host/co-host, the one-time pairing code has not expired, and the central relay is reachable
- Assign co-host before opening the Zoom App. If the app is closed or reloaded, create a new pairing code
- The Pro path does not request participant email, so review name-only matches carefully

### Zoom names look unfamiliar

- Use the Review queue
- Save aliases for nickname or device-name cases
