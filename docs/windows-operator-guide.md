# ZoomCheck Windows Operator Guide

## Beginner workflow

For a detailed Korean live-meeting checklist, see [`windows-live-test-ko.md`](windows-live-test-ko.md).

1. Install `ZoomCheck-Setup-x64.exe`
2. Double-click the **ZoomCheck** shortcut
3. Open or join the Zoom meeting first
4. Type the meeting ID in ZoomCheck
5. Choose the roster Excel file and click **Load roster**
6. Click **Refresh** during class
7. Check the **Review queue** for weak or unmatched names
8. Save aliases when needed
9. Click **Export** at the end of class

## Does the user need .NET installed?

No, not when using the packaged self-contained Windows installer.

If you are running from source instead, install .NET 8 SDK first:

- https://dotnet.microsoft.com/en-us/download/dotnet/8.0

## What ZoomCheck starts automatically

The desktop app starts the bundled local backend automatically and waits for it to become ready.

The operator does not need to open a terminal or launch a second program.

## Troubleshooting

### ZoomCheck says the local service could not start

- Close ZoomCheck and open it again
- If the message stays, reinstall ZoomCheck using the Windows installer

### The dashboard opens but attendance does not update

- Check that the meeting ID is correct
- Check that the roster file was loaded
- Check that Zoom events are reaching the backend

### Zoom names look unfamiliar

- Use the Review queue
- Save aliases for nickname or device-name cases
