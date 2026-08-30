/* ZoomCheck 실시간 회의 운영 대시보드 — 외부 의존성 없음 */
(function () {
  'use strict';

  var STORAGE = {
    meetingId: 'zoomcheck.meetingId',
    autoRefresh: 'zoomcheck.autoRefresh',
    interval: 'zoomcheck.autoRefreshSeconds'
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
    selectedKey: null,
    pending: 0,
    timerId: null,
    clockId: null,
    nextSyncAt: null,
    lastSyncAt: null,
    lastSyncOk: null,
    sessionLog: [],
    alertAction: null,
    zoomConfigured: false
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
  function currentMeetingId() { return el.meetingId.value.trim(); }
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
    state.pending += 1;
    setText(el.busyText, message || '처리 중…');
    el.busy.hidden = false;
  }
  function endBusy() {
    state.pending = Math.max(0, state.pending - 1);
    if (state.pending === 0) { el.busy.hidden = true; }
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
    node.classList.remove('is-ok', 'is-bad');
    if (status === true) { node.classList.add('is-ok'); }
    if (status === false) { node.classList.add('is-bad'); }
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

  function checkZoomConnection(showToast) {
    return request('/api/zoom/connection-status').then(function (status) {
      state.zoomConfigured = !!(status && status.configured);
      setDot(el.zoomDot, state.zoomConfigured);
      setText(el.zoomStatus, state.zoomConfigured ? '설정됨' : '미설정');
      setText(el.apiSettingsStatus, state.zoomConfigured
        ? 'OAuth 자격증명이 설정되어 있습니다. 실제 권한은 지금 동기화에서 확인됩니다.'
        : 'Windows 사용자 환경 변수 Account ID, Client ID, Client Secret을 설정한 뒤 앱을 다시 시작하세요.');
      if (showToast) {
        toast(state.zoomConfigured ? 'ok' : 'warn', state.zoomConfigured ? 'Zoom API 설정됨' : 'Zoom API 설정 필요',
          state.zoomConfigured ? '실제 권한은 지금 동기화로 확인합니다.' : 'Server-to-Server OAuth 자격증명이 필요합니다.');
      }
      return status;
    }, function (error) {
      state.zoomConfigured = false;
      setDot(el.zoomDot, false);
      setText(el.zoomStatus, '확인 실패');
      setText(el.apiSettingsStatus, error.message);
      if (showToast) { toast('bad', 'Zoom API 상태 확인 실패', error.message); }
    });
  }

  function loadRoster(showToast) {
    return request('/api/roster').then(function (people) {
      state.roster = asArray(people);
      setText(el.rosterSummary, '명단 ' + state.roster.length + '명');
      setText(el.rosterNote, state.roster.length
        ? '현재 ' + state.roster.length + '명의 명단이 등록되어 있습니다.'
        : '등록된 명단이 없습니다. Excel 파일을 올려 주세요.');
      if (showToast) { toast('ok', '명단 확인 완료', state.roster.length + '명'); }
      if (state.board) { renderBoard(state.board); }
      return state.roster;
    }, function (error) {
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
    beginBusy('명단을 올리는 중…');
    request('/api/roster/upload', { method: 'POST', headers: { Accept: 'application/json' }, body: form }).then(function (result) {
      endBusy();
      el.rosterFile.value = '';
      toast('ok', '명단 업로드 완료', (result && result.count !== undefined ? result.count : '') + '명');
      return Promise.all([loadRoster(false), refreshBoard({ silent: true })]);
    }, function (error) {
      endBusy();
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

  function markSync(ok, note) {
    state.lastSyncAt = new Date();
    state.lastSyncOk = ok;
    setText(el.lastSyncTime, formatTime(state.lastSyncAt));
    var dot = el.syncSummary.querySelector('.status-dot');
    dot.className = 'status-dot ' + (ok ? 'ok' : 'bad');
    setText(el.syncSummary.lastChild, ok ? (note || ' 동기화 정상') : ' 동기화 실패');
    setText(el.summaryTime, ok ? formatDateTime(state.lastSyncAt) + ' 기준' : '동기화 실패 · 기존 출석 유지');
  }

  function syncZoomParticipants(options) {
    var opts = options || {};
    var meetingId = opts.silent ? currentMeetingId() : requireMeetingId();
    if (!meetingId) { return Promise.resolve(null); }
    var path = '/api/zoom/meetings/' + encodeURIComponent(meetingId) + '/sync';
    if (opts.allowEmpty) { path += '?allowEmptySnapshot=true'; }
    if (!opts.silent) { beginBusy('Zoom 참가자를 동기화하는 중…'); }
    return request(path, { method: 'POST' }).then(function (result) {
      if (!opts.silent) { endBusy(); }
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
      if (!opts.silent) { endBusy(); }
      markSync(false, ' 동기화 실패');
      scheduleNextSync();
      var emptyRejected = error.status === 409;
      showAlert(emptyRejected ? 'warn' : 'bad', emptyRejected ? 'Zoom이 0명을 반환했습니다' : 'Zoom 동기화 실패',
        emptyRejected ? '일시적인 빈 응답일 수 있어 기존 출석을 유지했습니다. 실제로 회의가 비었다면 전원 퇴장을 확정하세요.' : error.message,
        emptyRejected ? '전원 퇴장 확정' : '다시 시도',
        emptyRejected ? function () { syncZoomParticipants({ allowEmpty: true, silent: false }); } : function () { syncZoomParticipants({ silent: false }); });
      logSession('bad', 'Zoom 동기화 실패', error.message);
      if (!opts.silent) { toast('bad', 'Zoom 동기화 실패', error.message); }
      return null;
    });
  }

  function refreshBoard(options) {
    var opts = options || {};
    var meetingId = opts.silent ? currentMeetingId() : requireMeetingId();
    if (!meetingId) { return Promise.resolve(null); }
    if (!opts.silent) { beginBusy('출석 현황을 불러오는 중…'); }
    return request('/api/meetings/' + encodeURIComponent(meetingId) + '/board').then(function (board) {
      if (!opts.silent) { endBusy(); }
      renderBoard(board);
      if (opts.notify) { toast('ok', '화면 새로고침 완료', '저장된 최신 상태를 표시합니다.'); }
      return board;
    }, function (error) {
      if (!opts.silent) { endBusy(); toast('bad', '화면 새로고침 실패', error.message); }
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
      return {
        key: item.presenceKey || item.connectionKey || item.id || ('connection-' + index + '-' + rawName),
        rawName: rawName,
        canonicalName: canonicalName,
        email: item.participantEmail || item.email || null,
        firstSeenAt: item.firstSeenAt || item.joinedAt || null,
        lastSeenAt: item.lastSeenAt || item.updatedAt || null,
        rosterPersonId: item.matchedRosterPersonId || item.rosterPersonId || null,
        confidence: item.confidence || item.matchConfidence || 'Unmatched',
        source: item.source || 'Zoom API'
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
            rawName: item.rawDisplayName || item.rawName || item.originalName || item.zoomName || item.displayName || item.participantName || '',
            canonicalName: item.canonicalName || item.displayName || item.participantName || '',
            email: item.participantEmail || item.email || null,
            firstSeenAt: item.firstSeenAt || item.joinedAt || null,
            lastSeenAt: item.lastSeenAt || item.updatedAt || null
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
        organization: person.organization || roster.organization || '',
        attendanceState: person.attendanceState,
        confidence: person.confidence,
        confidenceReason: person.confidenceReason,
        lastJoinedAt: person.lastJoinedAt,
        lastLeftAt: person.lastLeftAt,
        joinCount: person.joinCount,
        connections: duplicate ? duplicate.connections : personConnections,
        duplicate: !!duplicate,
        review: REVIEW_CONFIDENCE.indexOf(person.confidence) >= 0 || !!duplicate
      };
    });
    var unmatchedConnections = connections.filter(function (connection) { return !connection.rosterPersonId; });
    asArray(board.unmatchedParticipants).forEach(function (item, index) {
      var matching = unmatchedConnections.filter(function (connection) {
        return normalizeSearch(connection.rawName) === normalizeSearch(item.participantName) || normalizeSearch(connection.canonicalName) === normalizeSearch(item.participantName);
      });
      rows.push({
        key: 'unmatched:' + index + ':' + item.participantName,
        kind: 'unmatched',
        rosterPersonId: null,
        name: item.participantName,
        email: matching[0] ? matching[0].email || '' : '',
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
      });
    });
    return rows;
  }

  function renderBoard(board) {
    if (!board || typeof board !== 'object') { return; }
    state.board = board;
    var rosterMap = rosterById();
    state.connections = extractConnections(board);
    state.duplicates = deriveDuplicateGroups(board, state.connections, rosterMap);
    state.rows = buildRows(board, state.connections, state.duplicates, rosterMap);

    var people = asArray(board.people);
    var present = people.filter(function (p) { return p.attendanceState === 'Present'; });
    var absent = people.filter(function (p) { return p.attendanceState !== 'Present'; });
    var review = state.rows.filter(function (row) { return row.review; });
    var unmatched = state.rows.filter(function (row) { return row.kind === 'unmatched'; });
    var total = people.length || state.roster.length;
    var rate = total ? Math.round((present.length / total) * 100) : 0;

    setText(el.summaryTotal, total);
    setText(el.summaryPresent, present.length);
    setText(el.summaryRate, total ? rate + '%' : '–');
    el.rateRing.style.setProperty('--rate', total ? rate : 0);
    setText(el.metricPresent, present.length + '명');
    setText(el.metricAbsent, absent.length + '명');
    setText(el.metricReview, review.length + '명');
    setText(el.metricDuplicate, state.duplicates.length + '명');
    setText(el.summaryTime, formatDateTime(board.generatedAt) + ' 기준');
    setText(el.participantsMeta, '명단 ' + total + '명 · 현재 Zoom 연결 ' + (state.connections.length || present.length + unmatched.length) + '건');

    setText(el.countAll, state.rows.length);
    setText(el.countPresent, state.rows.filter(function (row) { return row.attendanceState === 'Present'; }).length);
    setText(el.countAbsent, state.rows.filter(function (row) { return row.attendanceState !== 'Present'; }).length);
    setText(el.countReview, review.length);
    setText(el.countUnmatched, unmatched.length);
    setText(el.rosterSummary, '명단 ' + total + '명');

    renderParticipants();
    renderActivity(board.recentEvents, state.duplicates);
    renderDuplicates(state.duplicates);
  }

  function rowMatchesFilter(row) {
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
    return normalizeSearch([row.name, row.email, row.organization, connectionNames].join(' ')).indexOf(query) >= 0;
  }

  function statusInfo(row) {
    if (row.kind === 'unmatched') { return { label: '미매칭', className: 'unmatched' }; }
    if (row.attendanceState === 'Present') { return { label: '참석 중', className: 'present' }; }
    if (row.attendanceState === 'Left') { return { label: '퇴장', className: 'left' }; }
    return { label: '미참석', className: 'absent' };
  }
  function confidenceInfo(row) {
    if (row.kind === 'unmatched') { return { label: '미매칭', className: 'unmatched' }; }
    if (row.review) { return { label: row.duplicate ? '중복 검토' : (CONFIDENCE_LABEL[row.confidence] || '검토'), className: 'review' }; }
    return { label: CONFIDENCE_LABEL[row.confidence] || row.confidence || '–', className: 'verified' };
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
    var rows = state.rows.filter(rowMatchesFilter).filter(rowMatchesSearch);
    el.participantRows.textContent = '';
    el.participantsEmpty.hidden = rows.length > 0;
    setText(el.visibleCount, rows.length + '명 표시');

    rows.forEach(function (row) {
      var tr = document.createElement('tr');
      tr.className = 'participant-row' + (row.key === state.selectedKey ? ' is-selected' : '') + (row.kind === 'unmatched' ? ' is-unmatched' : '');
      tr.tabIndex = 0;
      tr.setAttribute('aria-expanded', row.key === state.selectedKey ? 'true' : 'false');
      tr.dataset.key = row.key;
      appendPillCell(tr, statusInfo(row));
      var nameCell = document.createElement('td');
      var name = document.createElement('span');
      name.className = 'participant-name';
      name.textContent = row.name;
      var secondary = document.createElement('span');
      secondary.className = 'participant-secondary';
      secondary.textContent = row.email || (row.kind === 'unmatched' ? 'Zoom 표시 이름' : '이메일 없음');
      nameCell.appendChild(name);
      nameCell.appendChild(secondary);
      tr.appendChild(nameCell);
      appendTextCell(tr, row.organization || '–');
      appendPillCell(tr, confidenceInfo(row), 'match-pill');
      appendTextCell(tr, formatTime(row.lastJoinedAt));
      appendTextCell(tr, row.attendanceState === 'Left' ? '퇴장 ' + formatTime(row.lastLeftAt) : (row.lastJoinedAt ? '입장 ' + formatTime(row.lastJoinedAt) : '–'));
      var connectionCell = document.createElement('td');
      var connectionPill = document.createElement('span');
      connectionPill.className = 'connection-pill' + (row.duplicate ? ' duplicate' : '');
      connectionPill.textContent = row.connections.length ? row.connections.length + '건' : '–';
      connectionCell.appendChild(connectionPill);
      tr.appendChild(connectionCell);
      tr.addEventListener('click', function () { toggleParticipant(row.key); });
      tr.addEventListener('keydown', function (event) {
        if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); toggleParticipant(row.key); }
      });
      el.participantRows.appendChild(tr);
      if (row.key === state.selectedKey) { el.participantRows.appendChild(buildDetailRow(row)); }
    });
    window.requestAnimationFrame(function () { el.participantTableWrap.scrollTop = scrollTop; });
  }

  function toggleParticipant(key) {
    state.selectedKey = state.selectedKey === key ? null : key;
    renderParticipants();
  }

  function buildDetailRow(row) {
    var tr = document.createElement('tr');
    tr.className = 'detail-row';
    var td = document.createElement('td');
    td.colSpan = 7;
    var detail = document.createElement('div');
    detail.className = 'participant-detail';

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
    row.connections.forEach(function (connection) {
      var card = document.createElement('div');
      card.className = 'connection-card';
      var strong = document.createElement('strong');
      strong.textContent = connection.rawName || connection.canonicalName || 'Zoom 연결';
      var span = document.createElement('span');
      var normalized = connection.canonicalName && connection.rawName && connection.canonicalName !== connection.rawName
        ? '자동 정리: ' + connection.canonicalName
        : (connection.email || connection.source || 'Zoom API');
      span.textContent = normalized;
      var time = document.createElement('time');
      time.textContent = formatTime(connection.firstSeenAt) + ' ~ ' + formatTime(connection.lastSeenAt);
      card.appendChild(strong);
      card.appendChild(span);
      card.appendChild(time);
      cards.appendChild(card);
    });
    connectionBlock.appendChild(cards);

    var matchBlock = document.createElement('section');
    matchBlock.className = 'detail-block';
    var matchTitle = document.createElement('h3');
    matchTitle.textContent = '명단 및 활동 상세';
    matchBlock.appendChild(matchTitle);
    var dl = document.createElement('dl');
    dl.className = 'detail-list';
    [
      ['명단 이름', row.name],
      ['이메일', row.email || '없음'],
      ['매칭 상태', confidenceInfo(row).label],
      ['첫 입장', formatDateTime(row.lastJoinedAt)],
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
        meta: event.source || '',
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
    el.duplicateList.textContent = '';
    el.duplicateEmpty.hidden = groups.length > 0;
    el.duplicateList.hidden = groups.length === 0;
    setText(el.duplicateCount, groups.length);
    groups.forEach(function (group) {
      var card = document.createElement('article');
      card.className = 'duplicate-card';
      var header = document.createElement('header');
      var name = document.createElement('strong');
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
      card.addEventListener('click', function () {
        if (group.rosterPersonId) { state.selectedKey = 'roster:' + group.rosterPersonId; state.filter = 'all'; syncFilterButtons(); renderParticipants(); }
      });
      el.duplicateList.appendChild(card);
    });
  }

  function syncFilterButtons() {
    el.filterButtons.forEach(function (button) {
      button.setAttribute('aria-selected', button.dataset.filter === state.filter ? 'true' : 'false');
    });
  }

  function exportCsv() {
    var meetingId = requireMeetingId();
    if (!meetingId) { return; }
    beginBusy('CSV를 준비하는 중…');
    request('/api/meetings/' + encodeURIComponent(meetingId) + '/export', { raw: true }).then(function (response) {
      return response.blob();
    }).then(function (blob) {
      endBusy();
      var url = URL.createObjectURL(blob);
      var link = document.createElement('a');
      link.href = url;
      link.download = meetingId + '-attendance.csv';
      document.body.appendChild(link);
      link.click(); link.remove();
      setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
      toast('ok', 'CSV 내보내기 시작', link.download);
    }, function (error) { endBusy(); toast('bad', 'CSV 내보내기 실패', error.message); });
  }

  function parseNames(raw) {
    var seen = {};
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
    setText(el.snapshotParsed, '인식된 이름 ' + names.length + '명' + (names.length > MAX_SNAPSHOT_NAMES ? ' · 최대 인원 초과' : ''));
    return names;
  }
  function submitSnapshot() {
    var meetingId = requireMeetingId();
    if (!meetingId) { return; }
    var names = updateParsedCount();
    if (names.length > MAX_SNAPSHOT_NAMES) { toast('bad', '목록이 너무 깁니다', '최대 ' + MAX_SNAPSHOT_NAMES + '명까지 적용할 수 있습니다.'); return; }
    if (!names.length && !el.chkEmptyOk.checked) { toast('warn', '빈 목록을 적용할 수 없습니다', '전원 퇴장이라면 빈 목록 허용을 체크하세요.'); return; }
    beginBusy('전체 참가자 목록을 적용하는 중…');
    request('/api/meetings/' + encodeURIComponent(meetingId) + '/participant-snapshot', {
      method: 'POST',
      json: { participantNames: names, source: 'web-dashboard', capturedAt: new Date().toISOString() }
    }).then(function (result) {
      endBusy();
      if (result && result.board) { renderBoard(result.board); }
      toast('ok', '전체 목록 적용 완료', '현재 ' + (result ? result.presentCount : names.length) + '명');
    }, function (error) { endBusy(); toast('bad', '전체 목록 적용 실패', error.message); });
  }

  function stopAutoRefresh() {
    if (state.timerId !== null) { clearInterval(state.timerId); state.timerId = null; }
    state.nextSyncAt = null;
  }
  function scheduleNextSync() {
    if (!el.chkAutoRefresh.checked) { state.nextSyncAt = null; updateClock(); return; }
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
    if (checked) {
      state.timerId = window.setInterval(function () {
        if (!document.hidden && state.pending === 0 && currentMeetingId()) { syncZoomParticipants({ silent: true }); }
      }, intervalSeconds() * 1000);
      scheduleNextSync();
    } else { updateClock(); }
  }
  function updateClock() {
    if (!el.chkAutoRefresh.checked || !state.nextSyncAt) { setText(el.nextSyncTime, '–'); return; }
    var seconds = Math.max(0, Math.ceil((state.nextSyncAt.getTime() - Date.now()) / 1000));
    setText(el.nextSyncTime, '00:' + pad(seconds));
  }

  function openSettings() {
    el.settingsAutoRefresh.checked = el.chkAutoRefresh.checked;
    if (typeof el.settingsDialog.showModal === 'function') { el.settingsDialog.showModal(); }
    else { el.settingsDialog.setAttribute('open', ''); }
  }

  function cacheElements() {
    [
      'meeting-id','health-dot','health-text','zoom-dot','zoom-status','chk-autorefresh','auto-status','last-sync-time','next-sync-time',
      'btn-sync-zoom','btn-open-settings','btn-health-detail','btn-api-detail','alert-bar','alert-dot','alert-title','alert-message','btn-alert-action','btn-dismiss-alert',
      'summary-total','summary-present','summary-rate','summary-time','rate-ring','metric-present','metric-absent','metric-review','metric-duplicate',
      'participants-meta','btn-refresh','btn-export','participant-search','participant-table-wrap','participant-rows','participants-empty','visible-count',
      'count-all','count-present','count-absent','count-review','count-unmatched','activity-feed','activity-empty','activity-count',
      'duplicate-list','duplicate-empty','duplicate-count','sync-summary','roster-summary','settings-dialog','roster-file','btn-upload-roster',
      'btn-reload-roster','roster-note','api-settings-status','btn-check-zoom','settings-autorefresh','autorefresh-interval','snapshot-names',
      'snapshot-parsed','chk-empty-ok','btn-submit-snapshot','btn-clear-snapshot','session-log','session-log-empty','btn-clear-log',
      'toast-region','busy','busy-text'
    ].forEach(function (id) {
      var key = id.replace(/-([a-z])/g, function (_, letter) { return letter.toUpperCase(); });
      el[key] = $(id);
    });
    el.filterButtons = Array.prototype.slice.call(document.querySelectorAll('[data-filter]'));
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
    el.btnSyncZoom.addEventListener('click', function () { syncZoomParticipants({ silent: false }); });
    el.btnRefresh.addEventListener('click', function () { refreshBoard({ notify: true }); });
    el.btnExport.addEventListener('click', exportCsv);
    el.btnOpenSettings.addEventListener('click', openSettings);
    el.btnHealthDetail.addEventListener('click', checkHealth);
    el.btnApiDetail.addEventListener('click', function () { openSettings(); checkZoomConnection(false); });
    el.btnCheckZoom.addEventListener('click', function () { checkZoomConnection(true); });
    el.btnUploadRoster.addEventListener('click', uploadRoster);
    el.btnReloadRoster.addEventListener('click', function () { loadRoster(true); });
    el.btnSubmitSnapshot.addEventListener('click', submitSnapshot);
    el.btnClearSnapshot.addEventListener('click', function () { el.snapshotNames.value = ''; updateParsedCount(); });
    el.btnClearLog.addEventListener('click', function () { state.sessionLog = []; renderSessionLog(); });
    el.snapshotNames.addEventListener('input', updateParsedCount);
    el.participantSearch.addEventListener('input', renderParticipants);
    el.filterButtons.forEach(function (button) {
      button.addEventListener('click', function () { state.filter = button.dataset.filter; syncFilterButtons(); renderParticipants(); });
    });
    el.meetingId.addEventListener('change', function () {
      writeStore(STORAGE.meetingId, currentMeetingId());
      state.selectedKey = null;
      refreshBoard({ silent: true });
    });
    el.chkAutoRefresh.addEventListener('change', function () { applyAutoRefresh(el.chkAutoRefresh.checked); });
    el.settingsAutoRefresh.addEventListener('change', function () { applyAutoRefresh(el.settingsAutoRefresh.checked); });
    el.intervalInput.addEventListener('change', function () { el.intervalInput.value = intervalSeconds(); applyAutoRefresh(el.chkAutoRefresh.checked); });
    el.alertAction.addEventListener('click', function () { if (state.alertAction) { state.alertAction(); } });
    el.btnDismissAlert.addEventListener('click', hideAlert);
    document.addEventListener('keydown', function (event) {
      if (event.key === '/' && !el.settingsDialog.open && document.activeElement !== el.participantSearch) { event.preventDefault(); el.participantSearch.focus(); }
      if ((event.key === 'r' || event.key === 'R') && !event.metaKey && !event.ctrlKey && !event.altKey && !el.settingsDialog.open && document.activeElement.tagName !== 'INPUT' && document.activeElement.tagName !== 'TEXTAREA') {
        event.preventDefault(); syncZoomParticipants({ silent: false });
      }
    });
  }

  function restoreState() {
    el.meetingId.value = readStore(STORAGE.meetingId, '');
    var savedInterval = parseInt(readStore(STORAGE.interval, '10'), 10);
    el.intervalInput.value = isNaN(savedInterval) ? '10' : String(Math.min(600, Math.max(5, savedInterval)));
    var auto = readStore(STORAGE.autoRefresh, '0') === '1';
    el.chkAutoRefresh.checked = auto;
    el.settingsAutoRefresh.checked = auto;
  }

  function init() {
    cacheElements();
    restoreState();
    bindEvents();
    renderSessionLog();
    updateParsedCount();
    applyAutoRefresh(el.chkAutoRefresh.checked);
    state.clockId = window.setInterval(updateClock, 1000);
    checkHealth();
    checkZoomConnection(false);
    loadRoster(false);
    if (currentMeetingId()) { refreshBoard({ silent: true }); }
  }

  if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', init); }
  else { init(); }
})();
