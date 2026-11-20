import json
import os
import pathlib
import re
import shutil
import socket
import ssl
import statistics
import subprocess
import sys
import time
import urllib.request
import zipfile
from xml.sax.saxutils import escape as xml_escape

import psutil
from playwright.sync_api import sync_playwright

RELAY_PORT = 5443
RELAY_ORIGIN = f"https://localhost:{RELAY_PORT}"
RELAY_URL = f"{RELAY_ORIGIN}/"
DASHBOARD_URL = "http://127.0.0.1:5078"
MEETING_ID = "81234567890"
MEETING_UUID = "synthetic-meeting-uuid-001"
ROSTER_COUNT = 300
PRESENT_STUDENT_COUNT = 240
ABSENT_STUDENT_COUNT = ROSTER_COUNT - PRESENT_STUDENT_COUNT
COMPANION_PARTICIPANT_COUNT = PRESENT_STUDENT_COUNT + 1  # host + 240 synthetic students
ROUNDS = 5


def build_synthetic_roster_xlsx(dest_path: pathlib.Path, count: int = ROSTER_COUNT) -> None:
    """Write a minimal valid .xlsx matching ZoomCheck's MinimalXlsxBuilder format."""
    rows = [["번호", "성명", "이메일", "조"]]
    for idx in range(1, count + 1):
        group_no = ((idx - 1) // 30) + 1
        rows.append([
            str(idx),
            f"학생{idx:03d}",
            f"student{idx:03d}@example.com",
            f"{group_no}조",
        ])

    def col_name(col_idx: int) -> str:
        name = ""
        n = col_idx
        while True:
            name = chr(ord("A") + (n % 26)) + name
            n = (n // 26) - 1
            if n < 0:
                return name

    content_types = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />'
        '<Default Extension="xml" ContentType="application/xml" />'
        '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml" />'
        '<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml" />'
        "</Types>"
    )
    root_rels = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml" />'
        "</Relationships>"
    )
    workbook_rels = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml" />'
        "</Relationships>"
    )
    workbook = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
        'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
        '<sheets><sheet name="Roster" sheetId="1" r:id="rId1" /></sheets>'
        "</workbook>"
    )

    sheet_parts = [
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>',
        '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>',
    ]
    for r_idx, row in enumerate(rows, start=1):
        sheet_parts.append(f'<row r="{r_idx}">')
        for c_idx, val in enumerate(row):
            ref = f"{col_name(c_idx)}{r_idx}"
            sheet_parts.append(
                f'<c r="{ref}" t="inlineStr"><is><t xml:space="preserve">{xml_escape(val)}</t></is></c>'
            )
        sheet_parts.append("</row>")
    sheet_parts.append("</sheetData></worksheet>")

    dest_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(dest_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("[Content_Types].xml", content_types.encode("utf-8"))
        zf.writestr("_rels/.rels", root_rels.encode("utf-8"))
        zf.writestr("xl/workbook.xml", workbook.encode("utf-8"))
        zf.writestr("xl/_rels/workbook.xml.rels", workbook_rels.encode("utf-8"))
        zf.writestr("xl/worksheets/sheet1.xml", "".join(sheet_parts).encode("utf-8"))


def build_mock_zoom_sdk_js() -> str:
    participants = [
        {
            "participantUUID": "host-uuid-001",
            "screenName": "진행자001",
            "role": "host",
        }
    ]
    for idx in range(1, PRESENT_STUDENT_COUNT + 1):
        participants.append({
            "participantUUID": f"stu-uuid-{idx:03d}",
            "screenName": f"학생{idx:03d}",
            "role": "attendee",
        })

    payload = json.dumps({
        "meetingID": MEETING_ID,
        "meetingUUID": MEETING_UUID,
        "host": {
            "participantUUID": "host-uuid-001",
            "screenName": "진행자001",
            "role": "host",
        },
        "participants": participants,
    }, ensure_ascii=False)

    return f"""(function () {{
  'use strict';
  var DATA = {payload};
  var APIS = [
    'getSupportedJsApis',
    'getMeetingContext',
    'getMeetingUUID',
    'getUserContext',
    'getMeetingParticipants',
    'onParticipantChange',
    'setParticipantScreenName'
  ];
  window.zoomSdk = {{
    config: function () {{ return Promise.resolve({{ unsupportedApis: [] }}); }},
    getSupportedJsApis: function () {{ return Promise.resolve({{ supportedApis: APIS.slice() }}); }},
    getMeetingContext: function () {{
      return Promise.resolve({{ meetingID: DATA.meetingID, meetingUUID: DATA.meetingUUID }});
    }},
    getMeetingUUID: function () {{ return Promise.resolve({{ meetingUUID: DATA.meetingUUID }}); }},
    getUserContext: function () {{ return Promise.resolve(Object.assign({{}}, DATA.host)); }},
    getMeetingParticipants: function () {{
      return Promise.resolve({{ participants: DATA.participants.map(function (p) {{ return Object.assign({{}}, p); }}) }});
    }},
    onParticipantChange: function () {{ return undefined; }},
    setParticipantScreenName: function () {{ return Promise.resolve({{ message: 'ok' }}); }}
  }};
}})();
"""


def backend_processes():
    return [
        p for p in psutil.process_iter(["name", "pid"])
        if (p.info["name"] or "").lower() == "zoomcheck.backend.exe"
    ]


def stop_backends() -> None:
    procs = backend_processes()
    for p in procs:
        try:
            p.terminate()
        except psutil.NoSuchProcess:
            pass
    _, alive = psutil.wait_procs(procs, timeout=10)
    for p in alive:
        try:
            p.kill()
        except psutil.NoSuchProcess:
            pass
    if alive:
        psutil.wait_procs(alive, timeout=5)
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        if not backend_processes() and not is_port_open("127.0.0.1", 5078):
            return
        time.sleep(0.05)
    raise RuntimeError("ZoomCheck.Backend.exe or port 5078 did not stop cleanly")


def is_port_open(host: str, port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(0.2)
        return s.connect_ex((host, port)) == 0


def wait_http_200(url: str, timeout: float = 60.0, ssl_ctx=None) -> None:
    deadline = time.monotonic() + timeout
    last_err = None
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=2.5, context=ssl_ctx) as resp:
                if resp.status == 200:
                    return
        except Exception as ex:
            last_err = ex
        time.sleep(0.05)
    raise RuntimeError(f"Timed out waiting for HTTP 200 from {url}: {last_err}")


def archive_localappdata_zoomcheck(tag: str) -> None:
    """Move %LOCALAPPDATA%\\ZoomCheck aside while no backend is running so each run has a fresh data dir."""
    if backend_processes():
        raise RuntimeError("Cannot reset data directory while ZoomCheck.Backend.exe is running")
    local_app = pathlib.Path(os.environ["LOCALAPPDATA"]) / "ZoomCheck"
    if local_app.exists():
        archive_root = pathlib.Path(os.environ["RUNNER_TEMP"]) / "data-archive"
        archive_root.mkdir(parents=True, exist_ok=True)
        dest = archive_root / f"{tag}-{int(time.time() * 1000)}"
        shutil.move(str(local_app), str(dest))


def start_relay(version: str, workspace: pathlib.Path, log_dir: pathlib.Path):
    relay_root = workspace / f"relay-{version}" / "src" / "ZoomCheck.Relay"
    relay_dll = relay_root / "bin" / "Release" / "net8.0" / "ZoomCheck.Relay.dll"
    if not relay_dll.is_file():
        raise RuntimeError(f"Missing relay build output: {relay_dll}")

    env = os.environ.copy()
    env.update({
        "ASPNETCORE_ENVIRONMENT": "Production",
        "ASPNETCORE_URLS": RELAY_ORIGIN,
        "ASPNETCORE_Kestrel__Certificates__Default__Subject": "localhost",
        "ASPNETCORE_Kestrel__Certificates__Default__Store": "My",
        "ASPNETCORE_Kestrel__Certificates__Default__Location": "LocalMachine",
        "ASPNETCORE_Kestrel__Certificates__Default__AllowInvalid": "false",
    })
    log_dir.mkdir(parents=True, exist_ok=True)
    stdout_f = open(log_dir / "relay-stdout.log", "w", encoding="utf-8")
    stderr_f = open(log_dir / "relay-stderr.log", "w", encoding="utf-8")
    proc = subprocess.Popen(
        ["dotnet", str(relay_dll), "--contentRoot", str(relay_root)],
        cwd=str(relay_root),
        env=env,
        stdout=stdout_f,
        stderr=stderr_f,
    )
    ssl_ctx = ssl.create_default_context()
    try:
        wait_http_200(f"{RELAY_ORIGIN}/health", timeout=30.0, ssl_ctx=ssl_ctx)
        wait_http_200(f"{RELAY_ORIGIN}/zoom-app/app.js", timeout=10.0, ssl_ctx=ssl_ctx)
    except Exception:
        stop_process(proc, stdout_f, stderr_f)
        out = (log_dir / "relay-stdout.log").read_text(encoding="utf-8", errors="replace")
        err = (log_dir / "relay-stderr.log").read_text(encoding="utf-8", errors="replace")
        raise RuntimeError(f"Relay {version} failed to start.\nSTDOUT:\n{out}\nSTDERR:\n{err}")
    return proc, stdout_f, stderr_f


def stop_process(proc, stdout_f=None, stderr_f=None) -> None:
    try:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=5)
    finally:
        if stdout_f:
            stdout_f.close()
        if stderr_f:
            stderr_f.close()


