# Zoom Plugin SDK Windows Probe (Throwaway Spike)

Minimal Windows C++20 console probe evaluating whether the Zoom Plugin SDK for Windows (`ZMToolSuiteProxy`) + Public Client PKCE OAuth (`127.0.0.1` loopback redirect) can read a live Zoom Workplace meeting's participant list directly over local IPC without a relay URL, six-digit pairing code, or in-meeting Zoom App.

## Offline build and self-test (no Zoom Marketplace download required)

The Zoom Plugin SDK binary package (`export_h/`, `lib/zToolSuiteIPCProxy.lib`, `bin/zToolSuiteIPCProxy.dll` and runtime DLLs) is distributed only inside the signed-in Zoom App Marketplace console. Without `-DZOOM_PLUGIN_SDK_DIR`, CMake builds against `stubs/` (matching the public Windows Plugin SDK 7.1.0.2020 C++ API signatures):

```powershell
cmake -S spikes/zoom-plugin -B build-spike -A x64
cmake --build build-spike --config Release
.\build-spike\Release\zoomcheck-plugin-probe.exe --self-test
```

`--self-test` verifies:
1. RFC 7636 Appendix B PKCE S256 generation via Windows CNG (`bcrypt.lib`).
2. RFC 8252 `127.0.0.1:0` ephemeral loopback OAuth callback capture via Winsock2.
3. `ZMToolSuiteProxy` lifecycle wiring and UTF-8 `ParticipantSnapshotRequest`-compatible JSON output.

## Live test with the downloaded Zoom Plugin SDK

After downloading and extracting the Windows Plugin SDK zip from Zoom App Marketplace (`Build App > General app > Features > Access > Plugin SDK > Windows`), point CMake at the `x64` directory containing `export_h`, `lib`, and `bin`:

```powershell
cmake -S spikes/zoom-plugin -B build-spike-live -A x64 -DZOOM_PLUGIN_SDK_DIR="C:\path\to\zoom-plugin-sdk-windows\x64"
cmake --build build-spike-live --config Release
.\build-spike-live\Release\zoomcheck-plugin-probe.exe --client-id "<DEVELOPMENT_PUBLIC_CLIENT_ID>"
```

Sign in to Zoom Workplace (7.0.2+) with the same Zoom account that authorizes the browser PKCE prompt, start or join a meeting, and observe the IPC connection and participant snapshot JSON printed to stdout.
