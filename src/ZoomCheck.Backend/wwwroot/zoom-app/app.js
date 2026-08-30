(function () {
  'use strict';

  var sdk = window.zoomSdk;
  var CAPABILITIES = ['getSupportedJsApis', 'getMeetingContext', 'getMeetingUUID', 'getUserContext', 'getMeetingParticipants', 'onParticipantChange'];
  var REQUIRED = ['getMeetingParticipants', 'getMeetingContext', 'getUserContext'];
  var state = { meetingId:null, meetingUuid:null, role:null, supported:[], token:null, revision:0, heartbeat:null, periodic:null, debounce:null, syncing:false };
  var el = {};

  function $(id) { return document.getElementById(id); }
  function setText(node, value) { node.textContent = value; }
  function normalizeBase(value) {
    var url = new URL(window.location.origin);
    if (url.protocol !== 'https:') { throw new Error('Zoom App 연결 주소는 HTTPS여야 합니다.'); }
    return url.origin;
  }
  function roleLabel(role) { return role === 'host' ? '호스트' : (String(role).toLowerCase() === 'cohost' ? '공동호스트' : role || '미확인'); }
  function show(message, kind) { el.message.className = 'message' + (kind ? ' ' + kind : ''); setText(el.message, message); }
  function setConnected(connected) {
    el.statusDot.className = 'status-dot ' + (connected ? 'ok' : '');
    setText(el.bridgeState, connected ? '연결됨' : '미연결');
    el.btnSync.disabled = !connected; el.btnDisconnect.disabled = !connected; el.btnConnect.disabled = connected;
  }
  function api(path, body) {
    var base = normalizeBase(el.backendUrl.value);
    return fetch(base + path, { method:'POST', headers:{ 'Content-Type':'application/json', Accept:'application/json' }, body:JSON.stringify(body), cache:'no-store', credentials:'omit' }).then(function (response) {
      if (response.ok) { return response.json(); }
      return response.json().catch(function () { return {}; }).then(function (problem) { throw new Error(problem.detail || problem.title || ('HTTP ' + response.status)); });
    });
  }
  function clearTimers() {
    if (state.heartbeat) { clearInterval(state.heartbeat); state.heartbeat = null; }
    if (state.periodic) { clearInterval(state.periodic); state.periodic = null; }
    if (state.debounce) { clearTimeout(state.debounce); state.debounce = null; }
  }
  function disconnect(message) {
    clearTimers(); state.token = null; state.revision = 0; setConnected(false);
    if (message) { show(message); }
  }
  function participantPayload(item) { return { participantUuid:item.participantUUID || item.participantUuid || '', screenName:item.screenName || '', role:item.role || 'attendee' }; }
  function sendSnapshot(reason) {
    if (!state.token || state.syncing) { return Promise.resolve(null); }
    state.syncing = true; show('참가자 목록을 읽는 중…');
    return sdk.getMeetingParticipants().then(function (result) {
      var participants = (result && result.participants || []).map(participantPayload).filter(function (item) { return item.participantUuid && item.screenName; });
      return api('/api/zoom-app/bridge/snapshot', { sessionToken:state.token, meetingId:state.meetingId, capturedAt:new Date().toISOString(), participants:participants }).then(function (response) {
        setText(el.participantCount, String(response.activeParticipants)); setText(el.lastSent, new Date().toLocaleTimeString('ko-KR', { hour12:false }));
        show('현재 참가자 ' + response.activeParticipants + '명을 전송했습니다' + (reason ? ' · ' + reason : '') + '.', 'ok'); return response;
      });
    }).catch(function (error) {
      show('참가자 전송 실패: ' + error.message, 'bad');
      if (/session|expired|페어링/i.test(error.message)) { disconnect(); }
      return null;
    }).finally(function () { state.syncing = false; });
  }
  function heartbeat() {
    if (!state.token) { return; }
    api('/api/zoom-app/bridge/heartbeat', { sessionToken:state.token, lastRevision:state.revision }).then(function (response) {
      if (response.syncRequested) { state.revision = response.syncRevision; sendSnapshot('대시보드 요청'); }
    }).catch(function (error) { disconnect('연결이 종료되었습니다: ' + error.message); });
  }
  function startLoops() { clearTimers(); state.heartbeat = setInterval(heartbeat, 2000); state.periodic = setInterval(function () { sendSnapshot('주기 동기화'); }, 10000); }
  function connect() {
    var code = el.pairingCode.value.replace(/\D/g, '');
    if (code.length !== 6) { show('6자리 페어링 코드를 입력하세요.', 'bad'); return; }
    try { normalizeBase(el.backendUrl.value); } catch (error) { show(error.message, 'bad'); return; }
    show('ZoomCheck와 페어링하는 중…'); el.btnConnect.disabled = true;
    api('/api/zoom-app/bridge/connect', { pairingCode:code, meetingId:state.meetingId, meetingUuid:state.meetingUuid, role:state.role, supportedApis:state.supported }).then(function (response) {
      state.token = response.sessionToken; state.revision = response.syncRevision || 0; setConnected(true); startLoops(); return sendSnapshot('최초 연결');
    }).catch(function (error) { el.btnConnect.disabled = false; show('연결 실패: ' + error.message, 'bad'); });
  }
  function initializeSdk() {
    if (!sdk) { show('Zoom Apps SDK를 불러오지 못했습니다. Zoom 클라이언트 안에서 다시 여세요.', 'bad'); el.roleBadge.className = 'role-badge bad'; return; }
    sdk.config({ version:'0.16', capabilities:CAPABILITIES }).then(function () {
      return Promise.all([sdk.getSupportedJsApis(), sdk.getMeetingContext(), sdk.getMeetingUUID().catch(function () { return {}; }), sdk.getUserContext()]);
    }).then(function (values) {
      state.supported = values[0].supportedApis || []; state.meetingId = values[1].meetingID; state.meetingUuid = values[2].meetingUUID || null; state.role = values[3].role;
      var missing = REQUIRED.filter(function (name) { return state.supported.indexOf(name) < 0; });
      var allowedRole = state.role === 'host' || String(state.role).toLowerCase() === 'cohost';
      setText(el.meetingState, state.meetingId || '미확인'); setText(el.roleState, roleLabel(state.role)); setText(el.roleBadge, roleLabel(state.role));
      el.roleBadge.className = 'role-badge ' + (allowedRole && !missing.length ? 'ok' : 'bad');
      if (!allowedRole) { throw new Error('현재 계정은 이 회의의 호스트 또는 공동호스트가 아닙니다.'); }
      if (missing.length) { throw new Error('이 Zoom 클라이언트에서 필요한 기능이 열리지 않았습니다: ' + missing.join(', ')); }
      if (state.supported.indexOf('onParticipantChange') >= 0) {
        sdk.onParticipantChange(function () { if (state.debounce) { clearTimeout(state.debounce); } state.debounce = setTimeout(function () { sendSnapshot('참가자 변경'); }, 400); });
      }
      show('권한 확인 완료. 대시보드의 페어링 코드를 입력하세요.', 'ok'); el.btnConnect.disabled = false;
    }).catch(function (error) { show('SDK 초기화 실패: ' + error.message, 'bad'); el.roleBadge.className = 'role-badge bad'; setText(el.roleBadge, '사용 불가'); });
  }
  function init() {
    ['role-badge','status-dot','meeting-state','role-state','bridge-state','participant-count','last-sent','backend-url','pairing-code','btn-connect','btn-sync','btn-disconnect','message'].forEach(function (id) { el[id.replace(/-([a-z])/g, function (_, c) { return c.toUpperCase(); })] = $(id); });
    el.backendUrl.value = window.location.origin; setConnected(false); el.btnConnect.disabled = true;
    el.pairingCode.addEventListener('input', function () { el.pairingCode.value = el.pairingCode.value.replace(/\D/g, '').slice(0, 6); });
    el.btnConnect.addEventListener('click', connect); el.btnSync.addEventListener('click', function () { sendSnapshot('수동 요청'); }); el.btnDisconnect.addEventListener('click', function () { disconnect('연결을 해제했습니다.'); });
    initializeSdk();
  }
  if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', init); } else { init(); }
})();
