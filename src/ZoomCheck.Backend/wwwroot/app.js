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
    tutorialSeen: 'zoomcheck.tutorialSeen.v1',
    lang: 'zoomcheck.lang',
    sort: 'zoomcheck.participantSort'
  };
  var SUPPORTED_LANGS = ['ko', 'en', 'es'];
  var CONNECTION_MODES = ['auto', 'business', 'zoomApp', 'manual'];
  var THEME_OPTIONS = ['system', 'light', 'dark'];
  var SORT_KEYS = ['number', 'name'];
  var MODE_LABEL = {
    auto: '자동',
    business: 'Business API',
    zoomApp: 'Zoom 앱 (Pro)',
    manual: '직접 입력'
  };
  var MAX_ROSTER_BYTES = 20 * 1024 * 1024;
  var MAX_SNAPSHOT_NAMES = 2000;
  var MAX_SESSION_LOG = 60;
  var CONNECTION_POLL_MS = 5000;
  var REVIEW_CONFIDENCE = ['Possible'];
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

  function getLocales() {
    return (typeof ZOOMCHECK_LOCALES !== 'undefined' ? ZOOMCHECK_LOCALES : null) ||
           (typeof globalThis !== 'undefined' && globalThis.ZOOMCHECK_LOCALES) ||
           (typeof window !== 'undefined' && window.ZOOMCHECK_LOCALES) || null;
  }

  function koreanEuro(word) {
    var trimmed = String(word || '').replace(/[\s)\]}"'.,!?]+$/g, '');
    if (!trimmed) { return '으로'; }
    var last = trimmed.charAt(trimmed.length - 1);
    var code = last.charCodeAt(0);
    if (code >= 0xAC00 && code <= 0xD7A3) {
      var jong = (code - 0xAC00) % 28;
      return (jong === 0 || jong === 8) ? '로' : '으로';
    }
    if (code >= 48 && code <= 57) {
      return (last === '0' || last === '3' || last === '6') ? '으로' : '로';
    }
    var tokenMatch = trimmed.match(/([A-Za-z]+)$/);
    if (tokenMatch) {
      var token = tokenMatch[1];
      if (token.length >= 2 && token === token.toUpperCase()) {
        return (last === 'M' || last === 'N') ? '으로' : '로';
      }
      return /[mnpbkgc]/.test(last.toLowerCase()) ? '으로' : '로';
    }
    return '로';
  }

  function t(key, params) {
    var locales = getLocales();
    if (!locales) {
      return key;
    }
    var currentLang = (state && state.lang) || 'ko';
    var dict = locales[currentLang] || locales.ko || {};
    var text = dict[key] !== undefined ? dict[key] : ((locales.ko && locales.ko[key]) !== undefined ? locales.ko[key] : key);
    return params ? text.replace(/\{(\w+)(?:\|(으로))?\}/g, function (placeholder, name, particle) {
      if (!Object.prototype.hasOwnProperty.call(params, name)) { return placeholder; }
      var value = String(params[name]);
      return particle === '으로' ? value + koreanEuro(value) : value;
    }) : text;
  }

  function getModeLabel(mode) {
    var key = 'mode_' + mode;
    var val = t(key);
    return (val && val !== key) ? val : (MODE_LABEL[mode] || mode);
  }
  function getConfidenceLabel(confidence) {
    var key = 'confidence_' + confidence;
    var val = t(key);
    return (val && val !== key) ? val : (CONFIDENCE_LABEL[confidence] || confidence || '–');
  }
  function getEventLabel(event) {
    var key = 'event_' + event;
    var val = t(key);
    return (val && val !== key) ? val : (EVENT_LABEL[event] || event);
  }

  var state = {
    lang: 'ko',
    board: null,
    roster: [],
    rows: [],
    connections: [],
    duplicates: [],
    filter: 'all',
    sort: 'number',
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
    connectionPollInFlight: false,
    nextSyncAt: null,
    lastSyncAt: null,
    lastSyncOk: null,
    lastAttemptAt: null,
    awaitingSnapshot: false,
    requestedAfterReceipt: null,
    connectionDrafts: Object.create(null),
    rosterLoaded: false,
    rosterError: false,
    rosterFeedback: null,
    readinessAction: null,
    sessionLog: [],
    alertAction: null,
    healthStatus: null,
    zoomApiStatus: null,
    zoomApiError: '',
    zoomConfigured: false,
    connectionMode: 'auto',
    recommendedMode: null,
    zoomApp: null,
    pairing: null,
    relaySettings: null,
    relaySettingsRevision: 0,
    relaySettingsSaving: false,
    relayHealth: null,
    relayHealthChecking: false,
    relayHealthRevision: 0,
    update: null,
    updateAnnouncedFor: null,
    updatePollId: null,
    tutorialStep: 0,
    themePreference: 'system'
  };
  var el = {};

  function $(id) { return document.getElementById(id); }
  function setText(node, value) {
    if (!node) { return; }
    var next = value === null || value === undefined ? '' : String(value);
    if (node.children && node.children.length === 0 && node.textContent === next) { return; }
    node.textContent = next;
  }
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
      var nextLabel = t(effective === 'dark' ? 'light_mode' : 'dark_mode');
      setText(el.btnToggleTheme, nextLabel);
      el.btnToggleTheme.setAttribute('aria-label', t('theme_switch', { theme: nextLabel }));
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
    setText(el.syncSummary.lastChild, t('sync_waiting'));
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

  function renderHealthStatus() {
    if (!el.healthText) { return; }
    if (state.healthStatus === 'ok') { setText(el.healthText, t('status_connected')); }
    else if (state.healthStatus === 'error') { setText(el.healthText, t('error')); }
    else if (state.healthStatus === 'failed') { setText(el.healthText, t('status_connect_failed')); }
    else { setText(el.healthText, t('checking')); }
  }

  function checkHealth() {
    return request('/health').then(function (payload) {
      var ok = payload && payload.status === 'ok';
      state.healthStatus = ok ? 'ok' : 'error';
      setDot(el.healthDot, ok);
      renderHealthStatus();
      return ok;
    }, function (error) {
      state.healthStatus = 'failed';
      setDot(el.healthDot, false);
      renderHealthStatus();
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

  function renderZoomApiDot() {
    if (!el.zoomDot) { return; }
    setDot(el.zoomDot, state.zoomConfigured ? true : (state.connectionMode === 'business' ? 'warn' : null));
  }

  function applyConnectionMode(mode, options) {
    var opts = options || {};
    state.connectionMode = normalizeMode(mode);
    writeStore(STORAGE.connectionMode, state.connectionMode);
    el.modeRadios.forEach(function (radio) { radio.checked = radio.value === state.connectionMode; });
    renderModeNote();
    renderZoomApiDot();
    renderZoomAppStrip();
    if (opts.notify) {
      toast('ok', t('mode_changed'), t('mode_sync_with', { mode: getModeLabel(state.connectionMode) }));
    }
  }

  function renderModeNote() {
    var active = effectiveMode();
    var note = t('mode_selected', { mode: getModeLabel(state.connectionMode) });
    if (state.connectionMode === 'auto') { note += t('mode_current', { mode: getModeLabel(active) }); }
    if (state.recommendedMode) { note += t('mode_recommended', { mode: getModeLabel(state.recommendedMode) }); }
    if (active === 'manual') { note += t('mode_manual_help'); }
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
    setText(el.btnSyncZoom, manual ? t('sync_paste_list') : t('sync_now'));
    el.chkAutoRefresh.disabled = manual;
    el.settingsAutoRefresh.disabled = manual;
    el.intervalInput.disabled = manual;
    setText(el.autoStatus, manual ? t('mode_manual') : t(el.chkAutoRefresh.checked ? 'auto_active' : 'off'));
    setText(el.autoModeNote, manual ? t('auto_manual_hint') : t('auto_sync_hint'));
    if (manual) { state.nextSyncAt = null; setText(el.nextSyncTime, '–'); }
  }
  function renderReadiness() {
    if (!el.readiness) { return; }
    var title = '', note = '', label = '', action = null;
    if (state.rosterError) {
      title = t('readiness_error_title'); note = t('readiness_error_note'); label = t('readiness_error_action'); action = function () { loadRoster(true); };
    } else if (!state.rosterLoaded && !state.roster.length) {
      title = t('readiness_checking_title'); note = t('readiness_checking_note');
    } else if (!state.roster.length) {
      title = t('readiness_upload_title'); note = t('readiness_upload_note'); label = t('readiness_upload_action'); action = function () { openSettingsSection('roster', 'roster-file'); };
    } else if (!currentMeetingId()) {
      title = t('readiness_meeting_title'); note = t('readiness_meeting_note', { count: state.roster.length }); label = t('readiness_meeting_action'); action = function () { el.meetingId.focus(); };
    } else if (effectiveMode() === 'zoomApp' && (!state.zoomApp || !state.zoomApp.sessionActive)) {
      title = t('readiness_pair_title'); note = t('readiness_pair_note'); label = t('readiness_pair_action'); action = function () { openSettingsSection('pairing', 'btn-create-pairing-code'); };
    } else if (effectiveMode() === 'business' && !state.zoomConfigured) {
      title = t('readiness_api_title'); note = t('readiness_api_note'); label = t('readiness_api_action'); action = function () { openSettingsSection('mode', 'mode-auto'); };
    } else if (!state.board || (!state.board.lastReceivedAt && !state.board.latestEventAt)) {
      title = t('readiness_sync_title'); note = t('readiness_sync_note');
      label = effectiveMode() === 'manual' ? t('sync_paste_list') : t('readiness_sync_action'); action = function () { syncNow({ silent: false }); };
    }
    el.readiness.hidden = !title;
    setText(el.readinessTitle, title); setText(el.readinessNote, note);
    setText(el.btnNextStep, label); el.btnNextStep.hidden = !action;
    state.readinessAction = action;
  }
  function sourceLabel(source) {
    if (source === 'web-dashboard' || source === 'manual-snapshot') { return t('mode_manual'); }
    if (source === 'zoom-live-participants') { return t('source_participants'); }
    if (source === 'zoom-api') { return 'Zoom API'; }
    if (source === 'zoom-webhook' || source === 'zoom-webhook-raw') { return t('source_webhook'); }
    if (String(source).indexOf('zoom-app') >= 0 || String(source).indexOf('relay') >= 0) { return t('zoom_app'); }
    return source || t('source_saved');
  }
  function renderFreshness() {
    var board = state.board || {};
    var received = board.lastReceivedAt || null;
    var time = received ? new Date(received).getTime() : NaN;
    var valid = !isNaN(time);
    var age = valid ? Math.max(0, Math.floor((Date.now() - time) / 1000)) : 0;
    var sources = asArray(board.snapshotSources).map(function (item) { return sourceLabel(item.source); });
    sources = sources.filter(function (item, index) { return sources.indexOf(item) === index; });
    var source = sources.join(' + ') || getModeLabel(effectiveMode());
    if (valid && state.awaitingSnapshot && (!state.requestedAfterReceipt || time > new Date(state.requestedAfterReceipt).getTime())) { state.awaitingSnapshot = false; state.lastSyncOk = true; }
    state.lastSyncAt = valid ? new Date(received) : null;
    setText(el.lastSyncTime, valid ? formatTime(received) : '–');
    setText(el.summaryTime, valid ? t('freshness_details', { source: source, time: formatTime(received),
      age: age < 60 ? t('freshness_seconds', { count: age }) : t('freshness_minutes', { count: Math.floor(age / 60) }) })
      : board.latestEventAt ? t('freshness_latest', { time: formatDateTime(board.latestEventAt) }) : t('freshness_empty'));
    var automatic = effectiveMode() !== 'manual' && el.chkAutoRefresh.checked;
    var late = automatic && valid && age > Math.max(30, intervalSeconds() * 3);
    var status = state.lastSyncOk === false ? t('freshness_failed')
      : late ? t('freshness_late')
      : state.awaitingSnapshot ? t('freshness_requested')
      : valid ? t('freshness_received') : t('freshness_waiting');
    setText(el.sourceStatus, status + (state.lastAttemptAt ? t('freshness_attempt', { time: formatTime(state.lastAttemptAt) }) : '')
      + (board.generatedAt ? t('freshness_view', { time: formatTime(board.generatedAt) }) : ''));
    el.syncSummary.querySelector('.status-dot').className = 'status-dot ' + (state.lastSyncOk === false ? 'bad' : late || state.awaitingSnapshot ? 'warn' : valid ? 'ok' : '');
    setText(el.syncSummary.lastChild, status);
    setText(el.activityMode, t('activity_saved'));
  }

  function describeZoomAppRole(role) {
    if (role === 'host') { return t('role_host'); }
    if (role === 'coHost' || role === 'cohost' || role === 'co-host') { return t('role_cohost'); }
    return role ? String(role) : t('role_unknown');
  }

  function effectiveZoomAppHomeUrl() {
    if (state.relaySettings && state.relaySettings.configured && !state.relaySettings.restartRequired && state.relaySettings.baseUrl) {
      return state.relaySettings.baseUrl.replace(/\/+$/, '') + '/zoom-app/';
    }
    return (state.zoomApp && state.zoomApp.homeUrl) || '';
  }

  function pairingDisabledReason() {
    if (state.relaySettingsSaving) { return t('pairing_disabled_saving'); }
    if (!state.relaySettings) { return t('pairing_disabled_loading'); }
    if (state.relaySettings.restartRequired) { return t('pairing_disabled_restart'); }
    if (!effectiveZoomAppHomeUrl()) {
      return state.relaySettings.configured ? t('pairing_disabled_no_home') : t('pairing_disabled_no_relay');
    }
    return '';
  }

  function renderZoomAppStrip() {
    var app = state.zoomApp || {};
    var connected = !!app.connected;
    var sessionActive = app.sessionActive !== undefined ? !!app.sessionActive : connected;
    setDot(el.zoomAppDot, connected ? true : (sessionActive ? 'warn' : (state.connectionMode === 'zoomApp' ? false : null)));
    setText(el.zoomAppStatus, connected
      ? t('zoom_app_role_connected', { role: describeZoomAppRole(app.role) })
      : (sessionActive ? t('zoom_app_session_active') : t('zoom_app_disconnected')));
    if (connected) {
      setText(el.zoomAppSettingsStatus, t('zoom_app_connected_settings', {
        meetingId: app.meetingId || '–',
        role: describeZoomAppRole(app.role),
        time: formatTime(app.lastSeenAt)
      }));
      setText(el.pairingSession, t('zoom_app_connected_session', {
        meetingId: app.meetingId || '–',
        role: describeZoomAppRole(app.role)
      }));
    } else if (sessionActive) {
      setText(el.zoomAppSettingsStatus, t('zoom_app_active_settings', {
        meetingId: app.meetingId || '–',
        time: formatTime(app.lastSeenAt)
      }));
      setText(el.pairingSession, t('zoom_app_active_session', {
        meetingId: app.meetingId || '–'
      }));
    } else {
      setText(el.zoomAppSettingsStatus, t('zoom_app_waiting_status'));
      setText(el.pairingSession, t('pairing_session_default'));
    }
    var homeUrl = effectiveZoomAppHomeUrl();
    if (homeUrl) {
      setText(el.zoomAppHomeUrl, homeUrl);
    } else {
      setText(el.zoomAppHomeUrl, t('zoom_app_home_unset'));
    }
    if (el.btnCopyHomeUrl) { el.btnCopyHomeUrl.disabled = !homeUrl; }
    var disabledReason = pairingDisabledReason();
    if (el.btnCreatePairingCode) {
      el.btnCreatePairingCode.disabled = !!disabledReason;
      if (disabledReason) { el.btnCreatePairingCode.setAttribute('aria-describedby', 'pairing-disabled-reason'); }
      else { el.btnCreatePairingCode.removeAttribute('aria-describedby'); }
    }
    if (el.pairingDisabledReason) {
      el.pairingDisabledReason.hidden = !disabledReason;
      setText(el.pairingDisabledReason, disabledReason);
    }
    renderPairingCode();
  }

  function canCreatePairingCode() {
    return !pairingDisabledReason();
  }

  function formatRelayHealthMessage(result) {
    if (!result) { return ''; }
    if (result.checking) { return t('relay_checking'); }
    if (result.ok) {
      return t('relay_health_ok', { ms: result.latencyMs !== undefined && result.latencyMs !== null ? result.latencyMs : 0 });
    }
    var code = result.errorCode || '';
    if (code === 'not_configured') { return t('relay_health_not_configured'); }
    if (code === 'invalid_url') { return t('relay_health_invalid_url'); }
    if (code === 'timeout') { return t('relay_health_timeout'); }
    if (code === 'dns_error') { return t('relay_health_dns'); }
    if (code === 'tls_error') { return t('relay_health_tls'); }
    if (code === 'network_error') { return t('relay_health_network'); }
    if (code === 'http_error') {
      if (result.statusCode === 404) { return t('relay_health_http_404'); }
      if (result.statusCode === 502 || result.statusCode === 503 || result.statusCode === 504) {
        return t('relay_health_http_waking', { status: result.statusCode });
      }
      return t('relay_health_http', { status: result.statusCode || '?' });
    }
    if (code === 'invalid_response') { return t('relay_health_invalid_response'); }
    if (code === 'request_failed') { return t('relay_health_request_failed', { error: result.message || '' }); }
    return result.message || t('relay_health_network');
  }

  function renderRelayHealth() {
    if (el.btnCheckRelayHealth) {
      el.btnCheckRelayHealth.disabled = state.relayHealthChecking || state.relaySettingsSaving;
    }
    if (!el.relayHealthStatus) { return; }
    var result = state.relayHealth;
    if (!result) {
      el.relayHealthStatus.hidden = true;
      el.relayHealthStatus.classList.remove('is-ok', 'is-bad');
      setText(el.relayHealthStatus, '');
      return;
    }
    el.relayHealthStatus.hidden = false;
    el.relayHealthStatus.classList.toggle('is-ok', !!result.ok);
    el.relayHealthStatus.classList.toggle('is-bad', !result.checking && !result.ok);
    setText(el.relayHealthStatus, formatRelayHealthMessage(result));
  }

  function renderRelaySettings() {
    var settings = state.relaySettings;
    if (settings) {
      setText(el.relaySettingsNote, settings.restartRequired
        ? t('relay_note_restart')
        : (settings.configured ? t('relay_note_configured') : t('relay_note_unconfigured')));
    } else {
      setText(el.relaySettingsNote, t('relay_note_checking'));
    }
    if (el.btnSaveRelayUrl) { el.btnSaveRelayUrl.disabled = state.relaySettingsSaving; }
    renderRelayHealth();
    renderZoomAppStrip();
  }

  function applyRelaySettings(result) {
    var previous = state.relaySettings;
    var urlChanged = !!previous && previous.baseUrl !== result.baseUrl;
    state.relaySettings = result;
    if (result.restartRequired || urlChanged) {
      state.pairing = null;
      state.relayHealth = null;
      if (state.zoomApp && urlChanged) {
        var clearedHome = !result.configured && previous && previous.baseUrl
          && state.zoomApp.homeUrl === previous.baseUrl.replace(/\/+$/, '') + '/zoom-app/';
        state.zoomApp = Object.assign({}, state.zoomApp, {
          connected: false,
          sessionActive: false,
          pairingCodeExpiresAt: null,
          homeUrl: clearedHome ? '' : state.zoomApp.homeUrl
        });
      }
    }
    if (result.configured && !result.restartRequired && result.baseUrl) {
      state.zoomApp = Object.assign({}, state.zoomApp || {}, {
        homeUrl: result.baseUrl.replace(/\/+$/, '') + '/zoom-app/'
      });
    }
    if (el.relayBaseUrl) { el.relayBaseUrl.value = result.baseUrl || ''; }
    renderRelaySettings();
  }

  function loadRelaySettings() {
    if (state.relaySettingsSaving) { return Promise.resolve(null); }
    var revision = ++state.relaySettingsRevision;
    return request('/api/settings/zoom-relay').then(function (result) {
      if (revision === state.relaySettingsRevision) { applyRelaySettings(result); }
      return result;
    }, function (error) {
      if (revision === state.relaySettingsRevision) {
        setText(el.relaySettingsNote, t('relay_note_load_failed', { error: error.message }));
      }
      return null;
    });
  }

  function saveRelaySettings() {
    if (state.relaySettingsSaving) { return Promise.resolve(null); }
    var value = el.relayBaseUrl ? el.relayBaseUrl.value.trim() : '';
    var clearing = !value;
    if (!clearing && (value.toLowerCase().indexOf('https://') !== 0 || /\s/.test(value))) {
      toast('warn', t('relay_save_invalid_title'), t('relay_save_invalid_note'));
      if (el.relayBaseUrl) { el.relayBaseUrl.focus(); }
      return Promise.resolve(null);
    }
    ++state.relaySettingsRevision;
    state.relaySettingsSaving = true;
    renderRelaySettings();
    var busy = beginBusy(t('relay_save_busy'));
    return request('/api/settings/zoom-relay', { method: 'PUT', json: { baseUrl: value } }).then(function (result) {
      endBusy(busy);
      state.relaySettingsSaving = false;
      applyRelaySettings(result);
      if (clearing && !result.configured) {
        toast('ok', t('relay_clear_ok_title'), t('relay_clear_ok_note'));
      } else {
        toast('ok', t('relay_save_ok_title'), result.restartRequired
          ? t('relay_note_restart')
          : t('relay_save_ok_note'));
      }
      return result;
    }, function (error) {
      endBusy(busy);
      state.relaySettingsSaving = false;
      renderRelaySettings();
      toast('bad', t('relay_save_fail_title'), error.message);
      return null;
    });
  }

  function checkRelayHealth() {
    if (state.relayHealthChecking || state.relaySettingsSaving) { return Promise.resolve(null); }
    var value = (el.relayBaseUrl && el.relayBaseUrl.value ? el.relayBaseUrl.value.trim() : '')
      || ((state.relaySettings && state.relaySettings.baseUrl) || '').trim();
    if (!value) {
      state.relayHealth = { ok: false, errorCode: 'not_configured' };
      renderRelayHealth();
      if (el.relayBaseUrl) { el.relayBaseUrl.focus(); }
      return Promise.resolve(null);
    }
    if (value.toLowerCase().indexOf('https://') !== 0 || /\s/.test(value)) {
      state.relayHealth = { ok: false, errorCode: 'invalid_url' };
      renderRelayHealth();
      if (el.relayBaseUrl) { el.relayBaseUrl.focus(); }
      return Promise.resolve(null);
    }
    var revision = ++state.relayHealthRevision;
    state.relayHealthChecking = true;
    state.relayHealth = { checking: true };
    renderRelayHealth();
    return request('/api/settings/zoom-relay/health', { method: 'POST', json: { baseUrl: value } }).then(function (result) {
      if (revision !== state.relayHealthRevision) { return null; }
      state.relayHealthChecking = false;
      state.relayHealth = result || { ok: false, errorCode: 'invalid_response' };
      renderRelayHealth();
      logSession(state.relayHealth.ok ? 'ok' : 'bad', t('btn_check_relay'), formatRelayHealthMessage(state.relayHealth));
      return state.relayHealth;
    }, function (error) {
      if (revision !== state.relayHealthRevision) { return null; }
      state.relayHealthChecking = false;
      state.relayHealth = { ok: false, errorCode: 'request_failed', message: error.message };
      renderRelayHealth();
      logSession('bad', t('btn_check_relay'), formatRelayHealthMessage(state.relayHealth));
      return state.relayHealth;
    });
  }

  function renderPairingCode() {
    if (el.btnCopyPairingCode) { el.btnCopyPairingCode.disabled = !(state.pairing && state.pairing.code); }
    if (state.relaySettings && state.relaySettings.restartRequired) {
      setText(el.pairingCode, '– – – – – –');
      setText(el.pairingExpiry, t('pairing_disabled_restart'));
      return;
    }
    var app = state.zoomApp || {};
    var sessionActive = app.sessionActive !== undefined ? !!app.sessionActive : !!app.connected;
    if (sessionActive) {
      setText(el.pairingCode, t('pairing_code_active'));
      setText(el.pairingExpiry, t('pairing_expiry_active'));
      return;
    }
    if (!state.pairing) {
      setText(el.pairingCode, '– – – – – –');
      setText(el.pairingExpiry, t('pairing_expiry_default'));
      return;
    }
    setText(el.pairingCode, String(state.pairing.code || '').split('').join(' '));
    var expiresAt = state.pairing.expiresAt ? new Date(state.pairing.expiresAt) : null;
    if (!expiresAt || isNaN(expiresAt.getTime())) {
      setText(el.pairingExpiry, t('pairing_expiry_unknown'));
      return;
    }
    var remaining = Math.round((expiresAt.getTime() - Date.now()) / 1000);
    if (remaining <= 0) {
      setText(el.pairingExpiry, t('pairing_expiry_expired'));
      return;
    }
    setText(el.pairingExpiry, t('pairing_expiry_remaining', {
      time: formatTime(expiresAt),
      min: Math.floor(remaining / 60),
      sec: pad(remaining % 60)
    }));
  }

  function renderZoomApiStatus() {
    if (el.zoomStatus) {
      if (state.zoomApiStatus === 'ok') { setText(el.zoomStatus, t('connected')); }
      else if (state.zoomApiStatus === 'configured') { setText(el.zoomStatus, t('status_configured')); }
      else if (state.zoomApiStatus === 'unconfigured') { setText(el.zoomStatus, t('status_unconfigured')); }
      else if (state.zoomApiStatus === 'error') { setText(el.zoomStatus, t('status_check_failed')); }
      else { setText(el.zoomStatus, t('checking')); }
    }
    if (el.apiSettingsStatus) {
      if (state.zoomApiStatus === 'error') { setText(el.apiSettingsStatus, state.zoomApiError || t('status_check_failed')); }
      else if (state.zoomApiStatus === 'configured' || state.zoomApiStatus === 'ok') { setText(el.apiSettingsStatus, t('api_settings_configured')); }
      else if (state.zoomApiStatus === 'unconfigured') { setText(el.apiSettingsStatus, t('api_settings_unconfigured')); }
      else { setText(el.apiSettingsStatus, t('api_settings_checking')); }
    }
  }

  function checkZoomConnection(showToast) {
    return request('/api/zoom/connection-status').then(function (status) {
      var payload = status || {};
      var business = payload.business || {};
      /* configured/business.configured 둘 다 지원해 기존 응답과 호환한다. */
      state.zoomConfigured = business.configured !== undefined ? !!business.configured : !!payload.configured;
      state.zoomApiStatus = state.zoomConfigured ? 'configured' : 'unconfigured';
      state.zoomApiError = '';
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
      } else if (canCreatePairingCode() && state.zoomApp.pairingCodeExpiresAt && !state.pairing) {
        state.pairing = { code: null, expiresAt: state.zoomApp.pairingCodeExpiresAt };
      }
      renderZoomApiDot();
      renderZoomApiStatus();
      renderZoomAppStrip();
      renderModeNote();
      if (showToast) {
        toast(state.zoomConfigured ? 'ok' : 'warn', state.zoomConfigured ? 'Zoom API 설정됨' : 'Zoom API 설정 필요',
          state.zoomConfigured ? '실제 권한은 지금 동기화로 확인합니다.' : 'Server-to-Server OAuth 자격증명이 필요합니다.');
      }
      return payload;
    }, function (error) {
      state.zoomConfigured = false;
      state.zoomApiStatus = 'error';
      state.zoomApiError = error.message;
      setDot(el.zoomDot, false);
      renderZoomApiStatus();
      if (showToast) { toast('bad', 'Zoom API 상태 확인 실패', error.message); }
    });
  }

  function pollConnectionStatus() {
    if (document.hidden || state.connectionPollInFlight) { return null; }
    state.connectionPollInFlight = true;
    return checkZoomConnection(false).then(function () {
      state.connectionPollInFlight = false;
      if (state.pending === 0 && !document.hidden && state.zoomApp && (state.zoomApp.sessionActive || state.zoomApp.connected) && currentMeetingId()) {
        refreshBoard({ silent: true });
      }
    }, function () {
      state.connectionPollInFlight = false;
    });
  }

  function createPairingCode() {
    if (!canCreatePairingCode()) {
      toast('warn', '페어링 준비가 필요합니다', pairingDisabledReason());
      if (el.relayBaseUrl) { el.relayBaseUrl.focus(); }
      return Promise.resolve(null);
    }
    var revision = state.relaySettingsRevision;
    var busy = beginBusy('페어링 코드를 만드는 중…');
    return request('/api/zoom-app/pairing-code', { method: 'POST', json: {} }).then(function (result) {
      endBusy(busy);
      if (revision !== state.relaySettingsRevision || !canCreatePairingCode()) { return null; }
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
      showAlert('warn', '연결된 회의가 다릅니다', '선택한 회의 ID와 Zoom 앱의 회의 ID를 확인하세요.', t('sync_check_meeting'), function () { el.meetingId.focus(); });
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

  function renderRosterNote() {
    if (!state.rosterLoaded) {
      setText(el.rosterNote, t('roster_status_checking'));
    } else if (state.rosterError) {
      setText(el.rosterNote, t('roster_error', { error: state.rosterError.message }));
    } else {
      setText(el.rosterNote, state.roster.length ? t('roster_loaded', { count: state.roster.length }) : t('roster_load_empty'));
    }
  }

  function loadRoster(showToast) {
    return request('/api/roster').then(function (people) {
      state.rosterLoaded = true; state.rosterError = false;
      state.roster = asArray(people);
      setText(el.rosterSummary, t('roster_count', { count: state.roster.length }));
      renderRosterNote();
      if (showToast) { toast('ok', t('roster_checked'), state.roster.length + '명'); }
      if (state.board) { renderBoard(state.board); }
      else { state.groups = collectGroups(null); renderGroupOptions(); renderParticipants(); }
      renderReadiness();
      return state.roster;
    }, function (error) {
      state.rosterLoaded = true; state.rosterError = error; renderReadiness();
      renderRosterNote();
      if (showToast) { toast('bad', t('roster_load_failed'), error.message); }
      return [];
    });
  }

  var ROSTER_ERROR_KEY_BY_CODE = {
    ROSTER_FILE_REQUIRED: 'roster_err_file_required',
    ROSTER_UNSUPPORTED_EXTENSION: 'roster_err_unsupported_extension',
    ROSTER_FILE_TOO_LARGE: 'roster_err_file_too_large',
    ROSTER_NOT_EXCEL: 'roster_err_not_excel',
    ROSTER_PASSWORD_PROTECTED: 'roster_err_password_protected',
    ROSTER_EMPTY_SHEET: 'roster_err_empty_sheet',
    ROSTER_MISSING_HEADERS: 'roster_err_missing_headers',
    ROSTER_NO_ROWS: 'roster_err_no_rows',
    ROSTER_READ_FAILED: 'roster_err_read_failed',
    ROSTER_INVALID: 'roster_err_server'
  };
  var MAX_ROSTER_DUPLICATE_ITEMS = 10;

  function resolveRosterErrorKey(problem, status) {
    if (status === 0) { return 'roster_err_offline'; }
    var key = problem && typeof problem.messageKey === 'string' ? problem.messageKey : '';
    if (key && t(key + '_title') !== key + '_title') { return key; }
    var code = problem && typeof problem.errorCode === 'string' ? problem.errorCode : '';
    if (code && ROSTER_ERROR_KEY_BY_CODE[code]) { return ROSTER_ERROR_KEY_BY_CODE[code]; }
    if (status === 413) { return 'roster_err_file_too_large'; }
    return 'roster_err_server';
  }

  function formatRosterDuplicateItem(item) {
    var sequence = String(item && item.sequence !== undefined ? item.sequence : '').trim();
    var names = asArray(item && item.names).filter(Boolean).join(', ');
    var personKey = /^\d+$/.test(sequence) ? 'roster_import_dup_person_numeric' : 'roster_import_dup_person_other';
    var person = t(personKey, { number: sequence, names: names }).trim();
    var rows = asArray(item && item.rowNumbers).join(', ');
    return t('roster_import_dup_item', { count: item.count, person: person, rows: rows });
  }

  function buildRosterFeedbackContent(feedback) {
    if (!feedback) { return null; }
    if (feedback.kind === 'bad') {
      var problem = feedback.problem || null;
      var key = feedback.messageKey || resolveRosterErrorKey(problem, feedback.status);
      var title = t(key + '_title');
      var message = t(key + '_message');
      var hint = t(key + '_hint');
      if (hint === key + '_hint') { hint = ''; }
      var items = [];
      if (key === 'roster_err_missing_headers') {
        var missing = asArray(problem && problem.missingColumns);
        if (missing.length === 1 && missing[0] === 'name') {
          message = t('roster_err_missing_headers_name');
        } else if (missing.length === 1 && missing[0] === 'sequence') {
          message = t('roster_err_missing_headers_sequence');
        }
        var found = asArray(problem && problem.foundHeaders).filter(Boolean);
        if (found.length) {
          items.push(t('roster_err_found_headers', { headers: found.join(', ') }));
        }
      }
      return { kind: 'bad', title: title, message: message, hint: hint, items: items };
    }
    if (feedback.kind === 'warn') {
      var duplicates = asArray(feedback.duplicateNumbers);
      var shown = duplicates.slice(0, MAX_ROSTER_DUPLICATE_ITEMS).map(formatRosterDuplicateItem);
      if (duplicates.length > MAX_ROSTER_DUPLICATE_ITEMS) {
        shown.push(t('roster_import_dup_more', { count: duplicates.length - MAX_ROSTER_DUPLICATE_ITEMS }));
      }
      return {
        kind: 'warn',
        title: t('roster_import_dup_title', { count: feedback.count, groups: duplicates.length }),
        message: t('roster_import_dup_message'),
        hint: '',
        items: shown
      };
    }
    return {
      kind: 'ok',
      title: t('roster_import_ok_title'),
      message: feedback.fileName
        ? t('roster_import_ok_message', { file: feedback.fileName, count: feedback.count })
        : t('roster_import_ok_message_nofile', { count: feedback.count }),
      hint: '',
      items: []
    };
  }

  function renderRosterFeedback() {
    if (!el.rosterFeedback) { return; }
    var content = buildRosterFeedbackContent(state.rosterFeedback);
    if (!content) {
      el.rosterFeedback.hidden = true;
      el.rosterFeedback.className = 'roster-feedback';
      setText(el.rosterFeedbackTitle, '');
      setText(el.rosterFeedbackMessage, '');
      if (el.rosterFeedbackHint) { el.rosterFeedbackHint.hidden = true; setText(el.rosterFeedbackHint, ''); }
      if (el.rosterFeedbackList) { el.rosterFeedbackList.hidden = true; el.rosterFeedbackList.textContent = ''; }
      return;
    }
    el.rosterFeedback.hidden = false;
    el.rosterFeedback.className = 'roster-feedback is-' + content.kind;
    el.rosterFeedback.setAttribute('role', content.kind === 'bad' ? 'alert' : 'status');
    el.rosterFeedback.setAttribute('aria-live', content.kind === 'bad' ? 'assertive' : 'polite');
    setText(el.rosterFeedbackTitle, content.title);
    setText(el.rosterFeedbackMessage, content.message);
    if (el.rosterFeedbackHint) {
      el.rosterFeedbackHint.hidden = !content.hint;
      setText(el.rosterFeedbackHint, content.hint || '');
    }
    if (el.rosterFeedbackList) {
      el.rosterFeedbackList.textContent = '';
      el.rosterFeedbackList.hidden = content.items.length === 0;
      content.items.forEach(function (text) {
        var li = document.createElement('li');
        li.textContent = text;
        el.rosterFeedbackList.appendChild(li);
      });
    }
  }

  function setRosterFeedback(feedback, options) {
    state.rosterFeedback = feedback;
    renderRosterFeedback();
    var content = buildRosterFeedbackContent(feedback);
    if (content && (!options || options.log !== false)) {
      var detail = [content.message, content.items.join(' · '), content.hint].filter(Boolean).join(' ');
      logSession(content.kind, content.title, detail);
    }
  }

  function uploadRoster() {
    var file = el.rosterFile.files && el.rosterFile.files[0];
    if (!file || file.size === 0) {
      setRosterFeedback({ kind: 'bad', messageKey: 'roster_err_file_required' });
      return Promise.resolve(null);
    }
    var lower = String(file.name || '').toLowerCase();
    if (lower.slice(-5) !== '.xlsx' && lower.slice(-4) !== '.xls') {
      setRosterFeedback({ kind: 'bad', messageKey: 'roster_err_unsupported_extension' });
      return Promise.resolve(null);
    }
    if (file.size > MAX_ROSTER_BYTES) {
      setRosterFeedback({ kind: 'bad', messageKey: 'roster_err_file_too_large' });
      return Promise.resolve(null);
    }
    var form = new FormData();
    form.append('file', file, file.name);
    var busy = beginBusy(t('roster_uploading'));
    return request('/api/roster/upload', { method: 'POST', headers: { Accept: 'application/json' }, body: form }).then(function (result) {
      endBusy(busy);
      el.rosterFile.value = '';
      var count = result && result.count !== undefined ? result.count : 0;
      var fileName = (result && result.displayName) || file.name;
      var duplicateNumbers = asArray(result && result.warnings && result.warnings.duplicateNumbers);
      if (duplicateNumbers.length) {
        setRosterFeedback({ kind: 'warn', count: count, fileName: fileName, duplicateNumbers: duplicateNumbers });
      } else {
        setRosterFeedback({ kind: 'ok', count: count, fileName: fileName });
      }
      return Promise.all([loadRoster(false), refreshBoard({ silent: true })]).then(function () { return result; });
    }, function (error) {
      endBusy(busy);
      setRosterFeedback({
        kind: 'bad',
        status: error ? error.status : 0,
        problem: error && error.problem ? error.problem : null
      });
      return null;
    });
  }

  function requireMeetingId() {
    var meetingId = currentMeetingId();
    if (!meetingId) {
      el.meetingId.focus();
      toast('warn', t('meeting_id_required'), t('meeting_id_required_note'));
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
    var message = t('sync_error_default');
    var label = t('retry'), action = function () { syncNow({ silent: false }); };
    if (error.status === 403 || error.status === 401) {
      message = t('sync_error_permission'); label = t('sync_check_permissions');
      action = function () { openSettingsSection(mode === 'zoomApp' ? 'pairing' : 'api'); };
    } else if (error.status === 404) {
      message = t('sync_error_meeting'); label = t('sync_check_meeting'); action = function () { el.meetingId.focus(); };
    } else if (error.status === 429) {
      message = t('sync_error_interval'); label = t('sync_set_interval'); action = function () { openSettingsSection('auto', 'autorefresh-interval'); };
    } else if (error.status === 409 && mode === 'zoomApp') {
      message = t('sync_error_zoom_app'); label = t('sync_check_connection'); action = function () { openSettingsSection('pairing'); };
    } else if (error.status === 503 && mode === 'business' && !state.zoomConfigured) {
      message = t('sync_error_api'); label = t('readiness_api_action'); action = function () { openSettingsSection('mode'); };
    }
    showAlert('warn', t('sync_receive_failed'), message, label, action);
    if (el.alertDetails) { el.alertDetails.hidden = false; setText(el.alertTechnical, error.message); }
  }

  function syncZoomParticipants(options) {
    var opts = options || {};
    var meetingId = opts.silent ? currentMeetingId() : requireMeetingId();
    if (!meetingId) { return Promise.resolve(null); }
    var path = '/api/zoom/meetings/' + encodeURIComponent(meetingId) + '/sync';
    if (opts.allowEmpty) { path += '?allowEmptySnapshot=true'; }
    var context = beginMeetingRequest(opts.silent ? null : t('sync_in_progress'));
    return request(path, { method: 'POST' }).then(function (result) {
      if (!finishMeetingRequest(context)) { return null; }
      hideAlert();
      state.zoomConfigured = true;
      state.zoomApiStatus = 'ok';
      setDot(el.zoomDot, true);
      renderZoomApiStatus();
      if (result && result.snapshot && result.snapshot.board) { renderBoard(result.snapshot.board); }
      markSync(true, ' 참가자 ' + (result && result.activeParticipants !== undefined ? result.activeParticipants : '–') + '명');
      scheduleNextSync();
      logSession('ok', t('sync_zoom'), '현재 ' + (result && result.activeParticipants !== undefined ? result.activeParticipants : '–') + '명');
      if (!opts.silent) { toast('ok', t('sync_zoom_complete'), t('sync_current_participants', { count: result.activeParticipants })); }
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
      logSession('bad', t('sync_zoom_failed'), error.message);
      if (!opts.silent) { toast('bad', t('sync_zoom_failed'), error.message); }
      return null;
    });
  }

  function refreshBoard(options) {
    var opts = options || {};
    var meetingId = opts.silent ? currentMeetingId() : requireMeetingId();
    if (!meetingId) { return Promise.resolve(null); }
    var context = beginMeetingRequest(opts.silent ? null : t('board_loading'));
    return request('/api/meetings/' + encodeURIComponent(meetingId) + '/board').then(function (board) {
      if (!finishMeetingRequest(context)) { return null; }
      renderBoard(board);
      if (opts.notify) { toast('ok', t('board_refreshed'), t('board_refreshed_note')); }
      return board;
    }, function (error) {
      if (!finishMeetingRequest(context)) { return null; }
      if (!opts.silent) { toast('bad', t('board_refresh_failed'), error.message); }
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
        evidenceToken: item.evidenceToken, matchReason: item.matchReason || '',
        needsReview: !!item.needsReview, reviewReasonCode: item.reviewReasonCode || ''
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
          personName: group.personName || group.rosterPersonName || group.name || (rosterMap[personId] && rosterMap[personId].name) || t('metric_review'),
          connections: groupConnections,
          reason: group.reason || t('duplicate_multiple_connections')
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
        personName: rosterMap[personId] ? rosterMap[personId].name : t('metric_review'),
        connections: byPerson[personId],
        reason: t('duplicate_multiple_connections')
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
        identityReviewReason: person.identityReviewReason || '',
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
    all.textContent = t('all_groups');
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

  /* 그룹 선택 시 명단 기준 집계에 맞춰 소속이 없는 미매칭은 제외한다. */
  function rowInSelectedGroup(row) {
    if (!state.group) { return true; }
    if (row.kind === 'unmatched') { return false; }
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
      return (!state.group && !connection.rosterPersonId) || allowed[connection.rosterPersonId];
    }).length;
  }
  /* 활동 피드도 선택 그룹 인물만 남긴다. */
  function scopedEvents(events, scoped) {
    if (!state.group) { return events; }
    var allowedPeople = {};
    scoped.forEach(function (row) {
      if (row.rosterPersonId) { allowedPeople[row.rosterPersonId] = true; }
    });
    return asArray(events).filter(function (event) {
      var personId = event.matchedRosterPersonId || event.rosterPersonId || null;
      return !!personId && !!allowedPeople[personId];
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
      toast('ok', t('group_changed'), state.group ? t('group_show', { group: state.group }) : t('group_show_all'));
    }
  }

  /* 지표, 표 머리글, 현황 복사 글이 같은 집계를 쓰도록 한 곳에서 계산한다. */
  function scopedSummary() {
    var allScoped = scopedRows();
    var scoped = allScoped.filter(function (row) { return !row.isExcluded; });
    var rosterScoped = scoped.filter(function (row) { return row.kind === 'roster'; });
    return {
      allScoped: allScoped,
      scoped: scoped,
      excluded: allScoped.filter(function (row) { return row.isExcluded; }),
      rosterScoped: rosterScoped,
      present: rosterScoped.filter(function (row) { return row.attendanceState === 'Present'; }),
      absent: rosterScoped.filter(function (row) { return row.attendanceState !== 'Present'; }),
      duplicates: scopedDuplicates(rosterScoped)
    };
  }

  function renderDesktopSummary(total, present) {
    setText(el.summaryTotal, total);
    setText(el.summaryPresent, present);
    if (!el.desktopSummary) { return; }
    var template = t('summary_progress');
    if (el.desktopSummary.replaceChildren && el.summaryTotal && el.summaryPresent && typeof document.createTextNode === 'function') {
      var parts = template.split(/(\{total\}|\{present\})/);
      var nodes = [];
      parts.forEach(function (part) {
        if (!part) { return; }
        if (part === '{total}') { nodes.push(el.summaryTotal); }
        else if (part === '{present}') { nodes.push(el.summaryPresent); }
        else { nodes.push(document.createTextNode(part)); }
      });
      el.desktopSummary.replaceChildren.apply(el.desktopSummary, nodes);
    } else {
      setText(el.desktopSummary, t('summary_progress', { total: total, present: present }));
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

    var summary = scopedSummary();
    var allScoped = summary.allScoped;
    var excluded = summary.excluded;
    var scoped = summary.scoped;
    var rosterScoped = summary.rosterScoped;
    var present = summary.present;
    var absent = summary.absent;
    var review = scoped.filter(function (row) { return row.review; });
    var unmatched = scoped.filter(function (row) { return row.kind === 'unmatched'; });
    var globalUnmatchedCount = state.rows.filter(function (row) { return !row.isExcluded && row.kind === 'unmatched'; }).length;
    var duplicates = summary.duplicates;
    var connectionCount = scopedConnectionCount(rosterScoped);
    var total = rosterScoped.length;
    var rate = total ? Math.round((present.length / total) * 100) : 0;

    resetInvalidSelection(allScoped);

    renderDesktopSummary(total, present.length);
    setText(el.phoneSummary, t('phone_summary', { present: present.length, total: total, absent: absent.length }));
    setText(el.summaryRate, total ? rate + '%' : '–');
    el.rateRing.style.setProperty('--rate', total ? rate : 0);
    setText(el.metricPresent, t('count_people', { count: present.length }));
    setText(el.metricAbsent, t('count_people', { count: absent.length }));
    setText(el.metricReview, t('count_items', { count: review.length }));
    if (el.btnReview) { el.btnReview.disabled = review.length === 0; }
    setText(el.metricDuplicate, t('count_people', { count: duplicates.length }));
    renderFreshness();
    renderReadiness();
    setText(el.participantsMeta, t('participants_totals', { present: present.length,
      total: total, unmatched: unmatched.length, connections: connectionCount }));
    var scopeSummary = state.group
      ? state.group + ' · ' + (globalUnmatchedCount > 0
        ? t('scope_exclusions_group', { unmatched: globalUnmatchedCount, count: excluded.length })
        : t('scope_exclusions_only', { count: excluded.length }))
      : t('scope_exclusions', { count: excluded.length });
    setText(el.scopeNote, scopeSummary + (board.attendanceDate ? ' · ' + board.attendanceDate : ''));
    setText(el.btnExport, t('export_group_label', { group: state.group || t('group_all_scope'), count: total }));
    setText(el.exportScope, t('export_group_scope', { group: state.group || t('group_all_scope') }));
    el.btnExport.disabled = !currentMeetingId() || total === 0;
    el.btnCopyStatus.disabled = el.btnExport.disabled;

    setText(el.countAll, rosterScoped.length);
    setText(el.countPresent, present.length);
    setText(el.countAbsent, absent.length);
    setText(el.countReview, review.length);
    setText(el.countUnmatched, unmatched.length);
    setText(el.countExcluded, excluded.length);
    setText(el.rosterSummary, state.group ? t('group_count', { group: state.group, count: total }) : t('roster_count', { count: total }));

    renderParticipants();
    renderActivity(scopedEvents(board.recentEvents, scoped), duplicates);
    renderDuplicates(duplicates);
  }

  function rowMatchesFilter(row) {
    if (state.filter === 'excluded') { return !!row.isExcluded; }
    if (row.isExcluded) { return false; }
    if (state.filter === 'all') { return row.kind === 'roster'; }
    if (state.filter === 'present') { return row.kind === 'roster' && row.attendanceState === 'Present'; }
    if (state.filter === 'absent') { return row.kind === 'roster' && row.attendanceState !== 'Present'; }
    if (state.filter === 'review') { return row.review; }
    if (state.filter === 'unmatched') { return row.kind === 'unmatched'; }
    return true;
  }
  function rowMatchesSearch(row) {
    var query = normalizeSearch(el.participantSearch.value);
    if (!query) { return true; }
    var connectionNames = row.connections.map(function (item) { return item.rawName + ' ' + item.canonicalName + ' ' + (item.email || ''); }).join(' ');
    var phoneDigits = digitsOnly(row.phone);
    var haystack = [row.name, row.email, row.organization, row.group, row.phone, phoneDigits, connectionNames].join(' ');
    if (normalizeSearch(haystack).indexOf(query) >= 0) { return true; }
    var queryDigits = digitsOnly(query);
    return queryDigits.length >= 3 && /^[0-9\s\-().+]+$/.test(query) && phoneDigits.indexOf(queryDigits) >= 0;
  }

  /* 번호는 숫자 순서(2 다음 10), 번호가 없는 행(미매칭 등)은 기존 순서대로 맨 뒤에 둔다. */
  function sortRows(rows, key) {
    var collator = new Intl.Collator(state.lang, { numeric: true, sensitivity: 'base' });
    function bySequence(left, right) {
      var a = String(left.sequence || '').trim();
      var b = String(right.sequence || '').trim();
      if (!a || !b) { return (a ? 0 : 1) - (b ? 0 : 1); }
      return collator.compare(a, b);
    }
    function byName(left, right) { return collator.compare(left.name || '', right.name || ''); }
    var compare = (key || state.sort) === 'name'
      ? function (left, right) { return byName(left, right) || bySequence(left, right); }
      : bySequence;
    return rows.slice().sort(compare);
  }
  function applySort(key) {
    state.sort = SORT_KEYS.indexOf(key) >= 0 ? key : 'number';
    writeStore(STORAGE.sort, state.sort);
    el.participantTableWrap.scrollTop = 0;
    syncSortButtons(); renderParticipants();
  }
  function syncSortButtons() {
    el.sortButtons.forEach(function (button) {
      var header = button.closest('th');
      if (button.dataset.sort === state.sort) { header.setAttribute('aria-sort', 'ascending'); }
      else { header.removeAttribute('aria-sort'); }
    });
  }

  function statusInfo(row) {
    if (row.isExcluded) { return { label: t('status_Excluded') !== 'status_Excluded' ? t('status_Excluded') : '오늘 제외', className: 'neutral' }; }
    if (row.kind === 'unmatched') { return { label: t('status_Unmatched') !== 'status_Unmatched' ? t('status_Unmatched') : '미매칭', className: 'unmatched' }; }
    if (row.attendanceState === 'Present') { return { label: t('status_Present') !== 'status_Present' ? t('status_Present') : '참석 중', className: 'present' }; }
    if (row.attendanceState === 'Left') { return { label: t('status_Left') !== 'status_Left' ? t('status_Left') : '퇴장', className: 'left' }; }
    return { label: t('status_Absent') !== 'status_Absent' ? t('status_Absent') : '미참석', className: 'absent' };
  }
  function confidenceInfo(row) {
    if (row.kind === 'roster' && row.attendanceState === 'NotJoined') {
      return { label: t('status_NotJoined') !== 'status_NotJoined' ? t('status_NotJoined') : '입장 전', className: 'neutral' };
    }
    if (row.kind === 'unmatched') {
      return { label: t('status_Unmatched') !== 'status_Unmatched' ? t('status_Unmatched') : '미매칭', className: 'unmatched' };
    }
    var needsIdentityAttention = row.identityReviewStatus && row.identityReviewStatus !== 'none';
    if (row.confidence === 'Verified' || row.confidence === 'AliasVerified') {
      return { label: getConfidenceLabel(row.confidence), className: needsIdentityAttention ? 'review' : 'verified' };
    }
    if (row.confidence === 'NameOnly') {
      return { label: getConfidenceLabel(row.confidence), className: needsIdentityAttention ? 'review' : 'neutral' };
    }
    return { label: getConfidenceLabel(row.confidence), className: row.confidence === 'Unmatched' ? 'neutral' : 'review' };
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
    var rows = sortRows(scopedRows().filter(rowMatchesFilter).filter(rowMatchesSearch));
    el.participantRows.textContent = '';
    el.participantsEmpty.hidden = rows.length > 0;
    setText(el.visibleCount, t('visible_count', { count: rows.length }));
    var globalUnmatchedCount = state.rows.filter(function (row) { return !row.isExcluded && row.kind === 'unmatched'; }).length;
    var unmatchedHiddenByGroup = state.filter === 'unmatched' && !!state.group && globalUnmatchedCount > 0;
    var emptyTitleKey = 'empty_participants_title';
    var emptyNoteText = t('empty_filtered_note');
    if (state.filter === 'excluded') {
      emptyTitleKey = 'empty_excluded_title';
      emptyNoteText = t('empty_excluded_note');
    } else if (unmatchedHiddenByGroup) {
      emptyTitleKey = 'empty_unmatched_group_title';
      emptyNoteText = t('empty_unmatched_group_note', { count: globalUnmatchedCount });
    } else if (state.filter === 'review') {
      emptyTitleKey = 'empty_review_title';
    } else if (!state.rows.length) {
      if (state.rosterLoaded && !state.roster.length) {
        emptyTitleKey = 'empty_no_roster_title';
        emptyNoteText = t('empty_no_roster_note');
      } else if (state.roster.length && !currentMeetingId()) {
        emptyTitleKey = 'empty_no_meeting_title';
        emptyNoteText = t('empty_no_meeting_note', { count: state.roster.length });
      } else {
        emptyNoteText = t('empty_participants_note');
      }
    }
    setText(el.emptyTitle, t(emptyTitleKey));
    setText(el.emptyNote, emptyNoteText);
    if (el.btnResetFilters) { el.btnResetFilters.hidden = !state.group && !el.participantSearch.value && state.filter === 'all'; }

    rows.forEach(function (row) {
      var tr = document.createElement('tr');
      tr.className = 'participant-row' + (row.key === state.selectedKey ? ' is-selected' : '') + (row.kind === 'unmatched' ? ' is-unmatched' : '');
      tr.dataset.key = row.key;
      appendTextCell(tr, row.sequence, 'participant-number');
      appendPillCell(tr, statusInfo(row));
      var nameCell = document.createElement('td');
      var name = document.createElement('button');
      name.type = 'button';
      name.className = 'participant-name row-toggle';
      name.textContent = (row.key === state.selectedKey ? '− ' : '+ ') + row.name;
      name.dataset.focusKey = 'row:' + row.key;
      name.setAttribute('aria-expanded', row.key === state.selectedKey ? 'true' : 'false');
      name.setAttribute('aria-label', t('participant_row_details', { name: row.name, action: t(row.key === state.selectedKey ? 'collapse' : 'expand') }));
      name.addEventListener('click', function (event) { event.stopPropagation(); toggleParticipant(row.key); });
      var secondary = document.createElement('span');
      secondary.className = 'participant-secondary';
      secondary.textContent = row.email || (row.kind === 'unmatched' ? t('participant_display_name') : t('participant_no_email'));
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
      appendTextCell(tr, row.attendanceState === 'Left' ? t('participant_left_at', { time: formatTime(row.lastLeftAt) }) : (row.lastJoinedAt ? t('participant_joined_at', { time: formatTime(row.lastJoinedAt) }) : '–'));
      var connectionCell = document.createElement('td');
      var connectionPill = document.createElement('span');
      connectionPill.className = 'connection-pill' + (row.duplicate ? ' duplicate' : '');
      connectionPill.textContent = row.connections.length ? t('connection_count', { count: row.connections.length }) : '–';
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
    var label = kind === 'identity' ? t('review_identity') : t('review_devices');
    return mutateMeeting(scope || meetingScope(), '/people/' + encodeURIComponent(row.rosterPersonId) + '/review',
      { kind: kind, status: status, expectedEvidenceToken: token }, personLabel(row) + '\n' + label + ' 검토를 '
      + (status === 'confirmed' ? t('review_confirm') : status === 'deferred' ? t('review_defer') : '미확인으로 되돌림') + ' 처리할까요?\n실제 매칭 근거와 입퇴장 상태는 바뀌지 않습니다.', label + ' 검토를 저장했습니다');
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
    if (row.isExcluded) { return t('review_excluded_reason'); }
    if (row.attendanceState === 'NotJoined') { return t('review_not_joined'); }
    var reasonByCode = {
      duplicate_roster_name: 'review_duplicate_roster_name',
      email_conflict: 'review_email_conflict',
      identifier_conflict: 'review_identifier_conflict',
      duplicate_roster_email: 'review_duplicate_roster_email',
      ambiguous_possible_match: 'review_ambiguous_possible_match',
      blank_name: 'review_blank_name'
    };
    var codeKey = row.identityReviewReason ? reasonByCode[row.identityReviewReason] : null;
    var reason = codeKey ? t(codeKey)
      : row.confidence === 'NameOnly' ? (row.identityReviewStatus && row.identityReviewStatus !== 'none'
        ? t('review_name_only') : t('review_exact_name_match'))
      : row.confidence === 'Possible' ? t('review_possible_match')
      : row.confidence === 'AliasVerified' ? t('review_alias_match')
      : row.confidence === 'Verified' ? (row.connections.some(function (connection) { return connection.manualMatch; })
        ? t('review_manual_match') : t('review_email_match'))
      : t('review_no_match');
    if (row.duplicate) { reason += t('review_duplicate_reason'); }
    return reason;
  }
  function appendPersonActions(parent, row, scope) {
    var section = document.createElement('section'); section.className = 'review-actions';
    var heading = document.createElement('h3'); heading.textContent = t('review_this_meeting'); section.appendChild(heading);
    var reason = document.createElement('p'); reason.textContent = reviewExplanation(row);
    section.appendChild(reason);
    if (!row.isExcluded) {
      ['identity', 'duplicate'].forEach(function (kind) {
        var status = row[kind + 'ReviewStatus'];
        if (!status || status === 'none') { return; }
        var line = document.createElement('div'); line.className = 'review-action-line';
        var label = document.createElement('strong'); label.textContent = (kind === 'identity' ? t('review_identity') : t('review_devices')) + ' · '
          + (status === 'confirmed' ? t('confidence_Verified') : status === 'deferred' ? t('review_deferred') : t('metric_review')); line.appendChild(label);
        var disabled = !row[kind + 'EvidenceToken'];
        if (status !== 'confirmed') { actionButton(line, t('review_confirm'), row.key + ':' + kind + ':confirm', function () { setPersonReview(row, kind, 'confirmed', scope); }, disabled); }
        if (status === 'pending') { actionButton(line, t('review_defer'), row.key + ':' + kind + ':defer', function () { setPersonReview(row, kind, 'deferred', scope); }, disabled); }
        if (status !== 'pending') { actionButton(line, t('review_undo'), row.key + ':' + kind + ':undo', function () { setPersonReview(row, kind, 'pending', scope); }, disabled); }
        section.appendChild(line);
      });
    }
    {
      actionButton(section, row.isExcluded ? t('review_restore') : t('review_exclude'), row.key + ':exclusion', function () { setPersonExcluded(row, !row.isExcluded, scope); });
      var note = document.createElement('p'); note.className = 'muted'; note.textContent = t('review_exclude_note'); section.appendChild(note);
    }
    parent.appendChild(section);
  }
  function appendConnectionActions(card, connection, row, scope, index) {
    var key = JSON.stringify([connection.source, connection.presenceKey]);
    var draft = state.connectionDrafts[key] || (state.connectionDrafts[key] = { query: '', personId: '', open: false });
    var label = document.createElement('p'); label.className = 'connection-identity';
    label.textContent = t('connection_number', { number: index + 1 }) + ' · ' + sourceLabel(connection.source) + ' · ' + (connection.presenceKey || t('connection_no_id'))
      + (connection.manualMatch ? t('connection_manual_tag') : '') + (connection.reviewStatus === 'deferred' ? t('connection_deferred_tag') : ''); card.appendChild(label);
    if (row.isExcluded || !connection.source || !connection.presenceKey || !connection.evidenceToken) { return; }
    var details = document.createElement('details'); details.className = 'connection-actions'; details.open = draft.open;
    var summary = document.createElement('summary'); summary.textContent = t('review_link'); summary.dataset.focusKey = key + ':details'; details.appendChild(summary);
    details.addEventListener('toggle', function () { draft.open = details.open; });
    var searchLabel = document.createElement('label'); searchLabel.textContent = t('review_search');
    var search = document.createElement('input'); search.type = 'search'; search.value = draft.query; search.placeholder = t('review_search_placeholder'); search.dataset.focusKey = key + ':search'; searchLabel.appendChild(search); details.appendChild(searchLabel);
    var selectLabel = document.createElement('label'); selectLabel.textContent = t('review_roster_person');
    var select = document.createElement('select'); select.dataset.focusKey = key + ':person'; selectLabel.appendChild(select); details.appendChild(selectLabel);
    function choices() {
      var rosterPeople = rosterById();
      select.textContent = '';
      var empty = document.createElement('option'); empty.value = ''; empty.textContent = t('review_choose_person'); select.appendChild(empty);
      asArray(state.board && state.board.people).filter(function (person) {
        var roster = rosterPeople[person.rosterPersonId] || {};
        return !person.isExcluded && normalizeSearch([person.name, person.group, person.organization, roster.email].join(' ')).indexOf(normalizeSearch(draft.query)) >= 0;
      }).forEach(function (person) {
        var roster = rosterPeople[person.rosterPersonId] || {};
        var option = document.createElement('option'); option.value = person.rosterPersonId;
        option.textContent = person.name + ' · ' + (person.group || t('review_no_group')) + ' · ' + (roster.email || t('roster_sequence', { id: person.sequence || person.rosterPersonId })); select.appendChild(option);
      });
      select.value = draft.personId;
      if (select.value !== draft.personId) { draft.personId = ''; select.value = ''; }
    }
    choices();
    var apply = actionButton(details, t('review_match'), key + ':match', function () { matchConnection(connection, select.value, scope); }, !draft.personId);
    search.addEventListener('input', function () { draft.query = search.value; draft.personId = ''; choices(); apply.disabled = true; });
    select.addEventListener('change', function () { draft.personId = select.value; apply.disabled = !select.value; });
    if (connection.manualMatch) { actionButton(details, t('review_unmatch'), key + ':undo-match', function () { matchConnection(connection, null, scope); }); }
    if (row.kind === 'unmatched') {
      actionButton(details, connection.reviewStatus === 'deferred' ? t('review_undefer') : t('review_defer_connection'), key + ':defer', function () { reviewConnection(connection, connection.reviewStatus === 'deferred' ? 'pending' : 'deferred', scope); });
    }
    var note = document.createElement('p'); note.textContent = t('review_connection_note'); details.appendChild(note); card.appendChild(details);
  }
  function buildDetailRow(row) {
    var tr = document.createElement('tr');
    tr.className = 'detail-row';
    var td = document.createElement('td');
    td.colSpan = 8;
    var detail = document.createElement('div');
    detail.className = 'participant-detail';
    var scope = meetingScope();
    if (row.kind === 'roster') { appendPersonActions(detail, row, scope); }

    var connectionBlock = document.createElement('section');
    connectionBlock.className = 'detail-block';
    var connectionTitle = document.createElement('h3');
    connectionTitle.textContent = t('connection_current', { count: row.connections.length });
    connectionBlock.appendChild(connectionTitle);
    var cards = document.createElement('div');
    cards.className = 'connection-cards';
    if (!row.connections.length) {
      var empty = document.createElement('div');
      empty.className = 'connection-card';
      var emptyStrong = document.createElement('strong');
      emptyStrong.textContent = t('connection_none');
      var emptySpan = document.createElement('span');
      emptySpan.textContent = t('connection_none_note');
      empty.appendChild(emptyStrong);
      empty.appendChild(emptySpan);
      cards.appendChild(empty);
    }
    row.connections.forEach(function (connection, index) {
      var card = document.createElement('div');
      card.className = 'connection-card';
      var strong = document.createElement('strong');
      strong.textContent = connection.rawName || connection.canonicalName || t('duplicate_connection');
      var span = document.createElement('span');
      var normalized = connection.canonicalName && connection.rawName && connection.canonicalName !== connection.rawName
        ? t('connection_normalized_name', { name: connection.canonicalName })
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
      renameButton.textContent = t('connection_rename', { name: candidate.target });
      renameButton.addEventListener('click', function (event) {
        event.stopPropagation();
        renameZoomParticipant(candidate);
      });
      var renameNote = document.createElement('p');
      renameNote.className = 'rename-note';
      renameNote.textContent = t('connection_rename_note');
      renameBox.appendChild(renameButton);
      renameBox.appendChild(renameNote);
      connectionBlock.appendChild(renameBox);
    }

    var matchBlock = document.createElement('section');
    matchBlock.className = 'detail-block';
    var matchTitle = document.createElement('h3');
    matchTitle.textContent = t('detail_title');
    matchBlock.appendChild(matchTitle);
    var dl = document.createElement('dl');
    dl.className = 'detail-list';
    [
      [t('detail_roster_name'), row.name],
      [t('detail_group'), row.group || t('detail_none')],
      [t('detail_email'), row.email || t('detail_none')],
      [t('detail_phone'), row.phone || t('detail_none')],
      [t('detail_match'), confidenceInfo(row).label],
      [t('detail_last_join'), formatDateTime(row.lastJoinedAt)],
      [t('detail_last_leave'), formatDateTime(row.lastLeftAt)],
      [t('detail_join_count'), t('join_count', { count: row.joinCount })]
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
        detail: getEventLabel(type),
        meta: sourceLabel(event.source),
        sortAt: new Date(event.occurredAt || 0).getTime()
      };
    });
    duplicates.forEach(function (group) {
      var latest = group.connections.reduce(function (value, item) {
        var at = new Date(item.lastSeenAt || item.firstSeenAt || 0).getTime();
        return at > value ? at : value;
      }, 0);
      items.push({ key: 'duplicate:' + group.key, type: 'Duplicate', at: latest ? new Date(latest) : null, title: group.personName, detail: t('activity_duplicate'), meta: t('connection_count', { count: group.connections.length }), sortAt: latest });
    });
    items.sort(function (a, b) { return b.sortAt - a.sortAt; });
    items = items.slice(0, 30);
    el.activityFeed.textContent = '';
    el.activityEmpty.hidden = items.length > 0;
    el.activityFeed.hidden = items.length === 0;
    setText(el.activityCount, t('activity_count', { count: items.length }));
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
      title.textContent = item.title || t('activity_unknown');
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
      name.setAttribute('aria-label', t('duplicate_details', { name: group.personName }));
      name.textContent = group.personName;
      var count = document.createElement('span');
      count.textContent = t('duplicate_connections', { count: group.connections.length });
      header.appendChild(name); header.appendChild(count);
      var reason = document.createElement('p');
      reason.textContent = group.reason || t('duplicate_count_as_one');
      var list = document.createElement('div');
      list.className = 'duplicate-connections';
      group.connections.forEach(function (connection) {
        var row = document.createElement('div');
        row.className = 'duplicate-connection';
        var label = document.createElement('strong');
        label.textContent = connection.rawName || connection.canonicalName || t('duplicate_connection');
        var time = document.createElement('time');
        time.textContent = t('connected_since', { time: formatTime(connection.firstSeenAt) });
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
    var busy = beginBusy(t('export_csv_preparing'));
    var path = '/api/meetings/' + encodeURIComponent(meetingId) + '/export';
    var group = state.group;
    if (group) { path += '?group=' + encodeURIComponent(group); }
    request(path, { raw: true }).then(function (response) {
      return response.blob();
    }).then(function (blob) {
      endBusy(busy);
      var fileName = scopedFileName(meetingId, group, 'attendance.csv');
      saveBlob(blob, fileName);
      toast('ok', t('export_csv_started'), fileName);
    }, function (error) { endBusy(busy); toast('bad', t('export_csv_failed'), error.message); });
  }

  function scopedFileName(meetingId, group, suffix) {
    return group
      ? meetingId + '-' + group.replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-+|-+$/g, '') + '-' + suffix
      : meetingId + '-' + suffix;
  }
  function saveBlob(blob, fileName) {
    var url = URL.createObjectURL(blob);
    var link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click(); link.remove();
    setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
  }

  /* 카카오톡·메모에 붙여 넣을 회의 현황. 화면 지표와 같은 집계(선택 그룹, 오늘 제외 반영)를 쓴다. */
  function buildStatusText() {
    var board = state.board || {};
    var summary = scopedSummary();
    var rosterRows = {};
    summary.rosterScoped.forEach(function (row) { rosterRows[row.rosterPersonId] = row; });
    function personLine(sequence, name) {
      return sequence ? t('status_text_person', { number: sequence, name: name }) : name;
    }
    var asOf = formatDateTime(board.lastReceivedAt || board.generatedAt || new Date()).slice(0, 16);
    var lines = [t('status_text_title', { meetingId: currentMeetingId() }),
      t('status_text_as_of', { time: asOf }) + (state.group ? ' · ' + t('status_text_group', { group: state.group }) : ''),
      '',
      t('status_text_connected', { total: summary.rosterScoped.length, present: summary.present.length })];
    if (summary.excluded.length) { lines.push(t('status_text_excluded', { count: summary.excluded.length })); }
    lines.push('');
    if (summary.absent.length) {
      lines.push(t('status_text_absent', { count: summary.absent.length }));
      sortRows(summary.absent, 'number').forEach(function (row) {
        lines.push(personLine(row.sequence, row.name) + (row.attendanceState === 'Left' ? t('status_text_left_suffix') : ''));
      });
    } else {
      lines.push(t('status_text_absent_none'));
    }
    lines.push('');
    if (summary.duplicates.length) {
      lines.push(t('status_text_duplicates', { count: summary.duplicates.length }));
      sortRows(summary.duplicates.map(function (group) {
        var row = rosterRows[group.rosterPersonId] || {};
        return { sequence: row.sequence, name: row.name || group.personName, count: group.connections.length };
      }), 'number').forEach(function (item) {
        lines.push(t('status_text_duplicate_line', { person: personLine(item.sequence, item.name), count: item.count }));
      });
    } else {
      lines.push(t('status_text_duplicates_none'));
    }
    return lines.join('\n');
  }

  /* 클립보드를 쓸 수 있으면 복사 완료를, 막히면 같은 글을 선택해 둔 상자를 보여 준다. */
  function copyMeetingStatus() {
    if (!state.board) { return Promise.resolve(); }
    var text = buildStatusText();
    el.statusText.value = text;
    var clipboard = typeof navigator !== 'undefined' && navigator.clipboard;
    var written = clipboard && clipboard.writeText ? clipboard.writeText(text) : Promise.reject(new Error('clipboard unavailable'));
    return written.then(function () { showStatusDialog(true); }, function () { showStatusDialog(false); });
  }
  function showStatusDialog(copied) {
    setText(el.statusTextTitle, t(copied ? 'status_copied_title' : 'status_copy_manual_title'));
    setText(el.statusTextNote, t(copied ? 'status_copied_note' : 'status_copy_manual_note'));
    if (!el.statusTextDialog.open) { el.statusTextDialog.showModal(); }
    if (copied) { el.btnStatusClose.focus(); }
    else { el.statusText.focus(); el.statusText.setSelectionRange(0, el.statusText.value.length); }
    el.statusText.scrollTop = 0;
  }
  function downloadStatusText() {
    var now = new Date();
    var stamp = now.getFullYear() + pad(now.getMonth() + 1) + pad(now.getDate()) + '-' + pad(now.getHours()) + pad(now.getMinutes());
    var fileName = scopedFileName(currentMeetingId(), state.group, 'status-' + stamp + '.txt');
    saveBlob(new Blob(['\ufeff' + el.statusText.value.replace(/\n/g, '\r\n')], { type: 'text/plain;charset=utf-8' }), fileName);
    toast('ok', t('status_download_started'), fileName);
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
    setText(el.snapshotParsed, t('snapshot_parsed', { lines: lines, names: names.length, duplicates: lines - names.length })
      + (names.length > MAX_SNAPSHOT_NAMES ? t('snapshot_parsed_over_limit') : ''));
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
        var item = document.createElement('p'); item.textContent = pair[0] + ': ' + (asArray(result && result[pair[1]]).join(', ') || t('detail_none')); el.snapshotResultDetails.appendChild(item);
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
    setText(el.autoStatus, checked ? t('auto_active') : t('off'));
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
    setText(el.btnTutorialNext, t(state.tutorialStep === total - 1 ? 'btn_tutorial_finish' : 'btn_tutorial_next'));
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
    if (info.phase === 'unsupported') { return t('update_status_unsupported'); }
    if (info.phase === 'checking') { return t('update_status_checking'); }
    if (info.phase === 'downloading') {
      return t('update_status_downloading', { version: target, progress: info.progress === null ? '…' : ' · ' + info.progress + '%' });
    }
    if (info.phase === 'verifying') { return t('update_status_verifying', { version: target }); }
    if (info.phase === 'ready') { return t('update_status_ready', { version: target }); }
    if (info.phase === 'installing') { return t('update_status_installing'); }
    if (info.phase === 'available') { return t('update_status_available', { version: target }); }
    if (info.phase === 'error') { return t('update_status_error'); }
    if (info.phase === 'idle') {
      return t('update_status_idle', { checked: info.lastCheckedAt ? t('update_status_last_checked', { time: formatDateTime(info.lastCheckedAt) }) : '' });
    }
    return t('update_status_unknown');
  }

  function renderUpdate() {
    var info = state.update;
    if (!info || !el.updateStatusText) {
      if (!info && el.versionText) { setText(el.versionText, t('version_checking')); }
      if (!info && el.updateCurrentVersion) { setText(el.updateCurrentVersion, t('checking') + '…'); }
      return;
    }
    var busy = UPDATE_BUSY_PHASES.indexOf(info.phase) >= 0;

    setText(el.updateCurrentVersion, info.currentVersion || t('update_version_unknown'));
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
      if (info.phase === 'ready') { badge = t('update_badge_ready'); badgeClass = 'is-ready'; }
      else if (info.phase === 'downloading' || info.phase === 'verifying' || info.phase === 'available') { badge = t('update_badge_preparing'); }
      else if (info.phase === 'installing') { badge = t('update_badge_installing'); }
      else if (info.phase === 'error') { badge = t('update_badge_error'); badgeClass = 'is-bad'; }
      else if (info.phase === 'unsupported') { badge = t('update_badge_unsupported'); badgeClass = 'is-warn'; }
      else if (info.phase === 'idle') { badge = t('update_badge_idle'); badgeClass = 'is-ready'; }
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
      setText(el.btnCheckUpdate, info.phase === 'checking' ? t('btn_check_update_busy') : t('btn_check_update'));
    }
    if (el.btnInstallUpdate) {
      el.btnInstallUpdate.hidden = info.phase !== 'ready' && info.phase !== 'installing';
      el.btnInstallUpdate.disabled = info.phase === 'installing';
      setText(el.btnInstallUpdate, info.phase === 'installing' ? t('btn_install_update_busy') : t('btn_install_update'));
    }

    if (el.versionText) {
      var chipLabel = info.currentVersion ? 'v' + String(info.currentVersion).replace(/^v/i, '') : t('version_checking');
      if (info.phase === 'ready') { chipLabel += ' · ' + t('update_chip_ready'); }
      else if (info.phase === 'downloading' || info.phase === 'verifying' || info.phase === 'available') { chipLabel += ' · ' + t('update_chip_preparing'); }
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

  var originalRichLabels = new WeakMap();
  function applyLanguage(lang, opts) {
    opts = opts || {};
    var target = SUPPORTED_LANGS.indexOf(lang) >= 0 ? lang : 'ko';
    state.lang = target;
    if (opts.persist !== false) {
      writeStore(STORAGE.lang, target);
    }
    if (typeof document !== 'undefined' && document.documentElement) {
      document.documentElement.lang = target;
    }

    if (el.btnToggleLang) {
      el.btnToggleLang.textContent = target.toUpperCase();
    }
    if (el.langRadios) {
      el.langRadios.forEach(function (radio) {
        radio.checked = radio.value === target;
      });
    }

    // Translate elements with data-i18n
    if (typeof document !== 'undefined') {
      var i18nNodes = document.querySelectorAll('[data-i18n]');
      for (var i = 0; i < i18nNodes.length; i++) {
        var node = i18nNodes[i];
        var key = node.getAttribute('data-i18n');
        if (key) {
          var translated = t(key);
          if (translated && translated !== key) {
            if (node.children.length) {
              if (target === 'ko') { continue; }
              originalRichLabels.set(node, Array.from(node.childNodes));
            }
            var original = originalRichLabels.get(node);
            if (target === 'ko' && original) {
              node.replaceChildren.apply(node, original);
            } else {
              node.textContent = translated;
            }
          }
        }
      }

      var placeholderNodes = document.querySelectorAll('[data-i18n-placeholder]');
      for (var j = 0; j < placeholderNodes.length; j++) {
        var pNode = placeholderNodes[j];
        var pKey = pNode.getAttribute('data-i18n-placeholder');
        if (pKey) {
          var pText = t(pKey);
          if (pText && pText !== pKey) {
            pNode.placeholder = pText;
          }
        }
      }

      var ariaNodes = document.querySelectorAll('[data-i18n-aria]');
      for (var k = 0; k < ariaNodes.length; k++) {
        var aNode = ariaNodes[k];
        var aKey = aNode.getAttribute('data-i18n-aria');
        if (aKey) {
          var aText = t(aKey);
          if (aText && aText !== aKey) {
            aNode.setAttribute('aria-label', aText);
          }
        }
      }
    }

    document.title = t('app_title');
    renderHealthStatus();
    renderZoomApiStatus();
    renderModeNote();
    renderRelaySettings();
    renderThemeControls();
    renderRosterNote();
    renderRosterFeedback();
    renderUpdate();
    if (el.snapshotNames) { updateParsedCount(); }
    if (el.tutorialSteps.length) { renderTutorial(); }
    if (state.board) {
      renderBoard(state.board);
    } else {
      renderParticipants();
      renderFreshness();
      renderGroupOptions();
      renderActivity([], []);
      setText(el.rosterSummary, t('roster_count', { count: state.roster.length }));
      var zeroPeople = t('count_people', { count: 0 });
      renderDesktopSummary(0, 0);
      setText(el.phoneSummary, t('phone_summary', { present: 0, total: 0, absent: 0 }));
      setText(el.metricPresent, zeroPeople);
      setText(el.metricAbsent, zeroPeople);
      setText(el.metricReview, t('count_items', { count: 0 }));
      if (el.btnReview) { el.btnReview.disabled = true; }
      if (el.btnExport) { el.btnExport.disabled = true; }
      setText(el.metricDuplicate, zeroPeople);
    }

    if (opts.notify) {
      var langNames = { ko: '한국어', en: 'English', es: 'Español' };
      toast('ok', t('language'), langNames[target] || target);
    }
  }

  function toggleLanguage() {
    var nextIndex = (SUPPORTED_LANGS.indexOf(state.lang) + 1) % SUPPORTED_LANGS.length;
    applyLanguage(SUPPORTED_LANGS[nextIndex], { persist: true, notify: true });
  }

  function cacheElements() {
    [
      'meeting-id','health-dot','health-text','zoom-dot','zoom-status','chk-autorefresh','auto-status','last-sync-time','next-sync-time',
      'readiness','readiness-title','readiness-note','btn-next-step','btn-review','source-status','activity-mode','auto-mode-note',
      'scope-note','export-scope','count-excluded','empty-title','empty-note','btn-reset-filters','alert-details','alert-technical',
      'snapshot-result','snapshot-result-summary','snapshot-result-details',
      'action-confirm','action-confirm-message','btn-confirm-cancel','btn-confirm-apply',
      'btn-sync-zoom','btn-toggle-lang','btn-toggle-theme','btn-open-settings','btn-open-tutorial','btn-health-detail','btn-api-detail','alert-bar','alert-dot','alert-title','alert-message','btn-alert-action','btn-dismiss-alert',
      'desktop-summary','summary-total','summary-present','phone-summary','summary-rate','summary-time','rate-ring','metric-present','metric-absent','metric-review','metric-duplicate',
      'participants-meta','btn-refresh','btn-export','btn-copy-status','status-text-dialog','status-text-title','status-text-note','status-text','btn-status-download','btn-status-close','participant-search','group-filter','participant-table-wrap','participant-rows','participants-empty','visible-count',
      'count-all','count-present','count-absent','count-review','count-unmatched','activity-feed','activity-empty','activity-count',
      'duplicate-list','duplicate-empty','duplicate-count','sync-summary','roster-summary','settings-dialog','roster-file','btn-upload-roster',
      'btn-reload-roster','roster-note','roster-feedback','roster-feedback-title','roster-feedback-message','roster-feedback-hint','roster-feedback-list','api-settings-status','btn-check-zoom','settings-autorefresh','autorefresh-interval','snapshot-names',
      'snapshot-parsed','chk-empty-ok','btn-submit-snapshot','btn-clear-snapshot','session-log','session-log-empty','btn-clear-log',
      'toast-region','busy','busy-text',
      'zoom-app-dot','zoom-app-status','btn-zoom-app-detail','mode-note','zoom-app-settings-status','pairing-code','pairing-expiry',
      'pairing-session','btn-create-pairing-code','btn-copy-pairing-code','btn-zoom-app-sync','pairing-disabled-reason','zoom-app-home-url','btn-copy-home-url',
      'relay-base-url','btn-save-relay-url','btn-check-relay-health','relay-settings-note','relay-health-status',
      'update-current-version','update-status-text','update-progress','update-progress-bar','update-badge','update-error-text',
      'btn-check-update','btn-install-update','btn-version-chip','version-dot','version-text',
      'btn-settings-tutorial','tutorial-dialog','tutorial-progress-text','btn-close-tutorial','btn-skip-tutorial','btn-tutorial-back','btn-tutorial-next'
    ].forEach(function (id) {
      var key = id.replace(/-([a-z])/g, function (_, letter) { return letter.toUpperCase(); });
      el[key] = $(id);
    });
    el.filterButtons = Array.prototype.slice.call(document.querySelectorAll('[data-filter]'));
    el.sortButtons = Array.prototype.slice.call(document.querySelectorAll('[data-sort]'));
    el.tutorialSteps = Array.prototype.slice.call(document.querySelectorAll('[data-tutorial-step]'));
    el.tutorialProgressButtons = Array.prototype.slice.call(document.querySelectorAll('#tutorial-progress [role="tab"]'));
    el.modeRadios = Array.prototype.slice.call(document.querySelectorAll('input[name="connection-mode"]'));
    el.themeRadios = Array.prototype.slice.call(document.querySelectorAll('input[name="theme"]'));
    el.langRadios = Array.prototype.slice.call(document.querySelectorAll('input[name="lang"]'));
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
    el.btnCopyStatus.addEventListener('click', copyMeetingStatus);
    el.btnStatusDownload.addEventListener('click', downloadStatusText);
    el.btnStatusClose.addEventListener('click', function () { el.statusTextDialog.close(); });
    el.statusTextDialog.addEventListener('close', function () { el.btnCopyStatus.focus(); });
    el.sortButtons.forEach(function (button) {
      button.addEventListener('click', function () { applySort(button.dataset.sort); });
    });
    el.btnToggleTheme.addEventListener('click', toggleTheme);
    if (el.btnToggleLang) { el.btnToggleLang.addEventListener('click', toggleLanguage); }
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
    el.btnSaveRelayUrl.addEventListener('click', saveRelaySettings);
    if (el.btnCheckRelayHealth) { el.btnCheckRelayHealth.addEventListener('click', checkRelayHealth); }
    if (el.relayBaseUrl) {
      el.relayBaseUrl.addEventListener('input', function () {
        if (state.relayHealth && !state.relayHealthChecking) {
          state.relayHealth = null;
          renderRelayHealth();
        }
      });
    }
    el.btnCopyPairingCode.addEventListener('click', function () {
      copyToClipboard(state.pairing && state.pairing.code, '페어링 코드 복사됨');
    });
    el.btnZoomAppSync.addEventListener('click', function () { requestZoomAppSync({ silent: false }); });
    el.btnCopyHomeUrl.addEventListener('click', function () {
      copyToClipboard(effectiveZoomAppHomeUrl(), 'Home URL 복사됨');
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
    if (el.langRadios) {
      el.langRadios.forEach(function (radio) {
        radio.addEventListener('change', function () {
          if (radio.checked) { applyLanguage(radio.value, { persist: true, notify: true }); }
        });
      });
    }
    el.btnUploadRoster.addEventListener('click', uploadRoster);
    el.rosterFile.addEventListener('change', function () {
      if (el.rosterFile.files && el.rosterFile.files.length && state.rosterFeedback && state.rosterFeedback.kind === 'bad') {
        setRosterFeedback(null, { log: false });
      }
    });
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
      if (el.actionConfirm.open || el.statusTextDialog.open) { return; }
      if (el.tutorialDialog.open) {
        if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
          event.preventDefault(); moveTutorial(event.key === 'ArrowRight' ? 1 : -1);
        }
        return;
      }
      var active = document.activeElement;
      var inField = active && (/^(INPUT|TEXTAREA|SELECT)$/.test(active.tagName) || active.isContentEditable);
      if (event.key === '/' && !el.settingsDialog.open && !inField) { event.preventDefault(); el.participantSearch.focus(); }
      if ((event.key === 'r' || event.key === 'R') && !event.metaKey && !event.ctrlKey && !event.altKey && !el.settingsDialog.open && !inField) {
        event.preventDefault(); syncNow({ silent: false });
      }
    });
  }

  function restoreState() {
    var savedLang = readStore(STORAGE.lang, 'ko');
    applyLanguage(savedLang, { persist: false, notify: false });
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
    var savedSort = readStore(STORAGE.sort, 'number');
    state.sort = SORT_KEYS.indexOf(savedSort) >= 0 ? savedSort : 'number';
    syncSortButtons();
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
    state.connectionPollId = window.setInterval(pollConnectionStatus, CONNECTION_POLL_MS);
    document.addEventListener('visibilitychange', function () {
      if (!document.hidden) { pollConnectionStatus(); }
    });
    checkHealth();
    checkZoomConnection(false);
    loadRelaySettings();
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