def backend_env() -> dict:
    env = os.environ.copy()
    env.update({
        "ZOOMCHECK_Dashboard__OpenBrowserOnStart": "false",
        "ZOOMCHECK_Update__Enabled": "false",
        "ZOOMCHECK_ZoomRecovery__Enabled": "false",
    })
    # Ensure no lingering relay override forces a URL or disables relay.
    for key in list(env.keys()):
        if key.upper().startswith("ZOOMCHECK_ZOOMRELAY__"):
            del env[key]
        if key.upper().startswith("ZOOMCHECK_STORAGE__"):
            del env[key]
    return env


def digits_int(text: str) -> int:
    digits = re.sub(r"\D", "", text or "")
    return int(digits) if digits else -1


def wait_busy_hidden(page, timeout: int = 30000) -> None:
    page.locator("#busy").wait_for(state="hidden", timeout=timeout)


def run_once(
    playwright,
    version: str,
    round_num: int,
    workspace: pathlib.Path,
    installer_path: pathlib.Path,
    roster_path: pathlib.Path,
    mock_sdk_js: str,
    shots_dir: pathlib.Path,
) -> dict:
    run_tag = f"r{round_num}-{version}"
    run_dir = pathlib.Path(os.environ["RUNNER_TEMP"]) / f"first-setup-{run_tag}"
    install_dir = run_dir / "app"
    profile_dir = run_dir / "chrome-profile"
    log_dir = run_dir / "logs"
    run_dir.mkdir(parents=True, exist_ok=True)

    stop_backends()
    archive_localappdata_zoomcheck(f"before-{run_tag}")

    relay_proc, relay_out, relay_err = start_relay(version, workspace, log_dir)
    chrome_exe = pathlib.Path(os.environ["PROGRAMFILES"]) / "Google/Chrome/Application/chrome.exe"
    if not chrome_exe.is_file():
        stop_process(relay_proc, relay_out, relay_err)
        raise RuntimeError(f"Chrome executable not found at {chrome_exe}")

    context = None
    relaunch_proc = None
    try:
        context = playwright.chromium.launch_persistent_context(
            str(profile_dir),
            executable_path=str(chrome_exe),
            headless=True,
            viewport={"width": 1440, "height": 1000},
            ignore_https_errors=False,
        )
        dashboard = context.pages[0] if context.pages else context.new_page()

        # ---------------- STEP 1: install ----------------
        t0 = time.perf_counter()
        inst = subprocess.run(
            [
                str(installer_path),
                "/VERYSILENT",
                "/SUPPRESSMSGBOXES",
                "/NORESTART",
                f"/DIR={install_dir}",
            ],
            env=backend_env(),
            check=False,
        )
        if inst.returncode != 0:
            raise RuntimeError(f"{run_tag}: installer exited with {inst.returncode}")
        wait_http_200(f"{DASHBOARD_URL}/health", timeout=90.0)
        procs = backend_processes()
        if len(procs) != 1:
            raise RuntimeError(f"{run_tag}: expected 1 backend process after install, got {len(procs)}")

        with dashboard.expect_response(
            lambda r: "/api/settings/zoom-relay" in r.url and r.request.method == "GET",
            timeout=30000,
        ):
            dashboard.goto(DASHBOARD_URL, wait_until="domcontentloaded", timeout=60000)

        # Fresh first-run shows the 6-page tutorial modal after 250ms; skip it as a user would.
        dashboard.locator("#tutorial-dialog[open]").wait_for(state="visible", timeout=15000)
        dashboard.locator("#btn-skip-tutorial").click()
        dashboard.locator("#tutorial-dialog").wait_for(state="hidden", timeout=10000)
        dashboard.locator("#btn-open-settings").wait_for(state="visible", timeout=10000)
        t_install = time.perf_counter() - t0
        print(
            f"CLOCK version={version} round={round_num} step=install seconds={t_install:.6f}",
            flush=True,
        )

        # ---------------- STEP 2: relay_url ----------------
        t0 = time.perf_counter()
        dashboard.locator("#btn-open-settings").click()
        dashboard.locator("#settings-dialog[open]").wait_for(state="visible", timeout=10000)
        relay_input = dashboard.locator("#relay-base-url")
        relay_input.scroll_into_view_if_needed()
        relay_input.fill(RELAY_URL)
        with dashboard.expect_response(
            lambda r: "/api/settings/zoom-relay" in r.url and r.request.method == "PUT",
            timeout=30000,
        ) as save_wait:
            dashboard.locator("#btn-save-relay-url").click()
        save_resp = save_wait.value
        if save_resp.status != 200:
            raise RuntimeError(f"{run_tag}: PUT /api/settings/zoom-relay returned {save_resp.status}: {save_resp.text()}")
        save_json = save_resp.json()
        wait_busy_hidden(dashboard)

        restart_required = bool(save_json.get("restartRequired"))
        health_checked = False
        if version == "v0.6.3":
            if not restart_required:
                raise RuntimeError(f"{run_tag}: expected restartRequired=true on v0.6.3, got {save_json}")
            # Full exit and relaunch required on v0.6.3.
            stop_backends()
            exe = install_dir / "ZoomCheck.Backend.exe"
            if not exe.is_file():
                raise RuntimeError(f"{run_tag}: installed backend exe missing at {exe}")
            relaunch_proc = subprocess.Popen(
                [str(exe)],
                cwd=str(install_dir),
                env=backend_env(),
            )
            wait_http_200(f"{DASHBOARD_URL}/health", timeout=60.0)
            with dashboard.expect_response(
                lambda r: "/api/settings/zoom-relay" in r.url and r.request.method == "GET",
                timeout=30000,
            ) as after_restart_wait:
                dashboard.goto(DASHBOARD_URL, wait_until="domcontentloaded", timeout=60000)
            after_json = after_restart_wait.value.json()
            if not after_json.get("configured") or after_json.get("restartRequired"):
                raise RuntimeError(f"{run_tag}: relay not active after restart: {after_json}")
            dashboard.locator("#btn-open-settings").wait_for(state="visible", timeout=10000)
        else:
            if restart_required or not save_json.get("configured"):
                raise RuntimeError(f"{run_tag}: expected immediate apply on v0.7.0, got {save_json}")
            health_btn = dashboard.locator("#btn-check-relay-health")
            health_btn.scroll_into_view_if_needed()
            with dashboard.expect_response(
                lambda r: "/api/settings/zoom-relay/health" in r.url and r.request.method == "POST",
                timeout=30000,
            ) as health_wait:
                health_btn.click()
            health_resp = health_wait.value
            if health_resp.status != 200:
                raise RuntimeError(f"{run_tag}: health check HTTP {health_resp.status}: {health_resp.text()}")
            health_json = health_resp.json()
            if not health_json.get("ok"):
                raise RuntimeError(f"{run_tag}: relay health check failed: {health_json}")
            dashboard.locator("#relay-health-status").wait_for(state="visible", timeout=10000)
            health_checked = True

        t_relay = time.perf_counter() - t0
        print(
            f"CLOCK version={version} round={round_num} step=relay_url seconds={t_relay:.6f} "
            f"restart_required={str(restart_required).lower()} health_checked={str(health_checked).lower()}",
            flush=True,
        )

        # ---------------- STEP 3: roster_meeting ----------------
        t0 = time.perf_counter()
        settings_open = dashboard.locator("#settings-dialog").evaluate("el => !!el.open")
        if not settings_open:
            dashboard.locator("#btn-open-settings").click()
            dashboard.locator("#settings-dialog[open]").wait_for(state="visible", timeout=10000)

        roster_input = dashboard.locator("#roster-file")
        roster_input.scroll_into_view_if_needed()
        roster_input.set_input_files(str(roster_path))
        with dashboard.expect_response(
            lambda r: "/api/roster/upload" in r.url and r.request.method == "POST",
            timeout=30000,
        ) as upload_wait:
            dashboard.locator("#btn-upload-roster").click()
        upload_resp = upload_wait.value
        if upload_resp.status != 200:
            raise RuntimeError(f"{run_tag}: roster upload HTTP {upload_resp.status}: {upload_resp.text()}")
        upload_json = upload_resp.json()
        wait_busy_hidden(dashboard)
        if upload_json.get("count") != ROSTER_COUNT:
            raise RuntimeError(f"{run_tag}: expected {ROSTER_COUNT} roster rows, got {upload_json}")

        dashboard.locator('#settings-dialog footer button[type="submit"]').click()
        dashboard.locator("#settings-dialog").wait_for(state="hidden", timeout=10000)

        meeting_input = dashboard.locator("#meeting-id")
        meeting_input.fill(MEETING_ID)
        with dashboard.expect_response(
            lambda r: f"/api/meetings/{MEETING_ID}/board" in r.url and r.request.method == "GET",
            timeout=30000,
        ):
            meeting_input.press("Tab")
        dashboard.wait_for_function(
            "expected => document.getElementById('summary-total')?.textContent?.trim() === String(expected)",
            arg=ROSTER_COUNT,
            timeout=15000,
        )
        t_roster = time.perf_counter() - t0
        print(
            f"CLOCK version={version} round={round_num} step=roster_meeting seconds={t_roster:.6f} "
            f"roster_count={ROSTER_COUNT}",
            flush=True,
        )

        # ---- Simulated Zoom-forced steps 2 & 4 (excluded from measured host steps) ----
        companion = context.new_page()
        companion.route(
            "https://appssdk.zoom.us/sdk.min.js",
            lambda route: route.fulfill(
                status=200,
                content_type="application/javascript; charset=utf-8",
                body=mock_sdk_js,
            ),
        )
        companion.goto(f"{RELAY_ORIGIN}/zoom-app/", wait_until="domcontentloaded", timeout=30000)
        companion.wait_for_function(
            "() => !document.getElementById('btn-connect')?.disabled && document.getElementById('role-badge')?.textContent?.trim() === '호스트'",
            timeout=15000,
        )

        # ---------------- STEP 4: pairing ----------------
        dashboard.bring_to_front()
        t0 = time.perf_counter()
        dashboard.locator("#btn-open-settings").click()
        dashboard.locator("#settings-dialog[open]").wait_for(state="visible", timeout=10000)
        create_btn = dashboard.locator("#btn-create-pairing-code")
        create_btn.scroll_into_view_if_needed()
        with dashboard.expect_response(
            lambda r: "/api/zoom-app/pairing-code" in r.url and r.request.method == "POST",
            timeout=30000,
        ) as code_wait:
            create_btn.click()
        code_resp = code_wait.value
        if code_resp.status != 200:
            raise RuntimeError(f"{run_tag}: pairing code HTTP {code_resp.status}: {code_resp.text()}")
        wait_busy_hidden(dashboard)
        raw_code_text = dashboard.locator("#pairing-code").inner_text()
        pairing_code = re.sub(r"\D", "", raw_code_text)
        if len(pairing_code) != 6:
            raise RuntimeError(f"{run_tag}: invalid pairing code text {raw_code_text!r}")

        companion.bring_to_front()
        companion.locator("#pairing-code").fill(pairing_code)
        companion.locator("#btn-connect").click()
        companion.wait_for_function(
            """expectedCount => {
                const bridge = document.getElementById('bridge-state')?.textContent?.trim();
                const sync = document.getElementById('sync-state')?.textContent?.trim();
                const count = document.getElementById('participant-count')?.textContent?.trim();
                return bridge === '연결됨' && sync === '완료' && count === String(expectedCount);
            }""",
            arg=COMPANION_PARTICIPANT_COUNT,
            timeout=20000,
        )

        dashboard.bring_to_front()
        dashboard.locator('#settings-dialog footer button[type="submit"]').click()
        dashboard.locator("#settings-dialog").wait_for(state="hidden", timeout=10000)
        t_pairing = time.perf_counter() - t0
        print(
            f"CLOCK version={version} round={round_num} step=pairing seconds={t_pairing:.6f} "
            f"companion_participants={COMPANION_PARTICIPANT_COUNT}",
            flush=True,
        )

        # ---------------- STEP 5: first_sync ----------------
        t0 = time.perf_counter()
        dashboard.bring_to_front()
        dashboard.wait_for_function(
            """args => {
                const total = document.getElementById('summary-total')?.textContent?.trim();
                const present = document.getElementById('summary-present')?.textContent?.trim();
                const mPresent = (document.getElementById('metric-present')?.textContent || '').replace(/\\D/g, '');
                const mAbsent = (document.getElementById('metric-absent')?.textContent || '').replace(/\\D/g, '');
                const rows = document.querySelectorAll('#participant-rows tr').length;
                return total === String(args.total)
                    && present === String(args.present)
                    && mPresent === String(args.present)
                    && mAbsent === String(args.absent)
                    && rows > 0;
            }""",
            arg={
                "total": ROSTER_COUNT,
                "present": PRESENT_STUDENT_COUNT,
                "absent": ABSENT_STUDENT_COUNT,
            },
            timeout=30000,
        )
        t_sync = time.perf_counter() - t0
        review_count = digits_int(dashboard.locator("#metric-review").inner_text())
        zoom_app_status = dashboard.locator("#zoom-app-status").inner_text().strip()
        print(
            f"CLOCK version={version} round={round_num} step=first_sync seconds={t_sync:.6f} "
            f"total={ROSTER_COUNT} present={PRESENT_STUDENT_COUNT} absent={ABSENT_STUDENT_COUNT} "
            f"review={review_count}",
            flush=True,
        )

        t_total = t_install + t_relay + t_roster + t_pairing + t_sync
        print(
            f"CLOCK version={version} round={round_num} step=total seconds={t_total:.6f}",
            flush=True,
        )

        if round_num == ROUNDS:
            shots_dir.mkdir(parents=True, exist_ok=True)
            shot_path = shots_dir / f"{version}-dashboard.png"
            dashboard.screenshot(path=str(shot_path), full_page=False)
            print(
                f"SCREENSHOT version={version} path={shot_path.name} "
                f"total={ROSTER_COUNT} present={PRESENT_STUDENT_COUNT} absent={ABSENT_STUDENT_COUNT} "
                f"review={review_count} zoom_app_connected={str(bool(zoom_app_status)).lower()}",
                flush=True,
            )

        return {
            "install": t_install,
            "relay_url": t_relay,
            "roster_meeting": t_roster,
            "pairing": t_pairing,
            "first_sync": t_sync,
            "total": t_total,
            "review": review_count,
        }
    finally:
        if context is not None:
            try:
                context.close()
            except Exception:
                pass
        if relaunch_proc is not None:
            stop_process(relaunch_proc)
        stop_backends()
        stop_process(relay_proc, relay_out, relay_err)
        archive_localappdata_zoomcheck(f"after-{run_tag}")


