/* ZoomCheck 실시간 회의 운영 대시보드 — 외부 의존성 없음 */
(function () {
  'use strict';

  var STORAGE = {
    meetingId: 'zoomcheck.meetingId',
    autoRefresh: 'zoomcheck.autoRefresh',
    interval: 'zoomcheck.autoRefreshSeconds',
    connectionMode: 'zoomcheck.connectionMode',
    group: 'zoomcheck.groupFilter',
    theme: 'zoomcheck.theme',
    tutorialSeen: 'zoomcheck.tutorialSeen.v1'
  };
  var CONNECTION_MODES = ['auto', 'business', 'zoomApp', 'manual'];
  var THEME_OPTIONS = ['system', 'light', 'dark'];
  var MODE_LABEL = {
    auto: '자동',
    business: 'Business API',
    zoomApp: 'Zoom 앱 (Pro)',
    manual: '직접 입력'
  };
  var MAX_ROSTER_BYTES = 20 * 1024 * 1024;
  var MAX_SNAPSHOT_NAMES = 2000;
  var MAX_SESSION_LOG = 60;
  var REVIEW_CONFIDENCE = ['NameOnly', 'Possible'];
  var CONFIDENCE_LABEL = {
    Verified: '확인됨',
    AliasVerified: '별명 확인',
    NameOnly: '이름 일치',
    Possible: '추정 일치',
    Unmatched: '미매칭'
  };
  var EVENT_LABEL = {
    Joined: '입장',
    Left: '퇴장',
    NameChanged: '이름 변경'
  };

  var state = {
    board: null,
    roster: [],
    rows: [],
    connections: [],
    duplicates: [],
    filter: 'all',
    group: '',
    groups: [],
    selectedKey: null,
    pending: 0,
    busyRequests: [],
    meetingGeneration: 0,
    boardEpoch: 0,
    meetingRequests: [],
    timerId: null,
    clockId: null,
    connectionPollId: null,
    nextSyncAt: null,
    lastSyncAt: null,
    lastSyncOk: null,
    lastAttemptAt: null,
    awaitingSnapshot: false,
    requestedAfterReceipt: null,
    connectionDrafts: Object.create(null),
    rosterLoaded: false,
    rosterError: false,
    readinessAction: null,
    sessionLog: [],
    alertAction: null,
    zoomConfigured: false,
    connectionMode: 'auto',
    recommendedMode: null,
    zoomApp: null,
    pairing: null,
    update: null,
    updateAnnouncedFor: null,
    updatePollId: null,
    tutorialStep: 0,
    themePreference: 'system'
  };
  var el = {};

  function $(id) { return document.getElementById(id); }
  function setText(node, value) { if (node) { node.textContent = value; } }
  function asArray(value) { return Array.isArray(value) ? value : []; }
  function readStore(key, fallback) {
    try { var value = localStorage.getItem(key); return value === null ? fallback : value; }
    catch (_) { return fallback; }
  }
  function writeStore(key, value) {
    try { localStorage.setItem(key, String(value)); }
    catch (_) { /* private browsing may reject storage */ }
  }
  function systemTheme() {
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
  function normalizeTheme(value) {
    return THEME_OPTIONS.indexOf(value) >= 0 ? value : 'system';
  }
  function renderThemeControls() {
    var effective = document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light';
    if (el.btnToggleTheme) {
      var nextLabel = effective === 'dark' ? '라이트 모드' : '다크 모드';
      setText(el.btnToggleTheme, nextLabel);
      el.btnToggleTheme.setAttribute('aria-label', nextLabel + '로 전환');
      el.btnToggleTheme.setAttribute('aria-pressed', effective === 'dark' ? 'true' : 'false');
    }
    if (el.themeRadios) {
      el.themeRadios.forEach(function (radio) { radio.checked = radio.value === state.themePreference; });
    }
  }
  function applyTheme(preference, options) {
    options = options || {};
    state.themePreference = normalizeTheme(preference);
    var effective = state.themePreference === 'system' ? systemTheme() : state.themePreference;
    document.documentElement.dataset.theme = effective;
    document.documentElement.style.colorScheme = effective;
    var themeColor = $('theme-color');
    if (themeColor) { themeColor.content = effective === 'dark' ? '#0b1220' : '#f5f7fb'; }
    if (options.persist) { writeStore(STORAGE.theme, state.themePreference); }
    renderThemeControls();
    if (options.notify) {
      toast('ok', effective === 'dark' ? '다크 모드 적용' : '밝은 모드 적용', state.themePreference === 'system' ? 'Windows 화면 설정을 따릅니다.' : '이 PC에 선택을 저장했습니다.');
    }
  }
  function toggleTheme() {
    var current = document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light';
    applyTheme(current === 'dark' ? 'light' : 'dark', { persist: true, notify: true });
  }
  function watchSystemTheme() {
    if (!window.matchMedia) { return; }
    var query = window.matchMedia('(prefers-color-scheme: dark)');
    var onChange = function () {
      if (state.themePreference === 'system') { applyTheme('system', { persist: false, notify: false }); }
    };
    if (query.addEventListener) { query.addEventListener('change', onChange); }
    else if (query.addListener) { query.addListener(onChange); }
  }
  function pad(value) { return value < 10 ? '0' + value : String(value); }
  function formatTime(value) {
    if (!value) { return '–'; }
    var date = value instanceof Date ? value : new Date(value);
    return isNaN(date.getTime()) ? '–' : pad(date.getHours()) + ':' + pad(date.getMinutes()) + ':' + pad(date.getSeconds());
  }
  function formatDateTime(value) {
    if (!value) { return '–'; }
    var date = value instanceof Date ? value : new Date(value);
    if (isNaN(date.getTime())) { return '–'; }
    return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate()) + ' ' + formatTime(date);
  }
  function normalizeSearch(value) {
    return String(value || '').toLocaleLowerCase('ko-KR').replace(/\s+/g, ' ').trim();
  }
  function normalizeGroupName(value) {
    return String(value === null || value === undefined ? '' : value).replace(/\s+/g, ' ').trim();
  }
  function groupKey(value) {
    return normalizeSearch(normalizeGroupName(value));
  }
  function sameGroup(left, right) {
    return groupKey(left) === groupKey(right);
  }
  /* 010-1234-5678 과 01012345678 을 모두 찾을 수 있게 숫자만 남긴 값도 검색에 포함한다. */
  function digitsOnly(value) {
    return String(value === null || value === undefined ? '' : value).replace(/\D/g, '');
  }
  function normalizeMeetingId(value) {
    var trimmed = String(value || '').trim();
    if (/^[0-9\s-]+$/.test(trimmed)) { return trimmed.replace(/\D/g, ''); }
    return trimmed;
  }
  function currentMeetingId() {
    return normalizeMeetingId(el.meetingId.value);
  }
  function intervalSeconds() {
    var value = parseInt(el.intervalInput.value, 10);
    if (isNaN(value)) { value = 10; }
    return Math.min(600, Math.max(5, value));
  }

  function toast(kind, title, message) {
    logSession(kind, title, message);
    var node = document.createElement('div');
    node.className = 'toast is-' + (kind || 'info');
    var strong = document.createElement('strong');
    strong.textContent = title;
    node.appendChild(strong);
    if (message) {
      var span = document.createElement('span');
      span.textContent = message;
      node.appendChild(span);
    }
    el.toastRegion.appendChild(node);
    window.setTimeout(function () { if (node.parentNode) { node.remove(); } }, kind === 'bad' ? 9000 : 4500);
  }

  function beginBusy(message) {
    var token = { message: message || '처리 중…' };
    state.busyRequests.push(token);
    state.pending = state.busyRequests.length;
    setText(el.busyText, token.message);
    el.busy.hidden = false;
    return token;
  }
  function endBusy(token) {
    var index = state.busyRequests.indexOf(token);
    if (index < 0) { return; }
    state.busyRequests.splice(index, 1);
    state.pending = state.busyRequests.length;
    el.busy.hidden = state.pending === 0;
    if (state.pending) { setText(el.busyText, state.busyRequests[state.pending - 1].message); }
  }

  function beginMeetingRequest(message) {
    var context = { meetingId: currentMeetingId(), generation: state.meetingGeneration, boardEpoch: state.boardEpoch, busy: message ? beginBusy(message) : null };
    state.meetingRequests.push(context);
    return context;
  }
  function isCurrentMeetingRequest(context) {
    return context.generation === state.meetingGeneration && context.meetingId === currentMeetingId()
      && (context.boardEpoch === undefined || context.boardEpoch === state.boardEpoch);
  }
  function finishMeetingRequest(context) {
    if (context.busy) { endBusy(context.busy); context.busy = null; }
    var index = state.meetingRequests.indexOf(context);
    if (index >= 0) { state.meetingRequests.splice(index, 1); }
    return isCurrentMeetingRequest(context);
  }
  function resetMeetingSelection() {
    state.meetingGeneration += 1;
    state.meetingRequests.slice().forEach(finishMeetingRequest);
    state.selectedKey = null;
    state.lastSyncAt = null;
    state.lastSyncOk = null;
    state.lastAttemptAt = null;
    state.awaitingSnapshot = false;
    state.requestedAfterReceipt = null;
    state.connectionDrafts = Object.create(null);
    if (el.snapshotResult) { el.snapshotResult.hidden = true; }
    setText(el.lastSyncTime, '–');
    el.syncSummary.querySelector('.status-dot').className = 'status-dot';
    setText(el.syncSummary.lastChild, '동기화 대기');
    hideAlert();
    renderBoard({ meetingId: currentMeetingId(), people: [], currentConnections: [], recentEvents: [] });
    state.board = null;
    scheduleNextSync();
  }

  function logSession(kind, title, message) {
    state.sessionLog.unshift({ at: new Date(), kind: kind || 'info', title: title || '', message: message || '' });
    if (state.sessionLog.length > MAX_SESSION_LOG) { state.sessionLog.length = MAX_SESSION_LOG; }
    renderSessionLog();
  }
  function renderSessionLog() {
    if (!el.sessionLog) { return; }
    el.sessionLog.textContent = '';
    el.sessionLogEmpty.hidden = state.sessionLog.length > 0;
    el.sessionLog.hidden = state.sessionLog.length === 0;
    state.sessionLog.forEach(function (entry) {
      var li = document.createElement('li');
      var time = document.createElement('time');
      time.dateTime = entry.at.toISOString();
      time.textContent = formatTime(entry.at);
      var copy = document.createElement('div');
      var title = document.createElement('strong');
      title.textContent = entry.title;
      copy.appendChild(title);
      if (entry.message) {
        var detail = document.createElement('span');
        detail.textContent = entry.message;
        copy.appendChild(detail);
      }
      li.appendChild(time);
      li.appendChild(copy);
      el.sessionLog.appendChild(li);
    });
  }

  function showAlert(kind, title, message, actionLabel, action) {
    if (el.alertDetails) { el.alertDetails.hidden = true; el.alertDetails.open = false; }
    el.alertBar.hidden = false;
    el.alertDot.className = 'status-dot ' + (kind || 'warn');
    setText(el.alertTitle, title);
    setText(el.alertMessage, message);
    state.alertAction = typeof action === 'function' ? action : null;
    el.alertAction.hidden = !state.alertAction;
    if (state.alertAction) { setText(el.alertAction, actionLabel || '확인'); }
  }
  function hideAlert() {
    el.alertBar.hidden = true;
    state.alertAction = null;
    el.alertAction.hidden = true;
  }

  function describeProblem(problem, status) {
    if (!problem || typeof problem !== 'object') { return 'HTTP ' + status + ' 오류가 발생했습니다.'; }
    var parts = [];
    if (problem.title) { parts.push(String(problem.title)); }
    if (problem.detail) { parts.push(String(problem.detail)); }
    if (problem.errors && typeof problem.errors === 'object') {
      Object.keys(problem.errors).forEach(function (key) {
        if (Array.isArray(problem.errors[key])) { parts.push(problem.errors[key].join(' ')); }
      });
    }
    return parts.length ? parts.join(' — ') : 'HTTP ' + status + ' 오류가 발생했습니다.';
  }
  function ApiError(message, status, problem) {
    this.name = 'ApiError';
    this.message = message;
    this.status = status;
    this.problem = problem || null;
  }
  ApiError.prototype = Object.create(Error.prototype);

  function request(path, options) {
    var opts = options || {};
    var headers = opts.headers || (opts.json !== undefined
      ? { 'Content-Type': 'application/json', Accept: 'application/json' }
      : { Accept: 'application/json' });
    return fetch(path, {
      method: opts.method || 'GET',
      headers: headers,
      body: opts.json !== undefined ? JSON.stringify(opts.json) : opts.body,
      cache: 'no-store',
      credentials: 'same-origin'
    }).then(function (response) {
      var type = response.headers.get('content-type') || '';
      if (!response.ok) {
        if (type.indexOf('json') >= 0) {
          return response.json().then(function (problem) {
            throw new ApiError(describeProblem(problem, response.status), response.status, problem);
          });
        }
        return response.text().then(function (raw) {
          throw new ApiError(raw ? raw.slice(0, 300) : 'HTTP ' + response.status + ' 오류', response.status);
        });
      }
      if (opts.raw) { return response; }
      if (response.status === 204 || type.indexOf('json') < 0) { return null; }
      return response.json();
    }, function () {
      throw new ApiError('로컬 백엔드에 연결할 수 없습니다.', 0);
    });
  }

  function setDot(node, status) {
    node.classList.remove('is-ok', 'is-bad', 'warn');
    if (status === true) { node.classList.add('is-ok'); }
    if (status === false) { node.classList.add('is-bad'); }
    if (status === 'warn') { node.classList.add('warn'); }
  }

  function checkHealth() {
    return request('/health').then(function (payload) {
      var ok = payload && payload.status === 'ok';
      setDot(el.healthDot, ok);
      setText(el.healthText, ok ? '연결됨' : '오류');
      return ok;
    }, function (error) {
      setDot(el.healthDot, false);
      setText(el.healthText, '연결 실패');
      showAlert('bad', '백엔드 연결 실패', error.message, '재시도', checkHealth);
      return false;
    });
  }

  function normalizeMode(value) {
    return CONNECTION_MODES.indexOf(value) >= 0 ? value : 'auto';
  }

  /* 자동 모드에서 실제로 사용할 경로를 결정한다. Zoom 앱 연결이 살아 있으면 그것을 우선한다. */
  function effectiveMode() {
    if (state.connectionMode !== 'auto') { return state.connectionMode; }
    if (state.zoomApp && (state.zoomApp.sessionActive || state.zoomApp.connected)) { return 'zoomApp'; }
    if (state.zoomConfigured) { return 'business'; }
    if (state.recommendedMode && state.recommendedMode !== 'auto') { return state.recommendedMode; }
    return 'business';
  }

  function applyConnectionMode(mode, options) {
    var opts = options || {};
    state.connectionMode = normalizeMode(mode);
    writeStore(STORAGE.connectionMode, state.connectionMode);
    el.modeRadios.forEach(function (radio) { radio.checked = radio.value === state.connectionMode; });
    renderModeNote();
    renderZoomAppStrip();
    if (opts.notify) {
      toast('ok', '연결 방식 변경', MODE_LABEL[state.connectionMode] + '으로 동기화합니다.');
    }
  }

  function renderModeNote() {
    var active = effectiveMode();
    var note = '선택: ' + MODE_LABEL[state.connectionMode];
    if (state.connectionMode === 'auto') { note += ' · 현재 적용: ' + MODE_LABEL[active]; }
    if (state.recommendedMode) { note += ' · 권장: ' + (MODE_LABEL[state.recommendedMode] || state.recommendedMode); }
    if (active === 'manual') { note += ' · 아래 “전체 참가자 목록 직접 붙여넣기”를 사용하세요.'; }
    setText(el.modeNote, note);
    renderModeControls();
    renderReadiness();
  }

  function openSettingsSection(section, focusId) {
    openSettings();
    var target = $('settings-' + section);
    if (target && target.scrollIntoView) { target.scrollIntoView({ block: 'start' }); }
    var focus = focusId ? $(focusId) : target;
    if (focus) { focus.focus(); }
  }
  function renderModeControls() {
    var manual = effectiveMode() === 'manual';
    setText(el.btnSyncZoom, manual ? '참가자 목록 입력' : '지금 동기화');
    el.chkAutoRefresh.disabled = manual;
    el.settingsAutoRefresh.disabled = manual;
    el.intervalInput.disabled = manual;
    setText(el.autoStatus, manual ? '직접 입력' : (el.chkAutoRefresh.checked ? '활성' : '꺼짐'));
    setText(el.autoModeNote, manual ? '직접 입력 모드에서는 자동 수집하지 않습니다. 이전 자동 수집 설정은 보존됩니다.' : '연결된 회의의 참가자 전체 목록을 주기적으로 수집합니다.');
    if (manual) { state.nextSyncAt = null; setText(el.nextSyncTime, '–'); }
  }
  function renderReadiness() {
    if (!el.readiness) { return; }
    var title = '', note = '', label = '', action = null;
    if (state.rosterError) {
      title = '명단을 불러오지 못했습니다'; note = '연결을 확인한 뒤 저장된 명단을 다시 읽어 주세요.'; label = '명단 다시 읽기'; action = function () { loadRoster(true); };
    } else if (!state.rosterLoaded && !state.roster.length) {
      title = '저장된 명단 확인 중'; note = '회의 준비 상태를 확인하고 있습니다.';
    } else if (!state.roster.length) {
      title = '1. 오늘 사용할 명단을 올려 주세요'; note = 'Excel 명단 → 회의 ID → 연결 방식 순서로 준비합니다.'; label = '명단 올리기'; action = function () { openSettingsSection('roster', 'roster-file'); };
    } else if (!currentMeetingId()) {
      title = '2. 회의 ID를 확인해 주세요'; note = '명단 ' + state.roster.length + '명 등록됨 · 출석과 검토는 이 회의에 저장됩니다.'; label = '회의 ID 입력'; action = function () { el.meetingId.focus(); };
    } else if (effectiveMode() === 'zoomApp' && (!state.zoomApp || !state.zoomApp.sessionActive)) {
      title = '3. Zoom 앱을 연결해 주세요'; note = '회의 안 ZoomCheck 앱에 페어링 코드를 입력하세요.'; label = '연결 준비'; action = function () { openSettingsSection('pairing', 'btn-create-pairing-code'); };
    } else if (effectiveMode() === 'business' && !state.zoomConfigured) {
      title = '3. 수집 방식을 확인해 주세요'; note = 'API 설정을 확인하거나 Zoom 앱·직접 입력을 선택하세요.'; label = '연결 방식 선택'; action = function () { openSettingsSection('mode', 'mode-auto'); };
    } else if (!state.board || (!state.board.lastReceivedAt && !state.board.latestEventAt)) {
      title = '참가자 목록을 처음 받아 주세요'; note = '명단은 준비되었습니다. 참가자를 받기 전의 0명은 출석 결과가 아닙니다.';
      label = effectiveMode() === 'manual' ? '참가자 목록 입력' : '참가자 받기'; action = function () { syncNow({ silent: false }); };
    }
    el.readiness.hidden = !title;
    setText(el.readinessTitle, title); setText(el.readinessNote, note);
    setText(el.btnNextStep, label); el.btnNextStep.hidden = !action;
    state.readinessAction = action;
  }
  function sourceLabel(source) {
    if (source === 'web-dashboard' || source === 'manual-snapshot') { return '직접 입력'; }
    if (source === 'zoom-live-participants') { return 'Zoom 참가자'; }
    if (source === 'zoom-api') { return 'Zoom API'; }
    if (source === 'zoom-webhook' || source === 'zoom-webhook-raw') { return 'Zoom 활동 알림'; }
    if (String(source).indexOf('zoom-app') >= 0 || String(source).indexOf('relay') >= 0) { return 'Zoom 앱'; }
    return source || '저장된 출석';
  }
  function renderFreshness() {
    var board = state.board || {};
    var received = board.lastReceivedAt || null;
    var time = received ? new Date(received).getTime() : NaN;
    var valid = !isNaN(time);
    var age = valid ? Math.max(0, Math.floor((Date.now() - time) / 1000)) : 0;
    var sources = asArray(board.snapshotSources).map(function (item) { return sourceLabel(item.source); });
    sources = sources.filter(function (item, index) { return sources.indexOf(item) === index; });
    var source = sources.join(' + ') || (effectiveMode() === 'manual' ? '직접 입력' : MODE_LABEL[effectiveMode()]);
    if (valid && state.awaitingSnapshot && (!state.requestedAfterReceipt || time > new Date(state.requestedAfterReceipt).getTime())) { state.awaitingSnapshot = false; state.lastSyncOk = true; }
    state.lastSyncAt = valid ? new Date(received) : null;
    setText(el.lastSyncTime, valid ? formatTime(received) : '–');
    setText(el.summaryTime, valid ? source + ' · 마지막 참가자 수신 ' + formatTime(received) + ' · ' + (age < 60 ? age + '초 전' : Math.floor(age / 60) + '분 전')
      : board.latestEventAt ? '최근 활동 ' + formatDateTime(board.latestEventAt) + ' · 참가자 수신 기록 없음' : '아직 참가자 수신 기록이 없습니다.');
    var automatic = effectiveMode() !== 'manual' && el.chkAutoRefresh.checked;
    var late = automatic && valid && age > Math.max(30, intervalSeconds() * 3);
    var status = state.lastSyncOk === false ? '연결 실패 · 기존 출석 유지'
      : late ? '수신 지연 · 기존 출석 유지'
      : state.awaitingSnapshot ? '동기화 요청됨 · 참가자 수신 대기'
      : valid ? '참가자 수신 기록 있음' : '참가자 수신 대기';
    setText(el.sourceStatus, status + (state.lastAttemptAt ? ' · 마지막 시도 ' + formatTime(state.lastAttemptAt) : '')
      + (board.generatedAt ? ' · 화면 조회 ' + formatTime(board.generatedAt) : ''));
    el.syncSummary.querySelector('.status-dot').className = 'status-dot ' + (state.lastSyncOk === false ? 'bad' : late || state.awaitingSnapshot ? 'warn' : valid ? 'ok' : '');
    setText(el.syncSummary.lastChild, status);
    setText(el.activityMode, '저장된 활동');
  }

  function describeZoomAppRole(role) {
    if (role === 'host') { return '호스트'; }
    if (role === 'coHost' || role === 'cohost' || role === 'co-host') { return '공동호스트'; }
    return role ? String(role) : '권한 미확인';
  }

  function renderZoomAppStrip() {
    var app = state.zoomApp || {};
    var connected = !!app.connected;
    var sessionActive = app.sessionActive !== undefined ? !!app.sessionActive : connected;
    setDot(el.zoomAppDot, connected ? true : (sessionActive ? 'warn' : (state.connectionMode === 'zoomApp' ? false : null)));
    setText(el.zoomAppStatus, connected ? describeZoomAppRole(app.role) + ' 연결' : (sessionActive ? '연결 유지' : '미연결'));
    if (connected) {
      setText(el.zoomAppSettingsStatus, 'Zoom 앱이 연결되어 있습니다 · 회의 ' + (app.meetingId || '–')
        + ' · ' + describeZoomAppRole(app.role) + ' · 마지막 수신 ' + formatTime(app.lastSeenAt));
      setText(el.pairingSession, '연결된 Zoom 앱: 회의 ' + (app.meetingId || '–') + ' · ' + describeZoomAppRole(app.role));
    } else if (sessionActive) {
      setText(el.zoomAppSettingsStatus, '페어링은 유지 중이며 참가자 신호를 다시 확인하고 있습니다 · 회의 '
        + (app.meetingId || '–') + ' · 마지막 수신 ' + formatTime(app.lastSeenAt));
      setText(el.pairingSession, '연결 유지 중: 회의 ' + (app.meetingId || '–') + ' · Zoom 앱 신호 재확인 중');
    } else {
      setText(el.zoomAppSettingsStatus, '연결 대기 중입니다. 코드를 만들고 회의 안의 ZoomCheck 앱에 입력하세요. 별도 터널이나 API 키는 필요 없습니다.');
      setText(el.pairingSession, '아직 연결된 Zoom 앱이 없습니다.');
    }
    if (app.homeUrl) {
      setText(el.zoomAppHomeUrl, app.homeUrl);
    } else {
      setText(el.zoomAppHomeUrl, '릴레이 주소 미설정 — 운영자 배포가 필요합니다. 기존 직접 HTTPS 방식은 계속 사용할 수 있습니다.');
    }
    renderPairingCode();
  }

  function renderPairingCode() {
    var app = state.zoomApp || {};
    var sessionActive = app.sessionActive !== undefined ? !!app.sessionActive : !!app.connected;
    if (sessionActive) {
      setText(el.pairingCode, '연결 유지 중');
      setText(el.pairingExpiry, '6자리 코드는 최초 연결 후 폐기되며, 코드 만료와 무관하게 현재 세션이 유지됩니다.');
      return;
    }
    if (!state.pairing) {
      setText(el.pairingCode, '– – – – – –');
      setText(el.pairingExpiry, '코드를 생성하면 유효 시간이 표시됩니다.');
      return;
    }
    setText(el.pairingCode, String(state.pairing.code || '').split('').join(' '));
    var expiresAt = state.pairing.expiresAt ? new Date(state.pairing.expiresAt) : null;
    if (!expiresAt || isNaN(expiresAt.getTime())) {
      setText(el.pairingExpiry, '유효 시간 정보를 확인할 수 없습니다.');
      return;
    }
    var remaining = Math.round((expiresAt.getTime() - Date.now()) / 1000);
    if (remaining <= 0) {
      setText(el.pairingExpiry, '코드가 만료되었습니다. 다시 생성하세요.');
      return;
    }
    setText(el.pairingExpiry, formatTime(expiresAt) + '까지 유효 · 남은 시간 '
      + Math.floor(remaining / 60) + '분 ' + pad(remaining % 60) + '초');
  }

  function checkZoomConnection(showToast) {
    return request('/api/zoom/connection-status').then(function (status) {
      var payload = status || {};
      var business = payload.business || {};
      /* configured/business.configured 둘 다 지원해 기존 응답과 호환한다. */
      state.zoomConfigured = business.configured !== undefined ? !!business.configured : !!payload.configured;
      state.zoomApp = payload.zoomApp || null;
      state.recommendedMode = payload.recommendedMode ? normalizeMode(payload.recommendedMode) : null;
      if (state.zoomApp && (state.zoomApp.sessionActive || state.zoomApp.connected)) {
        state.pairing = null;
        if (!currentMeetingId() && state.zoomApp.meetingId) {
          var connectedMeetingId = normalizeMeetingId(state.zoomApp.meetingId);
          el.meetingId.value = connectedMeetingId;
          writeStore(STORAGE.meetingId, connectedMeetingId);
          resetMeetingSelection();
        }
      } else if (state.zoomApp && state.zoomApp.pairingCodeExpiresAt && !state.pairing) {
        state.pairing = { code: null, expiresAt: state.zoomApp.pairingCodeExpiresAt };
      }
      setDot(el.zoomDot, state.zoomConfigured);
      setText(el.zoomStatus, state.zoomConfigured ? '설정됨' : '미설정');
      setText(el.apiSettingsStatus, state.zoomConfigured
        ? 'OAuth 자격증명이 설정되어 있습니다. 실제 권한은 지금 동기화에서 확인됩니다.'
        : 'Windows 사용자 환경 변수 Account ID, Client ID, Client Secret을 설정한 뒤 앱을 다시 시작하세요. Pro 요금제라면 Zoom 앱 페어링을 사용하세요.');
      renderZoomAppStrip();
      renderModeNote();
      if (showToast) {
        toast(state.zoomConfigured ? 'ok' : 'warn', state.zoomConfigured ? 'Zoom API 설정됨' : 'Zoom API 설정 필요',
          state.zoomConfigured ? '실제 권한은 지금 동기화로 확인합니다.' : 'Server-to-Server OAuth 자격증명이 필요합니다.');
      }
      return payload;
    }, function (error) {
      state.zoomConfigured = false;
      setDot(el.zoomDot, false);
      setText(el.zoomStatus, '확인 실패');
      setText(el.apiSettingsStatus, error.message);
      if (showToast) { toast('bad', 'Zoom API 상태 확인 실패', error.message); }
    });
  }

  function createPairingCode() {
    var busy = beginBusy('페어링 코드를 만드는 중…');
    return request('/api/zoom-app/pairing-code', { method: 'POST', json: {} }).then(function (result) {
      endBusy(busy);
      state.pairing = { code: result && result.code ? result.code : null, expiresAt: result ? result.expiresAt : null };
      if (result && result.homeUrl) {
        state.zoomApp = state.zoomApp || {};
        state.zoomApp.homeUrl = result.homeUrl;
      }
      renderZoomAppStrip();
      toast('ok', '페어링 코드 생성', '회의 안 ZoomCheck 앱에 코드를 입력하세요.');
      return result;
    }, function (error) {
      endBusy(busy);
      toast('bad', '페어링 코드 생성 실패', error.message);
      return null;
    });
  }

  function copyToClipboard(value, okTitle) {
    if (!value) { toast('warn', '복사할 내용이 없습니다', '먼저 값을 생성하거나 설정하세요.'); return; }
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(value).then(function () {
        toast('ok', okTitle, value);
      }, function () {
        toast('warn', '복사할 수 없습니다', '값을 직접 선택해 복사하세요.');
      });
      return;
    }
    toast('warn', '복사할 수 없습니다', '이 브라우저는 클립보드를 지원하지 않습니다.');
  }

  function requestZoomAppSync(options) {
    var opts = options || {};
    if (state.zoomApp && state.zoomApp.meetingId && normalizeMeetingId(state.zoomApp.meetingId) !== currentMeetingId()) {
      showAlert('warn', '연결된 회의가 다릅니다', '선택한 회의 ID와 Zoom 앱의 회의 ID를 확인하세요.', '회의 ID 확인', function () { el.meetingId.focus(); });
      return Promise.resolve(null);
    }
    var context = beginMeetingRequest(opts.silent ? null : 'Zoom 앱에 동기화를 요청하는 중…');
    var previousReceipt = state.board && state.board.lastReceivedAt;
    return request('/api/zoom-app/sync', { method: 'POST', json: {} }).then(function (result) {
      if (!finishMeetingRequest(context)) { return null; }
      hideAlert();
      state.awaitingSnapshot = true; state.requestedAfterReceipt = previousReceipt; markSync(null);
      scheduleNextSync();
      logSession('ok', 'Zoom 앱 동기화 요청', result && result.requestedRevision !== undefined ? 'revision ' + result.requestedRevision : '');
      if (!opts.silent) { toast('ok', 'Zoom 앱 동기화 요청', '회의 안 앱이 참가자 목록을 곧 전송합니다.'); }
      /* 앱이 스냅샷을 올릴 시간을 주고 저장된 보드를 다시 읽는다. */
      window.setTimeout(function () {
        if (isCurrentMeetingRequest(context)) { refreshBoard({ silent: true }); }
      }, 2500);
      return result;
    }, function (error) {
      if (!finishMeetingRequest(context)) { return null; }
      markSync(false, ' Zoom 앱 동기화 실패');
      scheduleNextSync();
      showSyncError(error, 'zoomApp');
      logSession('bad', 'Zoom 앱 동기화 실패', error.message);
      if (!opts.silent) { toast('bad', 'Zoom 앱 동기화 실패', error.message); }
      return null;
    });
  }

  /* 선택된 연결 방식에 따라 동기화 경로를 나눈다. */
  function syncNow(options) {
    var opts = options || {};
    var mode = effectiveMode();
    if (mode === 'zoomApp') { return requestZoomAppSync(opts); }
    if (mode === 'manual') {
      if (!opts.silent) {
        openSettingsSection('manual', 'snapshot-names');
      }
      return Promise.resolve(null);
    }
    return syncZoomParticipants(opts);
  }

  function loadRoster(showToast) {
    return request('/api/roster').then(function (people) {
      state.rosterLoaded = true; state.rosterError = false;
      state.roster = asArray(people);
      setText(el.rosterSummary, '명단 ' + state.roster.length + '명');
      setText(el.rosterNote, state.roster.length
        ? '현재 ' + state.roster.length + '명의 명단이 등록되어 있습니다.'
        : '등록된 명단이 없습니다. Excel 파일을 올려 주세요.');
      if (showToast) { toast('ok', '명단 확인 완료', state.roster.length + '명'); }
      if (state.board) { renderBoard(state.board); }
      else { state.groups = collectGroups(null); renderGroupOptions(); }
      renderReadiness();
      return state.roster;
    }, function (error) {
      state.rosterLoaded = true; state.rosterError = true; renderReadiness();
      setText(el.rosterNote, '명단을 읽지 못했습니다 — ' + error.message);
      if (showToast) { toast('bad', '명단 읽기 실패', error.message); }
      return [];
    });
  }

  function uploadRoster() {
    var file = el.rosterFile.files && el.rosterFile.files[0];
    if (!file) { toast('warn', 'Excel 파일을 선택하세요', '.xlsx 또는 .xls 명단이 필요합니다.'); return; }
    var lower = file.name.toLowerCase();
    if (lower.slice(-5) !== '.xlsx' && lower.slice(-4) !== '.xls') { toast('warn', '지원하지 않는 파일', 'Excel .xlsx 또는 .xls 파일만 올릴 수 있습니다.'); return; }
    if (file.size > MAX_ROSTER_BYTES) { toast('warn', '파일이 너무 큽니다', '20MB 이하 파일을 사용하세요.'); return; }
    var form = new FormData();
    form.append('file', file, file.name);
    var busy = beginBusy('명단을 올리는 중…');
    request('/api/roster/upload', { method: 'POST', headers: { Accept: 'application/json' }, body: form }).then(function (result) {
      endBusy(busy);
      el.rosterFile.value = '';
      toast('ok', '명단 업로드 완료', (result && result.count !== undefined ? result.count : '') + '명');
      return Promise.all([loadRoster(false), refreshBoard({ silent: true })]);
    }, function (error) {
      endBusy(busy);
      toast('bad', '명단 업로드 실패', error.message);
    });
  }

  function requireMeetingId() {
    var meetingId = currentMeetingId();
    if (!meetingId) {
      el.meetingId.focus();
      toast('warn', '회의 ID가 필요합니다', '상단에 Zoom 회의 ID를 입력하세요.');
      return null;
    }
    return meetingId;
  }

  function markSync(ok) {
    state.lastAttemptAt = new Date();
    state.lastSyncOk = ok;
    if (ok !== null) { state.awaitingSnapshot = false; }
    renderFreshness();
  }
  function showSyncError(error, mode) {
    var message = '일시적으로 연결할 수 없습니다. 기존 출석은 유지됩니다.';
    var label = '다시 시도', action = function () { syncNow({ silent: false }); };
    if (error.status === 403 || error.status === 401) {
      message = '연결 권한을 확인해 주세요. 기존 출석은 유지됩니다.'; label = '권한 설정 확인';
      action = function () { openSettingsSection(mode === 'zoomApp' ? 'pairing' : 'api'); };
    } else if (error.status === 404) {
      message = '회의를 찾지 못했습니다. 회의 ID와 진행 상태를 확인하세요.'; label = '회의 ID 확인'; action = function () { el.meetingId.focus(); };
    } else if (error.status === 429) {
      message = '요청이 너무 잦습니다. 동기화 간격을 늘린 뒤 다시 시도하세요.'; label = '간격 조정'; action = function () { openSettingsSection('auto', 'autorefresh-interval'); };
    } else if (error.status === 409 && mode === 'zoomApp') {
      message = 'Zoom 앱 연결 상태를 확인해 주세요.'; label = '연결 확인'; action = function () { openSettingsSection('pairing'); };
    } else if (error.status === 503 && mode === 'business' && !state.zoomConfigured) {
      message = 'API 연결 설정이 필요합니다. 다른 수집 방식도 선택할 수 있습니다.'; label = '연결 방식 선택'; action = function () { openSettingsSection('mode'); };
    }
    showAlert('warn', '참가자 수신 실패', message, label, action);
    if (el.alertDetails) { el.alertDetails.hidden = false; setText(el.alertTechnical, error.message); }
  }

  function syncZoomParticipants(options) {
    var opts = options || {};
    var meetingId = opts.silent ? currentMeetingId() : requireMeetingId();
    if (!meetingId) { return Promise.resolve(null); }
    var path = '/api/zoom/meetings/' + encodeURIComponent(meetingId) + '/sync';
    if (opts.allowEmpty) { path += '?allowEmptySnapshot=true'; }
    var context = beginMeetingRequest(opts.silent ? null : 'Zoom 참가자를 동기화하는 중…');
    return request(path, { method: 'POST' }).then(function (result) {
      if (!finishMeetingRequest(context)) { return null; }
      hideAlert();
      state.zoomConfigured = true;
      setDot(el.zoomDot, true);
      setText(el.zoomStatus, '정상');
      if (result && result.snapshot && result.snapshot.board) { renderBoard(result.snapshot.board); }
      markSync(true, ' 참가자 ' + (result && result.activeParticipants !== undefined ? result.activeParticipants : '–') + '명');
      scheduleNextSync();
      logSession('ok', 'Zoom 동기화', '현재 ' + (result && result.activeParticipants !== undefined ? result.activeParticipants : '–') + '명');
      if (!opts.silent) { toast('ok', '실시간 동기화 완료', '현재 참가자 ' + result.activeParticipants + '명'); }
      return result;
    }, function (error) {
      if (!finishMeetingRequest(context)) { return null; }
      markSync(false, ' 동기화 실패');
      scheduleNextSync();
      var emptyRejected = error.status === 409;
      if (emptyRejected) {
        showAlert('warn', 'Zoom이 0명을 반환했습니다', '빈 응답일 수 있어 기존 출석을 유지했습니다. 실제로 회의가 비었다면 확인하세요.', '전원 퇴장 확인', function () {
          if (isCurrentMeetingRequest(context)) {
            confirmAction('회의 ' + meetingId + '의 참가자가 모두 퇴장했습니까?', function () {
              if (isCurrentMeetingRequest(context)) { return syncZoomParticipants({ allowEmpty: true, silent: false }); }
            });
          }
        });
      } else { showSyncError(error, 'business'); }
      logSession('bad', 'Zoom 동기화 실패', error.message);
      if (!opts.silent) { toast('bad', 'Zoom 동기화 실패', error.message); }
      return null;
    });
  }

  function refreshBoard(options) {
    var opts = options || {};
    var meetingId = opts.silent ? currentMeetingId() : requireMeetingId();
    if (!meetingId) { return Promise.resolve(null); }
    var context = beginMeetingRequest(opts.silent ? null : '출석 현황을 불러오는 중…');
    return request('/api/meetings/' + encodeURIComponent(meetingId) + '/board').then(function (board) {
      if (!finishMeetingRequest(context)) { return null; }
      renderBoard(board);
      if (opts.notify) { toast('ok', '화면 새로고침 완료', '저장된 최신 상태를 표시합니다.'); }
      return board;
    }, function (error) {
      if (!finishMeetingRequest(context)) { return null; }
      if (!opts.silent) { toast('bad', '화면 새로고침 실패', error.message); }
      return null;
    });
  }

  function rosterById() {
    var map = {};
    state.roster.forEach(function (person) { map[person.id] = person; });
    return map;
  }

  function extractConnections(board) {
    var source = board.currentConnections || board.participantConnections || board.connections || [];
    return asArray(source).map(function (item, index) {
      var rawName = item.rawDisplayName || item.rawName || item.originalName || item.zoomName || item.displayName || item.participantName || '';
      var canonicalName = item.canonicalName || item.displayName || item.participantName || rawName;
      var presenceKey = item.presenceKey || item.connectionKey || null;
      return {
        key: presenceKey || item.id || ('connection-' + index + '-' + rawName),
        presenceKey: presenceKey,
        rawName: rawName,
        canonicalName: canonicalName,
        displayName: item.displayName || '',
        email: item.participantEmail || item.email || null,
        firstSeenAt: item.firstSeenAt || item.joinedAt || null,
        lastSeenAt: item.lastSeenAt || item.updatedAt || null,
        rosterPersonId: item.matchedRosterPersonId || item.rosterPersonId || null,
        matchedRosterPersonName: item.matchedRosterPersonName || '',
        confidence: item.confidence || item.matchConfidence || 'Unmatched',
        source: item.source || '',
        manualMatch: !!item.manualMatch, reviewStatus: item.reviewStatus || 'none',
        evidenceToken: item.evidenceToken, matchReason: item.matchReason || ''
      };
    });
  }

  function deriveDuplicateGroups(board, connections, rosterMap) {
    var explicit = board.duplicateConnectionGroups || board.duplicateConnections || board.duplicateGroups;
    if (Array.isArray(explicit)) {
      return explicit.map(function (group, index) {
        var groupConnections = asArray(group.connections || group.participants || group.items).map(function (item, itemIndex) {
          return {
            key: item.presenceKey || item.connectionKey || item.id || ('duplicate-' + index + '-' + itemIndex),
            presenceKey: item.presenceKey || item.connectionKey || null,
            rawName: item.rawDisplayName || item.rawName || item.originalName || item.zoomName || item.displayName || item.participantName || '',
            canonicalName: item.canonicalName || item.displayName || item.participantName || '',
            displayName: item.displayName || '',
            email: item.participantEmail || item.email || null,
            firstSeenAt: item.firstSeenAt || item.joinedAt || null,
            lastSeenAt: item.lastSeenAt || item.updatedAt || null,
            source: item.source || '', manualMatch: !!item.manualMatch,
            reviewStatus: item.reviewStatus || 'none', evidenceToken: item.evidenceToken, matchReason: item.matchReason || ''
          };
        });
        var personId = group.rosterPersonId || group.matchedRosterPersonId || null;
        return {
          key: group.key || personId || ('group-' + index),
          rosterPersonId: personId,
          personName: group.personName || group.rosterPersonName || group.name || (rosterMap[personId] && rosterMap[personId].name) || '검토 필요',
          connections: groupConnections,
          reason: group.reason || '같은 명단 인물로 매칭된 여러 연결'
        };
      }).filter(function (group) { return group.connections.length > 1; });
    }
    var byPerson = {};
    connections.forEach(function (connection) {
      if (!connection.rosterPersonId) { return; }
      if (!byPerson[connection.rosterPersonId]) { byPerson[connection.rosterPersonId] = []; }
      byPerson[connection.rosterPersonId].push(connection);
    });
    return Object.keys(byPerson).filter(function (personId) { return byPerson[personId].length > 1; }).map(function (personId) {
      return {
        key: personId,
        rosterPersonId: personId,
        personName: rosterMap[personId] ? rosterMap[personId].name : '검토 필요',
        connections: byPerson[personId],
        reason: '같은 명단 인물로 매칭된 여러 연결'
      };
    });
  }

  function buildRows(board, connections, duplicates, rosterMap) {
    var duplicateByPerson = {};
    duplicates.forEach(function (group) { if (group.rosterPersonId) { duplicateByPerson[group.rosterPersonId] = group; } });
    var connectionsByPerson = {};
    connections.forEach(function (connection) {
      if (!connection.rosterPersonId) { return; }
      if (!connectionsByPerson[connection.rosterPersonId]) { connectionsByPerson[connection.rosterPersonId] = []; }
      connectionsByPerson[connection.rosterPersonId].push(connection);
    });
    var rows = asArray(board.people).map(function (person) {
      var roster = rosterMap[person.rosterPersonId] || {};
      var personConnections = connectionsByPerson[person.rosterPersonId] || [];
      var duplicate = duplicateByPerson[person.rosterPersonId] || null;
      return {
        key: 'roster:' + person.rosterPersonId,
        kind: 'roster',
        rosterPersonId: person.rosterPersonId,
        sequence: person.sequence,
        name: person.name,
        email: roster.email || '',
        phone: roster.phone || person.phone || '',
        group: normalizeGroupName(person.group || roster.group || ''),
        organization: person.organization || roster.organization || '',
        attendanceState: person.attendanceState,
        confidence: person.confidence,
        confidenceReason: person.confidenceReason,
        lastJoinedAt: person.lastJoinedAt,
        lastLeftAt: person.lastLeftAt,
        joinCount: person.joinCount,
        connections: personConnections.length ? personConnections : (duplicate ? duplicate.connections : []),
        duplicate: !!duplicate,
        isExcluded: !!person.isExcluded,
        identityReviewStatus: person.identityReviewStatus || 'none', duplicateReviewStatus: person.duplicateReviewStatus || 'none',
        identityEvidenceToken: person.identityEvidenceToken, duplicateEvidenceToken: person.duplicateEvidenceToken,
        review: !person.isExcluded && (typeof person.reviewRequired === 'boolean' ? person.reviewRequired : REVIEW_CONFIDENCE.indexOf(person.confidence) >= 0 || !!duplicate)
      };
    });
    var unmatchedConnections = connections.filter(function (connection) { return !connection.rosterPersonId; });
    var unmatchedNames = Object.create(null);
    asArray(board.unmatchedParticipants).forEach(function (item, index) {
      var nameKey = normalizeSearch(item.participantName);
      if (unmatchedNames[nameKey]) {
        var existing = unmatchedNames[nameKey];
        if (item.lastSeenAt > existing.lastJoinedAt) { existing.lastJoinedAt = item.lastSeenAt; }
        existing.joinCount = Math.max(existing.joinCount, item.eventCount || 0);
        return;
      }
      var matching = unmatchedConnections.filter(function (connection) {
        return normalizeSearch(connection.rawName) === normalizeSearch(item.participantName) || normalizeSearch(connection.canonicalName) === normalizeSearch(item.participantName);
      });
      var unmatchedRow = {
        key: 'unmatched:' + normalizeSearch(item.participantName),
        kind: 'unmatched',
        rosterPersonId: null,
        name: item.participantName,
        email: matching[0] ? matching[0].email || '' : '',
        phone: '',
        group: '',
        organization: '',
        attendanceState: item.attendanceState || 'Present',
        confidence: 'Unmatched',
        confidenceReason: '명단에서 안전하게 일치하는 사람을 찾지 못했습니다.',
        lastJoinedAt: item.lastSeenAt,
        lastLeftAt: null,
        joinCount: item.eventCount || 0,
        connections: matching,
        duplicate: matching.length > 1,
        review: true
      };
      unmatchedNames[nameKey] = unmatchedRow;
      rows.push(unmatchedRow);
    });
    return rows;
  }

  /* 보드가 노출하는 그룹 순서를 우선하고, 없으면 명단 등장 순서를 유지한다. */
  function collectGroups(board) {
    var ordered = [];
    var seen = {};
    function push(value) {
      var name = normalizeGroupName(value);
      if (!name) { return; }
      var key = groupKey(name);
      if (seen[key]) { return; }
      seen[key] = true;
      ordered.push(name);
    }
    asArray(board && board.groups).forEach(function (item) {
      push(item && typeof item === 'object' ? (item.name || item.group || item.title) : item);
    });
    asArray(board && board.people).forEach(function (person) { push(person && person.group); });
    state.roster.forEach(function (person) { push(person && person.group); });
    return ordered;
  }

  function renderGroupOptions() {
    if (!el.groupFilter) { return; }
    var known = state.groups.slice();
    /* 명단을 다시 올려 조 구성이 바뀌면 사라진 선택으로 0명 화면에 머물지 않는다. */
    if (state.group && !known.some(function (name) { return sameGroup(name, state.group); })
        && (state.board || state.roster.length)) {
      state.group = '';
      writeStore(STORAGE.group, '');
    }
    el.groupFilter.textContent = '';
    var all = document.createElement('option');
    all.value = '';
    all.textContent = '전체 그룹';
    el.groupFilter.appendChild(all);
    known.forEach(function (name) {
      var option = document.createElement('option');
      option.value = name;
      option.textContent = name;
      el.groupFilter.appendChild(option);
    });
    el.groupFilter.value = state.group || '';
    el.groupFilter.disabled = known.length === 0;
  }

  /* 그룹 선택 시에도 미매칭 참가자는 검토 대상이므로 항상 남긴다. */
  function rowInSelectedGroup(row) {
    if (!state.group) { return true; }
    if (row.kind === 'unmatched') { return true; }
    return sameGroup(row.group, state.group);
  }
  function scopedRows() {
    return state.rows.filter(rowInSelectedGroup);
  }
  function scopedDuplicates(rosterScoped) {
    if (!state.group) { return state.duplicates; }
    var allowed = {};
    rosterScoped.forEach(function (row) { if (row.rosterPersonId) { allowed[row.rosterPersonId] = true; } });
    return state.duplicates.filter(function (group) {
      return !group.rosterPersonId || allowed[group.rosterPersonId];
    });
  }
  function scopedConnectionCount(rosterScoped) {
    var allowed = {};
    rosterScoped.forEach(function (row) { if (row.rosterPersonId) { allowed[row.rosterPersonId] = true; } });
    return state.connections.filter(function (connection) {
      return !connection.rosterPersonId || allowed[connection.rosterPersonId];
    }).length;
  }
  /* 활동 피드도 선택 그룹 인물과 미매칭 이름만 남긴다. */
  function scopedEvents(events, scoped) {
    if (!state.group) { return events; }
    var allowedPeople = {};
    scoped.forEach(function (row) {
      if (row.rosterPersonId) { allowedPeople[row.rosterPersonId] = true; }
    });
    return asArray(events).filter(function (event) {
      var personId = event.matchedRosterPersonId || event.rosterPersonId || null;
      /* 미매칭 활동은 어느 조인지 알 수 없으므로 검토를 위해 계속 표시한다. */
      return !personId || !!allowedPeople[personId];
    });
  }
  /* 그룹을 바꿔 선택 행이 사라지면 상세를 닫는다. */
  function resetInvalidSelection(scoped) {
    if (!state.selectedKey) { return; }
    var stillVisible = scoped.some(function (row) { return row.key === state.selectedKey; });
    if (!stillVisible) { state.selectedKey = null; }
  }

  function applyGroup(value, options) {
    var opts = options || {};
    state.group = normalizeGroupName(value);
    el.participantTableWrap.scrollTop = 0;
    writeStore(STORAGE.group, state.group);
    if (el.groupFilter) { el.groupFilter.value = state.group || ''; }
    if (state.board) { renderBoard(state.board); }
    else { renderGroupOptions(); renderParticipants(); }
    if (opts.notify) {
      toast('ok', '그룹 필터 변경', state.group ? state.group + ' 기준으로 표시합니다.' : '전체 그룹을 표시합니다.');
    }
  }

  function renderBoard(board) {
    if (!board || typeof board !== 'object') { return; }
    if (board.meetingId && normalizeMeetingId(board.meetingId) !== currentMeetingId()) { return; }
    state.board = board;
    var rosterMap = rosterById();
    state.connections = extractConnections(board);
    state.duplicates = deriveDuplicateGroups(board, state.connections, rosterMap);
    state.rows = buildRows(board, state.connections, state.duplicates, rosterMap);
    state.groups = collectGroups(board);
    renderGroupOptions();

    var allScoped = scopedRows();
    var excluded = allScoped.filter(function (row) { return row.isExcluded; });
    var scoped = allScoped.filter(function (row) { return !row.isExcluded; });
    var rosterScoped = scoped.filter(function (row) { return row.kind === 'roster'; });
    var present = rosterScoped.filter(function (row) { return row.attendanceState === 'Present'; });
    var absent = rosterScoped.filter(function (row) { return row.attendanceState !== 'Present'; });
    var review = scoped.filter(function (row) { return row.review; });
    var unmatched = scoped.filter(function (row) { return row.kind === 'unmatched'; });
    var duplicates = scopedDuplicates(rosterScoped);
    var connectionCount = scopedConnectionCount(rosterScoped);
    var total = rosterScoped.length;
    var rate = total ? Math.round((present.length / total) * 100) : 0;

    resetInvalidSelection(allScoped);

    setText(el.summaryTotal, total);
    setText(el.summaryPresent, present.length);
    setText(el.summaryRate, total ? rate + '%' : '–');
    el.rateRing.style.setProperty('--rate', total ? rate : 0);
    setText(el.metricPresent, present.length + '명');
    setText(el.metricAbsent, absent.length + '명');
    setText(el.metricReview, review.length + '항목');
    setText(el.metricDuplicate, duplicates.length + '명');
    renderFreshness();
    renderReadiness();
    setText(el.participantsMeta, '명단 참석 ' + present.length + ' / ' + total + '명 · 미매칭 이름 ' + unmatched.length + '개 · 기기 연결 ' + connectionCount + '건');
    setText(el.scopeNote, (state.group ? state.group + ' · ' : '') + '미매칭은 그룹과 관계없이 표시 · 오늘 제외 ' + excluded.length + '명' + (board.attendanceDate ? ' · ' + board.attendanceDate : ''));
    setText(el.btnExport, (state.group || '전체') + ' 명단 CSV · ' + total + '명');
    setText(el.exportScope, 'CSV: 제외되지 않은 ' + (state.group || '전체') + ' 명단 · 검색·상태 필터와 미매칭 항목은 미포함');
    el.btnExport.disabled = !currentMeetingId() || total === 0;

    setText(el.countAll, scoped.length);
    setText(el.countPresent, scoped.filter(function (row) { return row.attendanceState === 'Present'; }).length);
    setText(el.countAbsent, scoped.filter(function (row) { return row.attendanceState !== 'Present'; }).length);
    setText(el.countReview, review.length);
    setText(el.countUnmatched, unmatched.length);
    setText(el.countExcluded, excluded.length);
    setText(el.rosterSummary, state.group ? state.group + ' ' + total + '명' : '명단 ' + total + '명');

    renderParticipants();
    renderActivity(scopedEvents(board.recentEvents, scoped), duplicates);
    renderDuplicates(duplicates);
  }

  function rowMatchesFilter(row) {
    if (state.filter === 'excluded') { return !!row.isExcluded; }
    if (row.isExcluded) { return false; }
    if (state.filter === 'present') { return row.attendanceState === 'Present'; }
    if (state.filter === 'absent') { return row.attendanceState !== 'Present'; }
    if (state.filter === 'review') { return row.review; }
    if (state.filter === 'unmatched') { return row.kind === 'unmatched'; }
    return true;
  }
  function rowMatchesSearch(row) {
    var query = normalizeSearch(el.participantSearch.value);
    if (!query) { return true; }
    var connectionNames = row.connections.map(function (item) { return item.rawName + ' ' + item.canonicalName + ' ' + (item.email || ''); }).join(' ');
    var haystack = [row.name, row.email, row.organization, row.group, row.phone, digitsOnly(row.phone), connectionNames].join(' ');
    return normalizeSearch(haystack).indexOf(query) >= 0;
  }

  function statusInfo(row) {
    if (row.isExcluded) { return { label: '오늘 제외', className: 'neutral' }; }
    if (row.kind === 'unmatched') { return { label: '미매칭', className: 'unmatched' }; }
    if (row.attendanceState === 'Present') { return { label: '참석 중', className: 'present' }; }
    if (row.attendanceState === 'Left') { return { label: '퇴장', className: 'left' }; }
    return { label: '미참석', className: 'absent' };
  }
  function confidenceInfo(row) {
    if (row.kind === 'roster' && row.attendanceState === 'NotJoined') { return { label: '입장 전', className: 'neutral' }; }
    if (row.kind === 'unmatched') { return { label: '미매칭', className: 'unmatched' }; }
    return { label: CONFIDENCE_LABEL[row.confidence] || row.confidence || '–',
      className: row.confidence === 'Verified' || row.confidence === 'AliasVerified' ? 'verified' : row.confidence === 'Unmatched' ? 'neutral' : 'review' };
  }

  function appendTextCell(tr, text, className) {
    var td = document.createElement('td');
    if (className) { td.className = className; }
    td.textContent = text === null || text === undefined || text === '' ? '–' : String(text);
    tr.appendChild(td);
    return td;
  }
  function appendPillCell(tr, info, type) {
    var td = document.createElement('td');
    var pill = document.createElement('span');
    pill.className = (type || 'status-pill') + ' ' + info.className;
    pill.textContent = info.label;
    td.appendChild(pill);
    tr.appendChild(td);
  }

  function renderParticipants() {
    var scrollTop = el.participantTableWrap.scrollTop;
    var focus = captureParticipantFocus();
    var rows = scopedRows().filter(rowMatchesFilter).filter(rowMatchesSearch);
    el.participantRows.textContent = '';
    el.participantsEmpty.hidden = rows.length > 0;
    setText(el.visibleCount, rows.length + '항목 표시');
    setText(el.emptyTitle, state.filter === 'excluded' ? '오늘 제외한 사람이 없습니다.' : state.filter === 'review' ? '현재 필터에 검토할 항목이 없습니다.' : '표시할 참가자가 없습니다.');
    setText(el.emptyNote, state.filter === 'excluded' ? '오늘 제외한 사람은 여기서 복원할 수 있으며 다음 날 다시 포함됩니다. 원본 명단과 과거 기록은 유지됩니다.' : '검색·그룹·상태 필터를 확인하세요. 모두 제외했다면 제외됨 목록에서 복원할 수 있습니다.');
    if (el.btnResetFilters) { el.btnResetFilters.hidden = !state.group && !el.participantSearch.value && state.filter === 'all'; }

    rows.forEach(function (row) {
      var tr = document.createElement('tr');
      tr.className = 'participant-row' + (row.key === state.selectedKey ? ' is-selected' : '') + (row.kind === 'unmatched' ? ' is-unmatched' : '');
      tr.dataset.key = row.key;
      appendPillCell(tr, statusInfo(row));
      var nameCell = document.createElement('td');
      var name = document.createElement('button');
      name.type = 'button';
      name.className = 'participant-name row-toggle';
      name.textContent = (row.key === state.selectedKey ? '− ' : '+ ') + row.name;
      name.dataset.focusKey = 'row:' + row.key;
      name.setAttribute('aria-expanded', row.key === state.selectedKey ? 'true' : 'false');
      name.setAttribute('aria-label', row.name + ' 상세 ' + (row.key === state.selectedKey ? '접기' : '펼치기'));
      name.addEventListener('click', function (event) { event.stopPropagation(); toggleParticipant(row.key); });
      var secondary = document.createElement('span');
      secondary.className = 'participant-secondary';
      secondary.textContent = row.email || (row.kind === 'unmatched' ? 'Zoom 표시 이름' : '이메일 없음');
      nameCell.appendChild(name);
      nameCell.appendChild(secondary);
      tr.appendChild(nameCell);
      var orgCell = document.createElement('td');
      var orgPrimary = document.createElement('span');
      orgPrimary.className = 'participant-name';
      orgPrimary.textContent = row.organization || '–';
      orgCell.appendChild(orgPrimary);
      if (row.group) {
        var groupTag = document.createElement('span');
        groupTag.className = 'group-tag';
        groupTag.textContent = row.group;
        orgCell.appendChild(groupTag);
      }
      tr.appendChild(orgCell);
      appendPillCell(tr, confidenceInfo(row), 'match-pill');
      appendTextCell(tr, formatTime(row.lastJoinedAt));
      appendTextCell(tr, row.attendanceState === 'Left' ? '퇴장 ' + formatTime(row.lastLeftAt) : (row.lastJoinedAt ? '입장 ' + formatTime(row.lastJoinedAt) : '–'));
      var connectionCell = document.createElement('td');
      var connectionPill = document.createElement('span');
      connectionPill.className = 'connection-pill' + (row.duplicate ? ' duplicate' : '');
      connectionPill.textContent = row.connections.length ? row.connections.length + '건' : '–';
      connectionCell.appendChild(connectionPill);
      tr.appendChild(connectionCell);
      tr.addEventListener('click', function (event) {
        if (event.target && event.target.closest && event.target.closest('button, input, select, a')) { return; }
        toggleParticipant(row.key);
      });
      el.participantRows.appendChild(tr);
      if (row.key === state.selectedKey) { el.participantRows.appendChild(buildDetailRow(row)); }
    });
    restoreParticipantFocus(focus);
    window.requestAnimationFrame(function () { el.participantTableWrap.scrollTop = scrollTop; });
  }

  function toggleParticipant(key) {
    state.selectedKey = state.selectedKey === key ? null : key;
    renderParticipants();
  }

  /* Zoom 이름 변경 버튼 노출 조건. 서버가 최종 판단하므로 클라이언트는 안전한 최소 조건만 본다.
     서버는 명단 이름으로 변경하므로 버튼 문구도 명단 이름을 그대로 쓴다. */
  function renameCandidate(row) {
    if (!row || row.kind !== 'roster' || row.isExcluded) { return null; }
    if (!state.zoomApp || state.zoomApp.transport !== 'relay') { return null; }
    if (row.duplicate || row.confidence === 'Possible' || row.confidence === 'Unmatched') { return null; }
    if (row.connections.length !== 1) { return null; }
    var app = state.zoomApp || {};
    if (!app.connected || app.sessionActive === false) { return null; }
    var connection = row.connections[0];
    if (!connection) { return null; }
    var presenceKey = connection.presenceKey || '';
    if (presenceKey.indexOf('zoom-app:') !== 0) { return null; }
    var target = normalizeGroupName(row.name);
    var canonical = normalizeGroupName(connection.canonicalName || connection.displayName || '');
    var rawName = normalizeGroupName(connection.rawName);
    if (!target || !rawName || !canonical) { return null; }
    if (target === rawName || canonical === rawName) { return null; }
    return { presenceKey: presenceKey, rawName: rawName, target: target };
  }

  function renameZoomParticipant(candidate) {
    var meetingId = requireMeetingId();
    if (!meetingId || !candidate) { return; }
    var context = beginMeetingRequest('Zoom 이름 변경을 요청하는 중…');
    return request('/api/zoom-app/participants/rename', {
      method: 'POST',
      json: { meetingId: meetingId, presenceKey: candidate.presenceKey }
    }).then(function () {
      if (!finishMeetingRequest(context)) { return null; }
      toast('ok', 'Zoom 이름 변경 요청됨', candidate.rawName + ' → ' + candidate.target);
      logSession('ok', 'Zoom 이름 변경', candidate.rawName + ' → ' + candidate.target);
      /* 변경 결과가 참가자 목록에 반영되도록 동기화를 한 번 요청한다. */
      return Promise.resolve(syncNow({ silent: true })).then(function () {
        if (isCurrentMeetingRequest(context)) { return refreshBoard({ silent: true }); }
      });
    }, function (error) {
      if (!finishMeetingRequest(context)) { return null; }
      toast('bad', 'Zoom 이름 변경 실패', error.message);
      logSession('bad', 'Zoom 이름 변경 실패', error.message);
    });
  }

  function captureParticipantFocus() {
    var active = document.activeElement;
    if (!active || !el.participantRows.contains(active)) { return null; }
    return { key: active.dataset && active.dataset.focusKey, start: active.selectionStart, end: active.selectionEnd };
  }
  function restoreParticipantFocus(saved) {
    if (!saved) { return; }
    var controls = Array.prototype.slice.call(el.participantRows.querySelectorAll('[data-focus-key]'));
    var target = controls.filter(function (node) { return node.dataset.focusKey === saved.key; })[0]
      || controls.filter(function (node) { return node.classList.contains('row-toggle'); })[0] || el.participantSearch;
    target.focus({ preventScroll: true });
    if (saved.start !== null && saved.start !== undefined && target.setSelectionRange && target.type === 'search') { target.setSelectionRange(saved.start, saved.end); }
  }
  function meetingScope() { return { meetingId: currentMeetingId(), generation: state.meetingGeneration, attendanceDate: state.board && state.board.attendanceDate }; }
  function scopeIsCurrent(scope) { return scope.meetingId === currentMeetingId() && scope.generation === state.meetingGeneration; }
  function personLabel(person) {
    var roster = rosterById()[person.rosterPersonId] || {};
    return [person.name, person.group || roster.group, person.email || roster.email].filter(Boolean).join(' · ');
  }
  function confirmAction(message, action) {
    if (el.actionConfirm.open) { return Promise.resolve(null); }
    return new Promise(function (resolve, reject) {
      var previousFocus = document.activeElement;
      state.confirmationCallback = function (accepted) {
        state.confirmationCallback = null;
        var focus = previousFocus && previousFocus.isConnected !== false ? previousFocus : el.participantSearch;
        if (focus && typeof focus.focus === 'function') { focus.focus({ preventScroll: true }); }
        try { resolve(accepted ? action() : null); }
        catch (error) { reject(error); }
      };
      setText(el.actionConfirmMessage, message);
      el.actionConfirm.returnValue = '';
      el.actionConfirm.showModal();
      if (el.actionConfirm.open) { el.btnConfirmCancel.focus(); }
    });
  }
  function mutateMeeting(scope, path, payload, confirmation, title) {
    if (!scopeIsCurrent(scope) || !scope.meetingId) { return Promise.resolve(null); }
    return confirmAction('회의 ' + scope.meetingId + '\n' + confirmation, function () {
    if (!scopeIsCurrent(scope)) { return Promise.resolve(null); }
    state.boardEpoch += 1;
    var context = beginMeetingRequest('이번 회의의 변경을 저장하는 중…');
    return request('/api/meetings/' + encodeURIComponent(scope.meetingId) + path, { method: 'PUT', json: payload }).then(function (board) {
      if (!finishMeetingRequest(context)) { return null; }
      state.boardEpoch += 1;
      renderBoard(board);
      toast('ok', title, '회의 ' + scope.meetingId + '에 저장했습니다.');
      return board;
    }, function (error) {
      if (!finishMeetingRequest(context)) { return null; }
      if (error.status === 409) {
        showAlert('warn', payload.attendanceDate ? '날짜 또는 참가자 정보가 변경되었습니다' : '참가자 정보가 변경되었습니다', '이전 화면의 판단은 저장하지 않았습니다. 최신 정보를 읽고 다시 검토하세요.', '최신 현황 다시 읽기', function () {
          if (scopeIsCurrent(scope)) { refreshBoard({ notify: true }); }
        });
        toast('warn', '변경되지 않았습니다', '최신 현황을 읽은 뒤 다시 검토하세요.');
      } else {
        showAlert('bad', '변경을 저장하지 못했습니다', '기존 판단과 명단을 유지했습니다. 연결을 확인한 뒤 다시 시도하세요.', '현황 다시 읽기', function () { if (scopeIsCurrent(scope)) { refreshBoard({ notify: true }); } });
        if (el.alertDetails) { el.alertDetails.hidden = false; setText(el.alertTechnical, error.message); }
      }
      return null;
    });
    });
  }
  function setPersonExcluded(row, excluded, scope) {
    if (!row || !row.rosterPersonId) { return Promise.resolve(null); }
    scope = scope || meetingScope();
    return mutateMeeting(scope, '/people/' + encodeURIComponent(row.rosterPersonId) + '/exclusion', { excluded: excluded, attendanceDate: scope.attendanceDate },
      (scope.attendanceDate || '오늘') + ' · ' + personLabel(row) + '\n' + (excluded ? '오늘 명단에서 제외할까요?\n출석률·검토·CSV에서 빠지며 제외됨 목록에서 복원할 수 있습니다.' : '오늘 명단으로 복원할까요?')
      + (excluded && row.attendanceState === 'Present' ? '\n현재 연결 중인 참가자입니다. Zoom 연결은 유지되고 오늘 집계에서만 제외됩니다.' : '')
      + '\n원본 명단과 과거 기록은 유지되고 다음 날 다시 포함됩니다.', excluded ? '오늘 명단에서 제외했습니다' : '오늘 명단에 복원했습니다');
  }
  function setPersonReview(row, kind, status, scope) {
    var token = kind === 'identity' ? row.identityEvidenceToken : row.duplicateEvidenceToken;
    if (!token) { return Promise.resolve(null); }
    var label = kind === 'identity' ? '신원' : '중복 기기';
    return mutateMeeting(scope || meetingScope(), '/people/' + encodeURIComponent(row.rosterPersonId) + '/review',
      { kind: kind, status: status, expectedEvidenceToken: token }, personLabel(row) + '\n' + label + ' 검토를 '
      + (status === 'confirmed' ? '이번 회의에서 확인' : status === 'deferred' ? '보류' : '미확인으로 되돌림') + ' 처리할까요?\n실제 매칭 근거와 입퇴장 상태는 바뀌지 않습니다.', label + ' 검토를 저장했습니다');
  }
  function matchConnection(connection, personId, scope) {
    if (!connection.source || !connection.presenceKey || !connection.evidenceToken) { return Promise.resolve(null); }
    var person = asArray(state.board && state.board.people).filter(function (item) { return item.rosterPersonId === personId; })[0];
    if (personId !== null && (!person || person.isExcluded)) { return Promise.resolve(null); }
    return mutateMeeting(scope || meetingScope(), '/connections/match', { source: connection.source, presenceKey: connection.presenceKey,
      rosterPersonId: personId, expectedEvidenceToken: connection.evidenceToken },
      connection.rawName + ' · ' + sourceLabel(connection.source) + ' · 연결 ' + connection.presenceKey + '\n'
      + (personId === null ? '운영자 연결을 취소하고 자동 매칭으로 돌아갈까요?' : personLabel(person) + '\n이 인물과 연결할까요?')
      + '\n이번 회의의 이 연결에만 적용됩니다. 영구 별명은 저장하지 않습니다.', personId === null ? '명단 연결을 취소했습니다' : '이번 회의의 명단 인물과 연결했습니다');
  }
  function reviewConnection(connection, status, scope) {
    if (!connection.source || !connection.presenceKey || !connection.evidenceToken) { return Promise.resolve(null); }
    return mutateMeeting(scope || meetingScope(), '/connections/review', { source: connection.source, presenceKey: connection.presenceKey,
      status: status, expectedEvidenceToken: connection.evidenceToken }, connection.rawName + ' · 연결 ' + connection.presenceKey
      + '\n이 연결을 ' + (status === 'deferred' ? '보류할까요? 보류 항목은 검토 목록에 남습니다.' : '미확인으로 되돌릴까요?'), '연결 검토를 저장했습니다');
  }
  function actionButton(parent, label, key, handler, disabled) {
    var button = document.createElement('button'); button.type = 'button'; button.className = 'btn secondary compact';
    button.textContent = label; button.dataset.focusKey = key; button.disabled = !!disabled;
    button.addEventListener('click', function (event) { event.stopPropagation(); handler(); }); parent.appendChild(button); return button;
  }
  function reviewExplanation(row) {
    if (row.isExcluded) { return '오늘 제외 · 원본 명단과 과거 기록 유지, 다음 날 다시 포함'; }
    if (row.attendanceState === 'NotJoined') { return '아직 입장 기록이 없습니다. 참가자가 입장하면 명단과 연결합니다.'; }
    var reason = row.confidence === 'NameOnly' ? '이름만 일치합니다. 이메일로는 확인되지 않았습니다.'
      : row.confidence === 'Possible' ? '비슷한 이름의 명단 인물을 찾았습니다. 같은 사람인지 확인해 주세요.'
      : row.confidence === 'AliasVerified' ? '등록된 별명으로 명단 인물과 연결했습니다.'
      : row.confidence === 'Verified' ? (row.connections.some(function (connection) { return connection.manualMatch; })
        ? '운영자가 이번 회의에서 명단 인물과 연결했습니다.' : '이메일로 명단 인물을 확인했습니다.')
      : '명단에서 같은 사람을 확인하지 못했습니다. 연결할 명단 인물을 선택해 주세요.';
    if (row.duplicate) { reason += ' 같은 명단 인물에 여러 기기가 연결되어 있습니다. 기기 연결도 확인해 주세요.'; }
    return reason;
  }
  function appendPersonActions(parent, row, scope) {
    var section = document.createElement('section'); section.className = 'review-actions';
    var heading = document.createElement('h3'); heading.textContent = '이번 회의 검토'; section.appendChild(heading);
    var reason = document.createElement('p'); reason.textContent = reviewExplanation(row);
    section.appendChild(reason);
    if (!row.isExcluded) {
      ['identity', 'duplicate'].forEach(function (kind) {
        var status = row[kind + 'ReviewStatus'];
        if (!status || status === 'none') { return; }
        var line = document.createElement('div'); line.className = 'review-action-line';
        var label = document.createElement('strong'); label.textContent = (kind === 'identity' ? '신원' : '중복 기기') + ' · '
          + (status === 'confirmed' ? '확인됨' : status === 'deferred' ? '보류' : '검토 필요'); line.appendChild(label);
        var disabled = !row[kind + 'EvidenceToken'];
        if (status !== 'confirmed') { actionButton(line, '이번 회의에서 확인', row.key + ':' + kind + ':confirm', function () { setPersonReview(row, kind, 'confirmed', scope); }, disabled); }
        if (status === 'pending') { actionButton(line, '보류', row.key + ':' + kind + ':defer', function () { setPersonReview(row, kind, 'deferred', scope); }, disabled); }
        if (status !== 'pending') { actionButton(line, '판단 취소', row.key + ':' + kind + ':undo', function () { setPersonReview(row, kind, 'pending', scope); }, disabled); }
        section.appendChild(line);
      });
    }
    {
      actionButton(section, row.isExcluded ? '오늘 명단에 복원' : '오늘 명단에서 제외 (불참)', row.key + ':exclusion', function () { setPersonExcluded(row, !row.isExcluded, scope); });
      var note = document.createElement('p'); note.className = 'muted'; note.textContent = '오늘 이 회의에만 적용 · 원본 명단과 과거 기록 유지 · 다음 날 다시 포함'; section.appendChild(note);
    }
    parent.appendChild(section);
  }
  function appendConnectionActions(card, connection, row, scope, index) {
    var key = JSON.stringify([connection.source, connection.presenceKey]);
    var draft = state.connectionDrafts[key] || (state.connectionDrafts[key] = { query: '', personId: '', open: false });
    var label = document.createElement('p'); label.className = 'connection-identity';
    label.textContent = '연결 ' + (index + 1) + ' · ' + sourceLabel(connection.source) + ' · ' + (connection.presenceKey || 'ID 없음')
      + (connection.manualMatch ? ' · 이번 회의에서 운영자가 연결함' : '') + (connection.reviewStatus === 'deferred' ? ' · 보류' : ''); card.appendChild(label);
    if (row.isExcluded || !connection.source || !connection.presenceKey || !connection.evidenceToken) { return; }
    var details = document.createElement('details'); details.className = 'connection-actions'; details.open = draft.open;
    var summary = document.createElement('summary'); summary.textContent = '명단 연결·검토'; summary.dataset.focusKey = key + ':details'; details.appendChild(summary);
    details.addEventListener('toggle', function () { draft.open = details.open; });
    var searchLabel = document.createElement('label'); searchLabel.textContent = '연결할 명단 인물 검색';
    var search = document.createElement('input'); search.type = 'search'; search.value = draft.query; search.placeholder = '이름·이메일·그룹'; search.dataset.focusKey = key + ':search'; searchLabel.appendChild(search); details.appendChild(searchLabel);
    var selectLabel = document.createElement('label'); selectLabel.textContent = '이번 회의의 명단 인물';
    var select = document.createElement('select'); select.dataset.focusKey = key + ':person'; selectLabel.appendChild(select); details.appendChild(selectLabel);
    function choices() {
      var rosterPeople = rosterById();
      select.textContent = '';
      var empty = document.createElement('option'); empty.value = ''; empty.textContent = '명단 인물을 선택하세요'; select.appendChild(empty);
      asArray(state.board && state.board.people).filter(function (person) {
        var roster = rosterPeople[person.rosterPersonId] || {};
        return !person.isExcluded && normalizeSearch([person.name, person.group, person.organization, roster.email].join(' ')).indexOf(normalizeSearch(draft.query)) >= 0;
      }).forEach(function (person) {
        var roster = rosterPeople[person.rosterPersonId] || {};
        var option = document.createElement('option'); option.value = person.rosterPersonId;
        option.textContent = person.name + ' · ' + (person.group || '그룹 없음') + ' · ' + (roster.email || '번호 ' + (person.sequence || person.rosterPersonId)); select.appendChild(option);
      });
      select.value = draft.personId;
      if (select.value !== draft.personId) { draft.personId = ''; select.value = ''; }
    }
    choices();
    var apply = actionButton(details, '이 명단 인물과 연결', key + ':match', function () { matchConnection(connection, select.value, scope); }, !draft.personId);
    search.addEventListener('input', function () { draft.query = search.value; draft.personId = ''; choices(); apply.disabled = true; });
    select.addEventListener('change', function () { draft.personId = select.value; apply.disabled = !select.value; });
    if (connection.manualMatch) { actionButton(details, '운영자 연결 취소', key + ':undo-match', function () { matchConnection(connection, null, scope); }); }
    if (row.kind === 'unmatched') {
      actionButton(details, connection.reviewStatus === 'deferred' ? '보류 취소' : '이 연결 보류', key + ':defer', function () { reviewConnection(connection, connection.reviewStatus === 'deferred' ? 'pending' : 'deferred', scope); });
    }
    var note = document.createElement('p'); note.textContent = '이 회의의 이 연결에만 적용합니다. 다음 회의의 별명으로 저장하지 않습니다.'; details.appendChild(note); card.appendChild(details);
  }
  function buildDetailRow(row) {
    var tr = document.createElement('tr');
    tr.className = 'detail-row';
    var td = document.createElement('td');
    td.colSpan = 7;
    var detail = document.createElement('div');
    detail.className = 'participant-detail';
    var scope = meetingScope();
    if (row.kind === 'roster') { appendPersonActions(detail, row, scope); }

    var connectionBlock = document.createElement('section');
    connectionBlock.className = 'detail-block';
    var connectionTitle = document.createElement('h3');
    connectionTitle.textContent = '현재 Zoom 연결 ' + row.connections.length + '건';
    connectionBlock.appendChild(connectionTitle);
    var cards = document.createElement('div');
    cards.className = 'connection-cards';
    if (!row.connections.length) {
      var empty = document.createElement('div');
      empty.className = 'connection-card';
      var emptyStrong = document.createElement('strong');
      emptyStrong.textContent = '활성 연결 없음';
      var emptySpan = document.createElement('span');
      emptySpan.textContent = '명단 기록과 이전 입퇴장 이력만 있습니다.';
      empty.appendChild(emptyStrong);
      empty.appendChild(emptySpan);
      cards.appendChild(empty);
    }
    row.connections.forEach(function (connection, index) {
      var card = document.createElement('div');
      card.className = 'connection-card';
      var strong = document.createElement('strong');
      strong.textContent = connection.rawName || connection.canonicalName || 'Zoom 연결';
      var span = document.createElement('span');
      var normalized = connection.canonicalName && connection.rawName && connection.canonicalName !== connection.rawName
        ? '자동 정리: ' + connection.canonicalName
        : (connection.email || sourceLabel(connection.source));
      span.textContent = normalized;
      var time = document.createElement('time');
      time.textContent = formatTime(connection.firstSeenAt) + ' ~ ' + formatTime(connection.lastSeenAt);
      card.appendChild(strong);
      card.appendChild(span);
      card.appendChild(time);
      appendConnectionActions(card, connection, row, scope, index);
      cards.appendChild(card);
    });
    connectionBlock.appendChild(cards);

    var candidate = renameCandidate(row);
    if (candidate) {
      var renameBox = document.createElement('div');
      renameBox.className = 'rename-box';
      var renameButton = document.createElement('button');
      renameButton.type = 'button';
      renameButton.className = 'btn secondary compact';
      renameButton.textContent = 'Zoom 이름을 ' + candidate.target + '으로 변경';
      renameButton.addEventListener('click', function (event) {
        event.stopPropagation();
        renameZoomParticipant(candidate);
      });
      var renameNote = document.createElement('p');
      renameNote.className = 'rename-note';
      renameNote.textContent = '호스트 또는 공동호스트 권한과 이름 변경을 지원하는 Zoom 클라이언트에서만 적용됩니다.';
      renameBox.appendChild(renameButton);
      renameBox.appendChild(renameNote);
      connectionBlock.appendChild(renameBox);
    }

    var matchBlock = document.createElement('section');
    matchBlock.className = 'detail-block';
    var matchTitle = document.createElement('h3');
    matchTitle.textContent = '명단 및 활동 상세';
    matchBlock.appendChild(matchTitle);
    var dl = document.createElement('dl');
    dl.className = 'detail-list';
    [
      ['명단 이름', row.name],
      ['그룹', row.group || '없음'],
      ['이메일', row.email || '없음'],
      ['전화번호', row.phone || '없음'],
      ['매칭 상태', confidenceInfo(row).label],
      ['최근 입장', formatDateTime(row.lastJoinedAt)],
      ['마지막 퇴장', formatDateTime(row.lastLeftAt)],
      ['입장 기록', row.joinCount + '회']
    ].forEach(function (pair) {
      var dt = document.createElement('dt'); dt.textContent = pair[0];
      var dd = document.createElement('dd'); dd.textContent = pair[1];
      dl.appendChild(dt); dl.appendChild(dd);
    });
    matchBlock.appendChild(dl);
    detail.appendChild(connectionBlock);
    detail.appendChild(matchBlock);
    td.appendChild(detail);
    tr.appendChild(td);
    return tr;
  }

  function safePayload(raw) {
    if (!raw || typeof raw !== 'string') { return {}; }
    try { return JSON.parse(raw); }
    catch (_) { return {}; }
  }

  function renderActivity(events, duplicates) {
    var panel = el.activityFeed.closest('.rail-panel');
    if (panel) { panel.classList.toggle('is-empty', !asArray(events).length && !duplicates.length); }
    var items = asArray(events).map(function (event) {
      var payload = safePayload(event.rawPayload);
      var oldName = event.previousParticipantName || event.previousName || payload.previousName || payload.oldName || payload.previousDisplayName || '';
      var newName = event.newParticipantName || event.newName || payload.newName || payload.displayName || event.participantName || '';
      var type = event.eventType || 'Joined';
      return {
        key: event.id || (type + ':' + event.occurredAt + ':' + event.participantName),
        type: type,
        at: event.occurredAt,
        title: type === 'NameChanged' ? (oldName ? oldName + ' → ' + newName : newName) : event.participantName,
        detail: EVENT_LABEL[type] || type,
        meta: sourceLabel(event.source),
        sortAt: new Date(event.occurredAt || 0).getTime()
      };
    });
    duplicates.forEach(function (group) {
      var latest = group.connections.reduce(function (value, item) {
        var at = new Date(item.lastSeenAt || item.firstSeenAt || 0).getTime();
        return at > value ? at : value;
      }, 0);
      items.push({ key: 'duplicate:' + group.key, type: 'Duplicate', at: latest ? new Date(latest) : null, title: group.personName, detail: '중복 연결 감지', meta: group.connections.length + '건', sortAt: latest });
    });
    items.sort(function (a, b) { return b.sortAt - a.sortAt; });
    items = items.slice(0, 30);
    el.activityFeed.textContent = '';
    el.activityEmpty.hidden = items.length > 0;
    el.activityFeed.hidden = items.length === 0;
    setText(el.activityCount, '최근 ' + items.length + '건');
    items.forEach(function (item) {
      var li = document.createElement('li');
      li.className = 'activity-item';
      var time = document.createElement('time');
      time.className = 'activity-time';
      time.textContent = formatTime(item.at);
      var marker = document.createElement('span');
      var markerClass = item.type === 'Left' ? 'leave' : item.type === 'NameChanged' ? 'rename' : item.type === 'Duplicate' ? 'duplicate' : 'join';
      marker.className = 'activity-marker ' + markerClass;
      marker.setAttribute('aria-hidden', 'true');
      var main = document.createElement('div');
      main.className = 'activity-main';
      var title = document.createElement('strong');
      title.textContent = item.title || '알 수 없는 참가자';
      var detail = document.createElement('span');
      detail.textContent = item.detail;
      main.appendChild(title);
      main.appendChild(detail);
      var meta = document.createElement('span');
      meta.className = 'activity-meta';
      meta.textContent = item.meta;
      li.appendChild(time); li.appendChild(marker); li.appendChild(main); li.appendChild(meta);
      el.activityFeed.appendChild(li);
    });
  }

  function renderDuplicates(groups) {
    var panel = el.duplicateList.closest('.rail-panel');
    if (panel) { panel.classList.toggle('is-empty', groups.length === 0); }
    el.duplicateList.textContent = '';
    el.duplicateEmpty.hidden = groups.length > 0;
    el.duplicateList.hidden = groups.length === 0;
    setText(el.duplicateCount, groups.length);
    groups.forEach(function (group) {
      var card = document.createElement('article');
      card.className = 'duplicate-card';
      var header = document.createElement('header');
      var name = document.createElement('button');
      name.type = 'button'; name.className = 'duplicate-person';
      name.disabled = !group.rosterPersonId;
      name.setAttribute('aria-label', group.personName + ' 참가자 상세 보기');
      name.textContent = group.personName;
      var count = document.createElement('span');
      count.textContent = group.connections.length + '건 연결';
      header.appendChild(name); header.appendChild(count);
      var reason = document.createElement('p');
      reason.textContent = group.reason || '출석은 한 명으로 계산하고 연결은 별도로 표시합니다.';
      var list = document.createElement('div');
      list.className = 'duplicate-connections';
      group.connections.forEach(function (connection) {
        var row = document.createElement('div');
        row.className = 'duplicate-connection';
        var label = document.createElement('strong');
        label.textContent = connection.rawName || connection.canonicalName || 'Zoom 연결';
        var time = document.createElement('time');
        time.textContent = formatTime(connection.firstSeenAt) + ' ~ 현재';
        row.appendChild(label); row.appendChild(time); list.appendChild(row);
      });
      card.appendChild(header); card.appendChild(reason); card.appendChild(list);
      name.addEventListener('click', function () {
        if (!group.rosterPersonId) { return; }
        state.selectedKey = 'roster:' + group.rosterPersonId; state.filter = 'all'; el.participantSearch.value = '';
        syncFilterButtons(); renderParticipants();
        window.requestAnimationFrame(function () {
          var target = Array.prototype.slice.call(el.participantRows.querySelectorAll('[data-focus-key]')).filter(function (node) { return node.dataset.focusKey === 'row:' + state.selectedKey; })[0];
          if (target) { target.focus({ preventScroll: true }); target.scrollIntoView({ block: 'nearest' }); }
        });
      });
      el.duplicateList.appendChild(card);
    });
  }

  function syncFilterButtons() {
    el.filterButtons.forEach(function (button) {
      button.setAttribute('aria-pressed', button.dataset.filter === state.filter ? 'true' : 'false');
    });
  }

  function exportCsv() {
    var meetingId = requireMeetingId();
    if (!meetingId) { return; }
    var busy = beginBusy('CSV를 준비하는 중…');
    var path = '/api/meetings/' + encodeURIComponent(meetingId) + '/export';
    var group = state.group;
    if (group) { path += '?group=' + encodeURIComponent(group); }
    request(path, { raw: true }).then(function (response) {
      return response.blob();
    }).then(function (blob) {
      endBusy(busy);
      var url = URL.createObjectURL(blob);
      var link = document.createElement('a');
      link.href = url;
      link.download = group
        ? meetingId + '-' + group.replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-+|-+$/g, '') + '-attendance.csv'
        : meetingId + '-attendance.csv';
      document.body.appendChild(link);
      link.click(); link.remove();
      setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
      toast('ok', 'CSV 내보내기 시작', link.download);
    }, function (error) { endBusy(busy); toast('bad', 'CSV 내보내기 실패', error.message); });
  }

  function parseNames(raw) {
    var seen = Object.create(null);
    var names = [];
    String(raw || '').split(/\r?\n/).forEach(function (line) {
      var name = line.replace(/\s+/g, ' ').trim();
      var key = normalizeSearch(name);
      if (!name || seen[key]) { return; }
      seen[key] = true; names.push(name);
    });
    return names;
  }
  function updateParsedCount() {
    var names = parseNames(el.snapshotNames.value);
    var lines = String(el.snapshotNames.value || '').split(/\r?\n/).filter(function (line) { return line.trim(); }).length;
    setText(el.snapshotParsed, '입력 ' + lines + '줄 · 서로 다른 이름 ' + names.length + '개 · 중복 ' + (lines - names.length) + '줄' + (names.length > MAX_SNAPSHOT_NAMES ? ' · 최대 인원 초과' : ''));
    return names;
  }
  function submitSnapshot() {
    var meetingId = requireMeetingId();
    if (!meetingId) { return; }
    var names = updateParsedCount();
    if (names.length > MAX_SNAPSHOT_NAMES) { toast('bad', '목록이 너무 깁니다', '최대 ' + MAX_SNAPSHOT_NAMES + '명까지 적용할 수 있습니다.'); return; }
    if (!names.length && !el.chkEmptyOk.checked) { toast('warn', '빈 목록을 적용할 수 없습니다', '전원 퇴장이라면 빈 목록 허용을 체크하세요.'); return; }
    var scope = meetingScope();
    return confirmAction('회의 ' + meetingId + '\n서로 다른 이름 ' + names.length + '개의 현재 전체 목록을 적용할까요?\n이전 직접 입력 목록에만 있던 참가자는 퇴장으로 기록됩니다. 같은 이름의 여러 사람·기기를 구분할 수 없으므로 전체 목록을 확인하세요.', function () {
    if (!scopeIsCurrent(scope)) { return Promise.resolve(null); }
    state.boardEpoch += 1;
    var context = beginMeetingRequest('전체 참가자 목록을 적용하는 중…');
    return request('/api/meetings/' + encodeURIComponent(meetingId) + '/participant-snapshot', {
      method: 'POST',
      json: { participantNames: names, source: 'web-dashboard', capturedAt: new Date().toISOString() }
    }).then(function (result) {
      if (!finishMeetingRequest(context)) { return null; }
      state.boardEpoch += 1;
      if (result && result.board) { renderBoard(result.board); }
      markSync(true);
      var summary = '입장 ' + asArray(result && result.joinedNames).length + ' · 퇴장 ' + asArray(result && result.leftNames).length + ' · 처리 제외 ' + asArray(result && result.ignoredNames).length;
      setText(el.snapshotResultSummary, '회의 ' + meetingId + ' · ' + summary + ' — 변경 내역');
      el.snapshotResultDetails.textContent = '';
      [['입장', 'joinedNames'], ['퇴장', 'leftNames'], ['처리 제외', 'ignoredNames']].forEach(function (pair) {
        var item = document.createElement('p'); item.textContent = pair[0] + ': ' + (asArray(result && result[pair[1]]).join(', ') || '없음'); el.snapshotResultDetails.appendChild(item);
      });
      el.snapshotResult.hidden = false; el.snapshotResult.open = true;
      toast('ok', '전체 목록 적용 완료', summary);
    }, function (error) {
      if (!finishMeetingRequest(context)) { return null; }
      toast('bad', '전체 목록 적용 실패', error.message);
    });
    });
  }

  function stopAutoRefresh() {
    if (state.timerId !== null) { clearInterval(state.timerId); state.timerId = null; }
    state.nextSyncAt = null;
  }
  function scheduleNextSync() {
    if (!el.chkAutoRefresh.checked || effectiveMode() === 'manual') { state.nextSyncAt = null; updateClock(); return; }
    state.nextSyncAt = new Date(Date.now() + intervalSeconds() * 1000);
    updateClock();
  }
  function applyAutoRefresh(checked) {
    el.chkAutoRefresh.checked = checked;
    el.settingsAutoRefresh.checked = checked;
    stopAutoRefresh();
    writeStore(STORAGE.autoRefresh, checked ? '1' : '0');
    writeStore(STORAGE.interval, intervalSeconds());
    setText(el.autoStatus, checked ? '활성' : '꺼짐');
    renderModeControls();
    if (checked) {
      state.timerId = window.setInterval(function () {
        if (document.hidden || state.pending !== 0) { return; }
        var mode = effectiveMode();
        if (mode === 'manual') { return; }
        if (mode === 'zoomApp') { requestZoomAppSync({ silent: true }); return; }
        if (currentMeetingId()) { syncZoomParticipants({ silent: true }); }
      }, intervalSeconds() * 1000);
      scheduleNextSync();
    } else { updateClock(); }
  }
  function updateClock() {
    renderPairingCode();
    renderFreshness();
    if (!el.chkAutoRefresh.checked || !state.nextSyncAt) { setText(el.nextSyncTime, '–'); return; }
    var seconds = Math.max(0, Math.ceil((state.nextSyncAt.getTime() - Date.now()) / 1000));
    setText(el.nextSyncTime, '00:' + pad(seconds));
  }

  function openSettings() {
    el.settingsAutoRefresh.checked = el.chkAutoRefresh.checked;
    if (typeof el.settingsDialog.showModal === 'function') { el.settingsDialog.showModal(); }
    else { el.settingsDialog.setAttribute('open', ''); }
  }

  /* ---------- 첫 실행 사용법 ---------- */
  function closeModal(dialog) {
    if (!dialog) { return; }
    if (typeof dialog.close === 'function' && dialog.open) { dialog.close(); }
    else { dialog.removeAttribute('open'); }
  }

  function shouldAutoOpenTutorial() {
    if (readStore(STORAGE.tutorialSeen, '') !== '') { return false; }
    /* v0.6 이하를 이미 사용한 브라우저는 기존 설정 키가 하나라도 남아 있다.
       기존 사용자에게 갑자기 튜토리얼을 띄우지 않고 다시 보기 버튼만 제공한다. */
    var legacyKeys = [STORAGE.meetingId, STORAGE.autoRefresh, STORAGE.interval, STORAGE.connectionMode, STORAGE.group];
    var hasExistingState = legacyKeys.some(function (key) { return readStore(key, '') !== ''; });
    if (hasExistingState) { writeStore(STORAGE.tutorialSeen, 'existing-user'); }
    return !hasExistingState;
  }

  function renderTutorial() {
    var total = el.tutorialSteps.length;
    state.tutorialStep = Math.max(0, Math.min(total - 1, state.tutorialStep));
    el.tutorialSteps.forEach(function (step, index) { step.hidden = index !== state.tutorialStep; });
    el.tutorialProgressButtons.forEach(function (button, index) {
      button.setAttribute('aria-selected', index === state.tutorialStep ? 'true' : 'false');
      button.classList.toggle('is-complete', index < state.tutorialStep);
      button.tabIndex = index === state.tutorialStep ? 0 : -1;
    });
    setText(el.tutorialProgressText, (state.tutorialStep + 1) + ' / ' + total);
    el.btnTutorialBack.disabled = state.tutorialStep === 0;
    setText(el.btnTutorialNext, state.tutorialStep === total - 1 ? '시작하기' : '다음');
  }

  function showTutorial(step) {
    closeModal(el.settingsDialog);
    state.tutorialStep = typeof step === 'number' ? step : 0;
    renderTutorial();
    writeStore(STORAGE.tutorialSeen, 'seen');
    if (typeof el.tutorialDialog.showModal === 'function') {
      if (!el.tutorialDialog.open) { el.tutorialDialog.showModal(); }
    } else { el.tutorialDialog.setAttribute('open', ''); }
    window.setTimeout(function () { el.btnCloseTutorial.focus(); }, 0);
  }

  function closeTutorial() {
    writeStore(STORAGE.tutorialSeen, 'seen');
    closeModal(el.tutorialDialog);
  }

  function moveTutorial(offset) {
    var next = state.tutorialStep + offset;
    if (next >= el.tutorialSteps.length) { closeTutorial(); return; }
    state.tutorialStep = Math.max(0, next);
    renderTutorial();
  }

  /* ---------- Windows 앱 업데이트 ----------
     백엔드가 시작할 때 스스로 확인·다운로드하므로 UI는 상태를 읽어 보여주기만 한다.
     응답 필드 이름이 바뀌어도 화면이 깨지지 않도록 여러 후보 키를 관용적으로 읽는다. */

  function updateSection() {
    if (!el.btnCheckUpdate || typeof el.btnCheckUpdate.closest !== 'function') { return null; }
    return el.btnCheckUpdate.closest('.settings-section');
  }

  function pickField(sources, names) {
    for (var s = 0; s < sources.length; s += 1) {
      var source = sources[s];
      if (!source || typeof source !== 'object') { continue; }
      for (var n = 0; n < names.length; n += 1) {
        var value = source[names[n]];
        if (value !== undefined && value !== null && value !== '') { return value; }
      }
    }
    return undefined;
  }
  function pickFlag(sources, names) {
    var value = pickField(sources, names);
    if (value === undefined) { return undefined; }
    if (typeof value === 'boolean') { return value; }
    var text = String(value).toLowerCase();
    if (text === 'true' || text === '1' || text === 'yes') { return true; }
    if (text === 'false' || text === '0' || text === 'no') { return false; }
    return undefined;
  }
  function pickText(sources, names) {
    var value = pickField(sources, names);
    if (value === undefined) { return ''; }
    if (typeof value === 'string') { return value.trim(); }
    if (typeof value === 'number' || typeof value === 'boolean') { return String(value); }
    if (typeof value === 'object') {
      var nested = pickField([value], ['message', 'detail', 'title', 'text', 'error']);
      return nested === undefined ? '' : String(nested).trim();
    }
    return '';
  }
  function pickNumber(sources, names) {
    var value = pickField(sources, names);
    if (value === undefined) { return null; }
    var parsed = typeof value === 'number' ? value : parseFloat(String(value).replace('%', ''));
    return isNaN(parsed) ? null : parsed;
  }

  function normalizeUpdatePhase(raw) {
    var key = String(raw || '').toLowerCase().replace(/[^a-z]/g, '');
    if (!key) { return ''; }
    if (/^(checking|checkingforupdates|checkforupdate|querying|searching)/.test(key)) { return 'checking'; }
    if (/^(downloading|download|fetching|downloadinprogress)/.test(key)) { return 'downloading'; }
    if (/^(verifying|verify|validating|validate|signaturecheck)/.test(key)) { return 'verifying'; }
    if (/^(ready|readytoinstall|readyforinstall|downloaded|verified|installready|pendingrestart|pendinginstall|staged)/.test(key)) { return 'ready'; }
    if (/^(installing|install|applying|apply|restarting|updating)/.test(key)) { return 'installing'; }
    if (/^(available|updateavailable|newversionavailable|found)/.test(key)) { return 'available'; }
    if (/^(uptodate|latest|current|none|noupdate|notavailable|idle|nonepending)/.test(key)) { return 'idle'; }
    if (/^(unsupported|notsupported|unavailable|disabled|off|portable|devbuild)/.test(key)) { return 'unsupported'; }
    if (/^(error|failed|failure|faulted)/.test(key)) { return 'error'; }
    return '';
  }

  /* 백엔드는 availability / downloadState 열거형을 문자열로 보낸다. 이름이 바뀌어도
     아래 매핑에 없으면 일반 단계 추론으로 넘어간다. */
  function phaseFromAvailability(raw) {
    var key = String(raw || '').toLowerCase().replace(/[^a-z]/g, '');
    if (key === 'uptodate') { return 'idle'; }
    if (key === 'updateavailable') { return 'available'; }
    if (key === 'disabled' || key === 'unsupported') { return 'unsupported'; }
    if (key === 'failed') { return 'error'; }
    return '';
  }
  function phaseFromDownloadState(raw) {
    var key = String(raw || '').toLowerCase().replace(/[^a-z]/g, '');
    if (key === 'downloading') { return 'downloading'; }
    if (key === 'verified') { return 'ready'; }
    if (key === 'verificationfailed' || key === 'failed') { return 'error'; }
    return '';
  }

  function formatBytes(value) {
    var bytes = typeof value === 'number' ? value : parseFloat(value);
    if (isNaN(bytes) || bytes <= 0) { return ''; }
    if (bytes < 1024 * 1024) { return Math.max(1, Math.round(bytes / 1024)) + ' KB'; }
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
  }

  /* 0~1 비율과 0~100 퍼센트를 모두 받는다. 바이트 수만 오는 경우도 계산한다. */
  function normalizeUpdateProgress(sources) {
    var percent = pickNumber(sources, ['progressPercent', 'percentComplete', 'percent', 'downloadPercent', 'progress', 'downloadProgress']);
    if (percent === null) {
      var done = pickNumber(sources, ['bytesDownloaded', 'downloadedBytes', 'receivedBytes', 'transferred']);
      var total = pickNumber(sources, ['totalBytes', 'contentLength', 'sizeBytes', 'totalSize']);
      if (done !== null && total !== null && total > 0) { percent = (done / total) * 100; }
    }
    if (percent === null) { return null; }
    if (percent > 0 && percent <= 1) { percent *= 100; }
    return Math.max(0, Math.min(100, Math.round(percent)));
  }

  function normalizeUpdateStatus(payload) {
    var sources = [payload];
    ['update', 'status', 'state', 'data', 'result', 'info', 'details'].forEach(function (key) {
      var nested = payload && typeof payload === 'object' ? payload[key] : null;
      if (nested && typeof nested === 'object' && !Array.isArray(nested)) { sources.push(nested); }
    });

    var currentVersion = pickText(sources, ['currentVersion', 'installedVersion', 'current', 'version', 'appVersion', 'runningVersion']);
    var latestVersion = pickText(sources, ['latestVersion', 'availableVersion', 'newVersion', 'targetVersion', 'nextVersion', 'remoteVersion']);
    var available = pickFlag(sources, ['updateAvailable', 'isUpdateAvailable', 'hasUpdate', 'isAvailable']);
    var ready = pickFlag(sources, ['installerReady', 'readyToInstall', 'isReadyToInstall', 'installReady', 'ready', 'isReady', 'downloadComplete', 'pendingRestart']);
    var verified = pickFlag(sources, ['verified', 'isVerified', 'signatureVerified', 'validated']);
    var downloading = pickFlag(sources, ['downloading', 'isDownloading']);
    var checking = pickFlag(sources, ['checking', 'isChecking', 'checkInProgress']);
    var installing = pickFlag(sources, ['installing', 'isInstalling']);
    var supportedPlatform = pickFlag(sources, ['supportedPlatform', 'supported', 'isSupported']);
    var enabled = pickFlag(sources, ['enabled', 'isEnabled', 'updatesEnabled']);
    var supported = supportedPlatform === false || enabled === false ? false
      : (supportedPlatform === true || enabled === true ? true : undefined);
    var error = pickText(sources, ['error', 'errorMessage', 'lastError', 'failureReason', 'errorDetail']);
    var progress = normalizeUpdateProgress(sources);
    var lastCheckedAt = pickText(sources, ['lastCheckedAt', 'checkedAt', 'lastChecked', 'lastCheckUtc', 'lastCheckTime']);
    var releaseNotes = pickText(sources, ['releaseNotes', 'notes', 'changelog', 'releaseNotesUrl']);
    var message = pickText(sources, ['message', 'statusMessage', 'note']);
    var installerSize = formatBytes(pickField(sources, ['installerSizeBytes', 'installerSize', 'sizeBytes']));
    var prerelease = pickFlag(sources, ['latestIsPrerelease', 'isPrerelease', 'prerelease']) === true;

    var availability = pickText(sources, ['availability', 'updateAvailability']);
    var downloadState = pickText(sources, ['downloadState', 'downloadStatus']);
    var phase = phaseFromDownloadState(downloadState) || phaseFromAvailability(availability)
      || normalizeUpdatePhase(pickText(sources, ['phase', 'stage', 'state', 'status', 'updateState', 'statusText']));
    if (!phase) {
      if (supported === false) { phase = 'unsupported'; }
      else if (installing === true) { phase = 'installing'; }
      else if (ready === true || (available === true && verified === true)) { phase = 'ready'; }
      else if (downloading === true || (progress !== null && progress > 0 && progress < 100)) { phase = 'downloading'; }
      else if (checking === true) { phase = 'checking'; }
      else if (available === true) { phase = 'available'; }
      else if (error) { phase = 'error'; }
      else if (available === false) { phase = 'idle'; }
      else { phase = 'unknown'; }
    }
    /* 명시적인 준비 완료 플래그는 문자열 단계보다 우선한다. */
    if (ready === true && phase !== 'installing') { phase = 'ready'; }
    if (installing === true) { phase = 'installing'; }
    /* Verified 로 표시됐지만 설치 파일이 아직 없으면 준비 완료로 보지 않는다. */
    if (phase === 'ready' && ready === false) { phase = available === false ? 'idle' : 'available'; }
    if (supported === false) { phase = 'unsupported'; }
    if (!error && (phase === 'error' || phase === 'unsupported')) { error = message; message = ''; }

    return {
      phase: phase,
      currentVersion: currentVersion,
      latestVersion: latestVersion,
      available: available === true || phase === 'available' || phase === 'downloading' || phase === 'ready',
      verified: verified === true || phase === 'ready',
      progress: progress,
      error: error,
      lastCheckedAt: lastCheckedAt,
      releaseNotes: releaseNotes,
      message: message,
      installerSize: installerSize,
      prerelease: prerelease
    };
  }

  var UPDATE_BUSY_PHASES = ['checking', 'downloading', 'verifying', 'installing'];

  function describeUpdateStatus(info) {
    var target = info.latestVersion ? ' ' + info.latestVersion : '';
    if (info.phase === 'unsupported') { return '이 설치 방식에서는 자동 업데이트를 사용하지 않습니다.'; }
    if (info.phase === 'checking') { return '새 버전을 확인하는 중입니다…'; }
    if (info.phase === 'downloading') {
      return '새 버전' + target + '을 내려받는 중입니다' + (info.progress === null ? '…' : ' · ' + info.progress + '%');
    }
    if (info.phase === 'verifying') { return '내려받은 파일' + target + '의 서명을 확인하는 중입니다…'; }
    if (info.phase === 'ready') { return '새 버전' + target + ' 확인이 끝났습니다. 원하는 때에 설치하세요.'; }
    if (info.phase === 'installing') { return '설치를 진행하는 중입니다. 앱이 곧 다시 시작됩니다.'; }
    if (info.phase === 'available') { return '새 버전' + target + '을 찾았습니다. 백그라운드에서 준비합니다.'; }
    if (info.phase === 'error') { return '업데이트 확인에 실패했습니다. 잠시 후 다시 시도하세요.'; }
    if (info.phase === 'idle') {
      return '최신 버전입니다.' + (info.lastCheckedAt ? ' 마지막 확인 ' + formatDateTime(info.lastCheckedAt) : '');
    }
    return '업데이트 상태를 아직 확인하지 못했습니다.';
  }

  function renderUpdate() {
    var info = state.update;
    if (!info || !el.updateStatusText) { return; }
    var busy = UPDATE_BUSY_PHASES.indexOf(info.phase) >= 0;

    setText(el.updateCurrentVersion, info.currentVersion || '알 수 없음');
    setText(el.updateStatusText, describeUpdateStatus(info));

    if (el.updateProgress) {
      var showProgress = info.phase === 'downloading' || info.phase === 'verifying';
      el.updateProgress.hidden = !showProgress;
      if (showProgress && el.updateProgressBar) {
        var indeterminate = info.progress === null;
        el.updateProgressBar.classList.toggle('is-indeterminate', indeterminate);
        el.updateProgressBar.style.width = indeterminate ? '' : info.progress + '%';
      }
    }

    if (el.updateBadge) {
      var badge = '';
      var badgeClass = '';
      if (info.phase === 'ready') { badge = '설치 준비 완료'; badgeClass = 'is-ready'; }
      else if (info.phase === 'downloading' || info.phase === 'verifying' || info.phase === 'available') { badge = '준비 중'; }
      else if (info.phase === 'installing') { badge = '설치 중'; }
      else if (info.phase === 'error') { badge = '확인 실패'; badgeClass = 'is-bad'; }
      else if (info.phase === 'unsupported') { badge = '사용 안 함'; badgeClass = 'is-warn'; }
      else if (info.phase === 'idle') { badge = '최신'; badgeClass = 'is-ready'; }
      el.updateBadge.hidden = !badge;
      el.updateBadge.className = 'update-badge' + (badgeClass ? ' ' + badgeClass : '');
      setText(el.updateBadge, badge);
    }

    if (el.updateErrorText) {
      el.updateErrorText.hidden = !info.error;
      setText(el.updateErrorText, info.error || '');
    }

    if (el.btnCheckUpdate) {
      el.btnCheckUpdate.disabled = busy || info.phase === 'unsupported';
      setText(el.btnCheckUpdate, info.phase === 'checking' ? '확인 중…' : '업데이트 확인');
    }
    if (el.btnInstallUpdate) {
      el.btnInstallUpdate.hidden = info.phase !== 'ready' && info.phase !== 'installing';
      el.btnInstallUpdate.disabled = info.phase === 'installing';
      setText(el.btnInstallUpdate, info.phase === 'installing' ? '설치 중…' : '설치하고 다시 시작');
    }

    if (el.versionText) {
      var chipLabel = info.currentVersion ? 'v' + String(info.currentVersion).replace(/^v/i, '') : '버전 확인 중';
      if (info.phase === 'ready') { chipLabel += ' · 업데이트 준비됨'; }
      else if (info.phase === 'downloading' || info.phase === 'verifying' || info.phase === 'available') { chipLabel += ' · 업데이트 준비 중'; }
      setText(el.versionText, chipLabel);
    }
    if (el.versionDot) {
      setDot(el.versionDot, info.phase === 'ready' ? true : (info.phase === 'error' ? 'warn' : null));
    }
    if (el.btnVersionChip) {
      el.btnVersionChip.classList.toggle('is-ready', info.phase === 'ready');
      el.btnVersionChip.title = describeUpdateStatus(info);
    }

    var section = updateSection();
    if (section) { section.hidden = info.phase === 'unavailable'; }
  }

  /* 준비 완료는 회의를 방해하지 않도록 상단 알림과 토스트로 한 번만 알린다. */
  function announceUpdateReady(info) {
    var key = info.latestVersion || info.currentVersion || 'ready';
    if (state.updateAnnouncedFor === key) { return; }
    state.updateAnnouncedFor = key;
    var label = info.latestVersion ? '새 버전 ' + info.latestVersion : '새 버전';
    toast('ok', '업데이트 준비 완료', label + ' 설치가 준비되었습니다. 회의가 끝난 뒤 설치하세요.');
    showAlert('ok', '업데이트 준비 완료', label + '이 준비되었습니다. 설치는 직접 누를 때만 진행되며 앱이 다시 시작됩니다.',
      '설치 화면 열기', openUpdateSettings);
  }

  function openUpdateSettings() {
    openSettings();
    var section = updateSection();
    if (section && typeof section.scrollIntoView === 'function') {
      section.scrollIntoView({ block: 'nearest' });
    }
    if (el.btnInstallUpdate && !el.btnInstallUpdate.hidden) { el.btnInstallUpdate.focus(); }
    else if (el.btnCheckUpdate) { el.btnCheckUpdate.focus(); }
  }

  function applyUpdateStatus(payload) {
    state.update = normalizeUpdateStatus(payload);
    renderUpdate();
    if (state.update.phase === 'ready') { announceUpdateReady(state.update); }
    return state.update;
  }

  function handleUpdateFailure(error, options) {
    var opts = options || {};
    /* 엔드포인트가 없는 빌드에서는 업데이트 UI를 조용히 감춘다. */
    if (error && (error.status === 404 || error.status === 501)) {
      state.update = normalizeUpdateStatus({ phase: 'unavailable', currentVersion: state.update ? state.update.currentVersion : '' });
      state.update.phase = 'unavailable';
      renderUpdate();
      var section = updateSection();
      if (section) { section.hidden = true; }
      if (el.btnVersionChip) { el.btnVersionChip.hidden = true; }
      return;
    }
    state.update = state.update || normalizeUpdateStatus({});
    state.update.phase = 'error';
    state.update.error = error ? error.message : '알 수 없는 오류';
    renderUpdate();
    if (!opts.silent) { toast('bad', opts.title || '업데이트 확인 실패', state.update.error); }
  }

  function loadUpdateStatus(options) {
    var opts = options || {};
    return request('/api/update/status').then(function (payload) {
      applyUpdateStatus(payload || {});
      return state.update;
    }, function (error) {
      handleUpdateFailure(error, { silent: opts.silent !== false });
      return state.update;
    });
  }

  function checkForUpdate() {
    if (state.update && state.update.phase === 'checking') { return; }
    state.update = state.update || normalizeUpdateStatus({});
    state.update.phase = 'checking';
    state.update.error = '';
    renderUpdate();
    request('/api/update/check', { method: 'POST', json: {} }).then(function (payload) {
      var info = payload && typeof payload === 'object' ? applyUpdateStatus(payload) : null;
      if (!info) { return loadUpdateStatus({ silent: false }); }
      if (info.phase === 'idle') { toast('ok', '최신 버전입니다', '설치된 버전 ' + (info.currentVersion || '–')); }
      else if (info.phase !== 'ready') { toast('ok', '업데이트 확인 완료', describeUpdateStatus(info)); }
      return info;
    }, function (error) {
      handleUpdateFailure(error, { silent: false });
    });
  }

  function installUpdate() {
    var info = state.update;
    if (!info || info.phase !== 'ready') {
      toast('warn', '설치할 업데이트가 없습니다', '먼저 업데이트를 확인하세요.');
      return;
    }
    var present = asArray(state.board && state.board.people).filter(function (person) { return person.attendanceState === 'Present'; }).length;
    var warning = present > 0
      ? '현재 ' + present + '명이 참석 중입니다. 설치하면 앱이 종료되고 다시 시작됩니다. 계속할까요?'
      : '설치하면 앱이 종료되고 새 버전으로 다시 시작됩니다. 계속할까요?';
    return confirmAction(warning, function () {
    if (state.update !== info || info.phase !== 'ready') { toast('warn', '업데이트 상태가 바뀌었습니다', '현재 설치 가능한 버전을 다시 확인하세요.'); return; }
    state.update.phase = 'installing';
    state.update.error = '';
    renderUpdate();
    hideAlert();
    var busy = beginBusy('업데이트를 설치하는 중…');
    request('/api/update/install', { method: 'POST', json: {} }).then(function (payload) {
      endBusy(busy);
      if (payload && typeof payload === 'object') { applyUpdateStatus(payload); }
      if (state.update.phase !== 'error') {
        state.update.phase = 'installing';
        renderUpdate();
      }
      toast('ok', '업데이트 설치 시작', '앱이 종료된 뒤 새 버전으로 다시 시작됩니다.');
    }, function (error) {
      endBusy(busy);
      handleUpdateFailure(error, { silent: false, title: '업데이트 설치 실패' });
    });
    });
  }

  function cacheElements() {
    [
      'meeting-id','health-dot','health-text','zoom-dot','zoom-status','chk-autorefresh','auto-status','last-sync-time','next-sync-time',
      'readiness','readiness-title','readiness-note','btn-next-step','btn-review','source-status','activity-mode','auto-mode-note',
      'scope-note','export-scope','count-excluded','empty-title','empty-note','btn-reset-filters','alert-details','alert-technical',
      'snapshot-result','snapshot-result-summary','snapshot-result-details',
      'action-confirm','action-confirm-message','btn-confirm-cancel','btn-confirm-apply',
      'btn-sync-zoom','btn-toggle-theme','btn-open-settings','btn-open-tutorial','btn-health-detail','btn-api-detail','alert-bar','alert-dot','alert-title','alert-message','btn-alert-action','btn-dismiss-alert',
      'summary-total','summary-present','summary-rate','summary-time','rate-ring','metric-present','metric-absent','metric-review','metric-duplicate',
      'participants-meta','btn-refresh','btn-export','participant-search','group-filter','participant-table-wrap','participant-rows','participants-empty','visible-count',
      'count-all','count-present','count-absent','count-review','count-unmatched','activity-feed','activity-empty','activity-count',
      'duplicate-list','duplicate-empty','duplicate-count','sync-summary','roster-summary','settings-dialog','roster-file','btn-upload-roster',
      'btn-reload-roster','roster-note','api-settings-status','btn-check-zoom','settings-autorefresh','autorefresh-interval','snapshot-names',
      'snapshot-parsed','chk-empty-ok','btn-submit-snapshot','btn-clear-snapshot','session-log','session-log-empty','btn-clear-log',
      'toast-region','busy','busy-text',
      'zoom-app-dot','zoom-app-status','btn-zoom-app-detail','mode-note','zoom-app-settings-status','pairing-code','pairing-expiry',
      'pairing-session','btn-create-pairing-code','btn-copy-pairing-code','btn-zoom-app-sync','zoom-app-home-url','btn-copy-home-url',
      'update-current-version','update-status-text','update-progress','update-progress-bar','update-badge','update-error-text',
      'btn-check-update','btn-install-update','btn-version-chip','version-dot','version-text',
      'btn-settings-tutorial','tutorial-dialog','tutorial-progress-text','btn-close-tutorial','btn-skip-tutorial','btn-tutorial-back','btn-tutorial-next'
    ].forEach(function (id) {
      var key = id.replace(/-([a-z])/g, function (_, letter) { return letter.toUpperCase(); });
      el[key] = $(id);
    });
    el.filterButtons = Array.prototype.slice.call(document.querySelectorAll('[data-filter]'));
    el.tutorialSteps = Array.prototype.slice.call(document.querySelectorAll('[data-tutorial-step]'));
    el.tutorialProgressButtons = Array.prototype.slice.call(document.querySelectorAll('#tutorial-progress [role="tab"]'));
    el.modeRadios = Array.prototype.slice.call(document.querySelectorAll('input[name="connection-mode"]'));
    el.themeRadios = Array.prototype.slice.call(document.querySelectorAll('input[name="theme"]'));
    el.chkAutoRefresh = el.chkAutorefresh;
    el.alertAction = el.btnAlertAction;
    el.alertBar = el.alertBar;
    el.settingsDialog = el.settingsDialog;
    el.settingsAutoRefresh = el.settingsAutorefresh;
    el.intervalInput = el.autorefreshInterval;
    el.participantTableWrap = el.participantTableWrap;
    el.participantRows = el.participantRows;
    el.sessionLogEmpty = el.sessionLogEmpty;
  }

  function bindEvents() {
    el.btnConfirmCancel.addEventListener('click', function () { el.actionConfirm.close('cancel'); });
    el.btnConfirmApply.addEventListener('click', function () { el.actionConfirm.close('apply'); });
    el.actionConfirm.addEventListener('close', function () {
      if (state.confirmationCallback) { state.confirmationCallback(el.actionConfirm.returnValue === 'apply'); }
    });
    el.btnNextStep.addEventListener('click', function () { if (state.readinessAction) { state.readinessAction(); } });
    el.btnReview.addEventListener('click', function () {
      el.participantTableWrap.scrollTop = 0;
      state.filter = 'review'; el.participantSearch.value = ''; syncFilterButtons(); renderParticipants();
      el.participantTableWrap.scrollIntoView({ block: 'nearest' });
      var filter = el.filterButtons.filter(function (button) { return button.dataset.filter === 'review'; })[0];
      if (filter) { filter.focus(); }
    });
    el.btnResetFilters.addEventListener('click', function () {
      state.filter = 'all'; el.participantSearch.value = ''; applyGroup('', { notify: false }); syncFilterButtons(); el.participantSearch.focus();
    });
    el.btnSyncZoom.addEventListener('click', function () { syncNow({ silent: false }); });
    el.btnRefresh.addEventListener('click', function () { refreshBoard({ notify: true }); });
    el.btnExport.addEventListener('click', exportCsv);
    el.btnToggleTheme.addEventListener('click', toggleTheme);
    el.btnOpenSettings.addEventListener('click', openSettings);
    el.btnOpenTutorial.addEventListener('click', function () { showTutorial(0); });
    el.btnSettingsTutorial.addEventListener('click', function () { showTutorial(0); });
    el.btnCloseTutorial.addEventListener('click', closeTutorial);
    el.btnSkipTutorial.addEventListener('click', closeTutorial);
    el.btnTutorialBack.addEventListener('click', function () { moveTutorial(-1); });
    el.btnTutorialNext.addEventListener('click', function () { moveTutorial(1); });
    el.tutorialProgressButtons.forEach(function (button, index) {
      button.addEventListener('click', function () { state.tutorialStep = index; renderTutorial(); });
    });
    el.tutorialDialog.addEventListener('close', function () { writeStore(STORAGE.tutorialSeen, 'seen'); });
    el.btnHealthDetail.addEventListener('click', checkHealth);
    el.btnApiDetail.addEventListener('click', function () { openSettingsSection('api'); checkZoomConnection(false); });
    el.btnZoomAppDetail.addEventListener('click', function () { openSettingsSection('pairing'); checkZoomConnection(false); });
    el.btnCheckZoom.addEventListener('click', function () { checkZoomConnection(true); });
    el.btnCreatePairingCode.addEventListener('click', createPairingCode);
    el.btnCopyPairingCode.addEventListener('click', function () {
      copyToClipboard(state.pairing && state.pairing.code, '페어링 코드 복사됨');
    });
    el.btnZoomAppSync.addEventListener('click', function () { requestZoomAppSync({ silent: false }); });
    el.btnCopyHomeUrl.addEventListener('click', function () {
      copyToClipboard(state.zoomApp && state.zoomApp.homeUrl, 'Home URL 복사됨');
    });
    if (el.btnCheckUpdate) { el.btnCheckUpdate.addEventListener('click', checkForUpdate); }
    if (el.btnInstallUpdate) { el.btnInstallUpdate.addEventListener('click', installUpdate); }
    if (el.btnVersionChip) { el.btnVersionChip.addEventListener('click', openUpdateSettings); }
    el.modeRadios.forEach(function (radio) {
      radio.addEventListener('change', function () {
        if (radio.checked) { applyConnectionMode(radio.value, { notify: true }); }
      });
    });
    el.themeRadios.forEach(function (radio) {
      radio.addEventListener('change', function () {
        if (radio.checked) { applyTheme(radio.value, { persist: true, notify: true }); }
      });
    });
    el.btnUploadRoster.addEventListener('click', uploadRoster);
    el.btnReloadRoster.addEventListener('click', function () { loadRoster(true); });
    el.btnSubmitSnapshot.addEventListener('click', submitSnapshot);
    el.btnClearSnapshot.addEventListener('click', function () { el.snapshotNames.value = ''; updateParsedCount(); });
    el.btnClearLog.addEventListener('click', function () { state.sessionLog = []; renderSessionLog(); });
    el.snapshotNames.addEventListener('input', updateParsedCount);
    el.participantSearch.addEventListener('input', function () {
      el.participantTableWrap.scrollTop = 0; renderParticipants();
    });
    if (el.groupFilter) {
      el.groupFilter.addEventListener('change', function () { applyGroup(el.groupFilter.value, { notify: true }); });
    }
    el.filterButtons.forEach(function (button) {
      button.addEventListener('click', function () {
        el.participantTableWrap.scrollTop = 0;
        state.filter = button.dataset.filter; syncFilterButtons(); renderParticipants();
      });
    });
    el.meetingId.addEventListener('input', resetMeetingSelection);
    el.meetingId.addEventListener('change', function () {
      var normalizedMeetingId = currentMeetingId();
      el.meetingId.value = normalizedMeetingId;
      writeStore(STORAGE.meetingId, normalizedMeetingId);
      resetMeetingSelection();
      refreshBoard({ silent: true });
    });
    el.chkAutoRefresh.addEventListener('change', function () { applyAutoRefresh(el.chkAutoRefresh.checked); });
    el.settingsAutoRefresh.addEventListener('change', function () { applyAutoRefresh(el.settingsAutoRefresh.checked); });
    el.intervalInput.addEventListener('change', function () { el.intervalInput.value = intervalSeconds(); applyAutoRefresh(el.chkAutoRefresh.checked); });
    el.alertAction.addEventListener('click', function () { if (state.alertAction) { state.alertAction(); } });
    el.btnDismissAlert.addEventListener('click', hideAlert);
    document.addEventListener('keydown', function (event) {
      if (el.actionConfirm.open) { return; }
      if (el.tutorialDialog.open && (event.key === 'ArrowRight' || event.key === 'ArrowLeft')) {
        event.preventDefault(); moveTutorial(event.key === 'ArrowRight' ? 1 : -1); return;
      }
      if (event.key === '/' && !el.settingsDialog.open && !/^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement.tagName)) { event.preventDefault(); el.participantSearch.focus(); }
      if ((event.key === 'r' || event.key === 'R') && !event.metaKey && !event.ctrlKey && !event.altKey && !el.settingsDialog.open && document.activeElement.tagName !== 'INPUT' && document.activeElement.tagName !== 'TEXTAREA') {
        event.preventDefault(); syncNow({ silent: false });
      }
    });
  }

  function restoreState() {
    applyTheme(readStore(STORAGE.theme, 'system'), { persist: false, notify: false });
    el.meetingId.value = normalizeMeetingId(readStore(STORAGE.meetingId, ''));
    var savedInterval = parseInt(readStore(STORAGE.interval, '10'), 10);
    el.intervalInput.value = isNaN(savedInterval) ? '10' : String(Math.min(600, Math.max(5, savedInterval)));
    var auto = readStore(STORAGE.autoRefresh, '0') === '1';
    el.chkAutoRefresh.checked = auto;
    el.settingsAutoRefresh.checked = auto;
    applyConnectionMode(readStore(STORAGE.connectionMode, 'auto'), { notify: false });
    state.group = normalizeGroupName(readStore(STORAGE.group, ''));
    renderGroupOptions();
  }

  function init() {
    cacheElements();
    var autoOpenTutorial = shouldAutoOpenTutorial();
    restoreState();
    bindEvents();
    watchSystemTheme();
    renderSessionLog();
    updateParsedCount();
    applyAutoRefresh(el.chkAutoRefresh.checked);
    state.clockId = window.setInterval(updateClock, 1000);
    state.connectionPollId = window.setInterval(function () {
      checkZoomConnection(false).then(function () {
        if (state.pending === 0 && !document.hidden && state.zoomApp && (state.zoomApp.sessionActive || state.zoomApp.connected) && currentMeetingId()) { refreshBoard({ silent: true }); }
      });
    }, 5000);
    checkHealth();
    checkZoomConnection(false);
    state.statusPollId = window.setInterval(function () {
      if (!document.hidden) { checkZoomConnection(false); }
    }, 10000);
    loadRoster(false);
    /* 시작 시 상태만 읽는다. 실제 확인·다운로드는 백엔드가 자동으로 수행하므로 화면을 막지 않는다. */
    loadUpdateStatus({ silent: true });
    state.updatePollId = window.setInterval(function () {
      if (document.hidden) { return; }
      var phase = state.update ? state.update.phase : '';
      if (phase === 'unavailable' || phase === 'unsupported') { return; }
      loadUpdateStatus({ silent: true });
    }, 15000);
    if (currentMeetingId()) { refreshBoard({ silent: true }); }
    if (autoOpenTutorial) { window.setTimeout(function () { showTutorial(0); }, 250); }
  }

  if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', init); }
  else { init(); }
})();
