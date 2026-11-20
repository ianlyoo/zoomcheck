# Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## Scope and signing process

- Official signed Windows installers (`ZoomCheck-Setup-x64.exe`) are built automatically from this repository (`main` branch) on GitHub-hosted runners via [`.github/workflows/build-windows-installer.yml`](.github/workflows/build-windows-installer.yml).
- Every release signing request requires manual approval by an Approver in SignPath before the certificate is applied.
- Builds executed without SignPath signing configuration (including local builds, pull request checks, and builds prior to SignPath Foundation onboarding) remain unsigned.
- Only ZoomCheck's own Windows installer (`ZoomCheck-Setup-x64.exe`) built from this repository's source code is submitted for signing. Third-party open-source runtime and library dependencies are not signed individually with ZoomCheck's SignPath certificate; they are packaged unmodified as permitted by the [SignPath Foundation terms](https://signpath.org/terms).
- `SHA256SUMS.txt` is regenerated from the final signed installer before release publication so ZoomCheck's built-in updater verifies the distributed binary's SHA-256 checksum.

## Team roles

- **Authors / Committers and reviewers**: [ianlyoo](https://github.com/ianlyoo) (repository maintainer; all changes proposed by external contributors, including build scripts and CI workflows, require maintainer review before merge).
- **Approvers**: [ianlyoo](https://github.com/ianlyoo) (reviews and manually approves each release signing request in SignPath).

Multi-factor authentication (MFA) is required for repository and SignPath accounts with commit, review, or approval privileges.

## Installation and uninstallation

- `ZoomCheck-Setup-x64.exe` installs per-user (`PrivilegesRequired=lowest`) using Inno Setup and does not modify system-wide configuration silently.
- To uninstall ZoomCheck, use Windows **Settings → Apps → Installed apps** (or **Add or remove programs**) and select **ZoomCheck**. Local user data stored under `%LOCALAPPDATA%\ZoomCheck` can be removed manually if desired.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it, with the sole exception of read-only release update checks against GitHub Releases described below.

- **Local operation**: ZoomCheck listens only on `http://127.0.0.1:5078` and stores rosters, attendance logs, and configuration locally under `%LOCALAPPDATA%\ZoomCheck`. ZoomCheck collects no telemetry or usage analytics.
- **Zoom services (operator-initiated)**: When configured and invoked by the operator (Business Dashboard API or Pro Zoom App companion), ZoomCheck communicates with Zoom services under the [Zoom Privacy Statement](https://www.zoom.com/en/trust/privacy/privacy-statement/).
- **Operator-managed HTTPS relay (optional, operator-configured)**: Public installers ship with no relay URL configured. If an operator explicitly configures their own HTTPS relay (`ZoomRelay.BaseUrl`) for Pro mode, ZoomCheck exchanges end-to-end encrypted participant snapshots through that operator-controlled server.
- **Release update checks**: At startup and periodically afterwards, the installed Windows app checks GitHub Releases (`https://github.com/ianlyoo/zoomcheck/releases`) for newer versions and downloads `ZoomCheck-Setup-x64.exe` and `SHA256SUMS.txt` when an update is available. Installation occurs only when the user explicitly selects **Install and restart**. These requests transmit no rosters, attendance records, or local files and are subject to the [GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).

## Reporting issues

- Report bugs, security concerns, or build/signing issues through [GitHub Issues](https://github.com/ianlyoo/zoomcheck/issues).
- Suspected violations of the SignPath Foundation Code of Conduct may also be reported directly to SignPath Foundation (`support@signpath.io`) as described in the [SignPath Foundation terms](https://signpath.org/terms).