def main() -> None:
    workspace = pathlib.Path(os.environ["GITHUB_WORKSPACE"])
    runner_temp = pathlib.Path(os.environ["RUNNER_TEMP"])
    installers_root = runner_temp / "installers"
    roster_path = runner_temp / "roster-300.xlsx"
    shots_dir = workspace / "first-setup-shots"

    build_synthetic_roster_xlsx(roster_path, ROSTER_COUNT)
    mock_sdk_js = build_mock_zoom_sdk_js()
    print(
        f"FIXTURE roster={roster_path.name} rows={ROSTER_COUNT} bytes={roster_path.stat().st_size} "
        f"meeting_id={MEETING_ID} companion_participants={COMPANION_PARTICIPANT_COUNT}",
        flush=True,
    )

    results = {"v0.6.3": [], "v0.7.0": []}
    with sync_playwright() as playwright:
        for round_num in range(1, ROUNDS + 1):
            order = ("v0.6.3", "v0.7.0") if (round_num % 2 == 1) else ("v0.7.0", "v0.6.3")
            for version in order:
                installer = installers_root / version / "ZoomCheck-Setup-x64.exe"
                if not installer.is_file():
                    raise RuntimeError(f"Missing staged installer: {installer}")
                rec = run_once(
                    playwright=playwright,
                    version=version,
                    round_num=round_num,
                    workspace=workspace,
                    installer_path=installer,
                    roster_path=roster_path,
                    mock_sdk_js=mock_sdk_js,
                    shots_dir=shots_dir,
                )
                results[version].append(rec)

    steps = ("install", "relay_url", "roster_meeting", "pairing", "first_sync", "total")
    for version in ("v0.6.3", "v0.7.0"):
        for step in steps:
            vals = [r[step] for r in results[version]]
            med = statistics.median(vals)
            lo = min(vals)
            hi = max(vals)
            print(
                f"SUMMARY version={version} step={step} median={med:.6f} min={lo:.6f} max={hi:.6f}",
                flush=True,
            )


if __name__ == "__main__":
    main()
