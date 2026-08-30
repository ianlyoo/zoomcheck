/* ZoomCheck 로컬 대시보드 — 의존성 없는 순수 JavaScript
 * 모든 요청은 이 페이지를 서비스하는 로컬 백엔드(같은 origin)로만 전송됩니다.
 */
(function () {
  'use strict';

  var STORAGE_KEYS = {
    meetingId: 'zoomcheck.meetingId',
    autoRefresh: 'zoomcheck.autoRefresh',
    interval: 'zoomcheck.autoRefreshSeconds',
    activeTab: 'zoomcheck.activeTab'
  };

  var MAX_SNAPSHOT = 2000;
  var MAX_ROSTER_BYTES = 20 * 1024 * 1024;
  var MAX_LOG_ENTRIES = 60;

  var CONFIDENCE_LABELS = {
    Verified: '확인됨',
    AliasVerified: '별명 확인',
    NameOnly: '이름만 일치',
    Possible: '추정 일치',
    Unmatched: '미매칭'
  };

  var CONFIDENCE_TONE = {
    Verified: 'ok',
    AliasVerified: 'ok',
    NameOnly: 'warn',
    Possible: 'warn',
    Unmatched: 'bad'
  };

  var STATE_LABELS = {
    Present: '참석 중',
    Left: '퇴장',
    NotJoined: '미입장'
  };

  var STATE_TONE = {
    Present: 'ok',
    Left: 'warn',
    NotJoined: 'bad'
  };

  var EVENT_LABELS = {
    Joined: '입장',
    Left: '퇴장'
  };

  var REVIEW_CONFIDENCES = ['NameOnly', 'Possible'];

  var TABS = ['present', 'absent', 'review', 'unmatched', 'events', 'activity'];

  var state = {
    board: null,
    rosterCount: null,
    lastSnapshot: null,
    timerId: null,
    pending: 0,
    log: [],
    lastSyncAt: null,
    lastSyncOk: null,
    zoomConfigured: false
  };

  function $(id) {
    return document.getElementById(id);
  }

  var el = {};

  /* ---------- 유틸 ---------- */

  function setText(node, value) {
    if (node) {
      node.textContent = value;
    }
  }

  function pad(n) {
    return n < 10 ? '0' + n : String(n);
  }

  function formatTime(value) {
    if (!value) {
      return '–';
    }
    var d = new Date(value);
    if (isNaN(d.getTime())) {
      return String(value);
    }
    return pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
  }

  function formatDateTime(value) {
    if (!value) {
      return '–';
    }
    var d = new Date(value);
    if (isNaN(d.getTime())) {
      return String(value);
    }
    return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) +
      ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
  }

  function readStore(key, fallback) {
    try {
      var raw = window.localStorage.getItem(key);
      return raw === null ? fallback : raw;
    } catch (err) {
      return fallback;
    }
  }

  function writeStore(key, value) {
    try {
      window.localStorage.setItem(key, String(value));
    } catch (err) {
      /* 프라이빗 모드 등에서 저장 실패는 무시 */
    }
  }

  function toast(kind, title, message) {
    logActivity(kind, title, message);
    var box = document.createElement('div');
    box.className = 'toast is-' + kind;
    box.setAttribute('role', kind === 'bad' ? 'alert' : 'status');
    var strong = document.createElement('strong');
    strong.textContent = title;
    box.appendChild(strong);
    if (message) {
      var span = document.createElement('span');
      span.textContent = message;
      box.appendChild(span);
    }
    el.toastRegion.appendChild(box);
    window.setTimeout(function () {
      if (box.parentNode) {
        box.parentNode.removeChild(box);
      }
    }, kind === 'bad' ? 9000 : 5000);
  }

  function beginBusy(label) {
    state.pending += 1;
    setText(el.busyText, label || '처리 중…');
    el.busy.hidden = false;
  }

  function endBusy() {
    state.pending = Math.max(0, state.pending - 1);
    if (state.pending === 0) {
      el.busy.hidden = true;
    }
  }

  /* ---------- 활동 로그 ---------- */

  /**
   * 세션 활동 로그에 항목을 추가합니다. 브라우저 메모리에만 보관하며 서버로 보내지 않습니다.
   */
  function logActivity(kind, title, message) {
    state.log.unshift({
      at: new Date(),
      kind: kind || 'info',
      title: title || '',
      message: message || ''
    });
    if (state.log.length > MAX_LOG_ENTRIES) {
      state.log.length = MAX_LOG_ENTRIES;
    }
    renderActivityLog();
  }

  function renderActivityLog() {
    if (!el.activityLog) {
      return;
    }
    setText(el.cntActivity, String(state.log.length));
    el.activityLog.textContent = '';
    if (!state.log.length) {
      el.emptyActivity.hidden = false;
      el.activityLog.hidden = true;
      return;
    }
    el.emptyActivity.hidden = true;
    el.activityLog.hidden = false;
    var fragment = document.createDocumentFragment();
    state.log.forEach(function (entry) {
      var li = document.createElement('li');
      li.className = 'log-item is-' + entry.kind;
      var time = document.createElement('time');
      time.className = 'log-time';
      time.dateTime = entry.at.toISOString();
      time.textContent = formatTime(entry.at);
      var title = document.createElement('span');
      title.className = 'log-title';
      title.textContent = entry.title;
      li.appendChild(time);
      li.appendChild(title);
      if (entry.message) {
        var detail = document.createElement('span');
        detail.className = 'log-detail';
        detail.textContent = entry.message;
        li.appendChild(detail);
      }
      fragment.appendChild(li);
    });
    el.activityLog.appendChild(fragment);
  }

  /* ---------- 동기화 상태 ---------- */

  function markSync(ok, note) {
    state.lastSyncAt = new Date();
    state.lastSyncOk = ok;
    updateSyncLine(note);
  }

  function updateSyncLine(note) {
    if (!el.syncText) {
      return;
    }
    var auto = el.chkAutoRefresh.checked;
    var parts = [];
    parts.push(auto ? '실시간 동기화 켜짐 (' + intervalSeconds() + '초 간격)' : '실시간 동기화 꺼짐');
    if (state.lastSyncAt) {
      parts.push('마지막 동기화 ' + formatTime(state.lastSyncAt) + (state.lastSyncOk ? '' : ' · 실패'));
    }
    if (note) {
      parts.push(note);
    }
    setText(el.syncText, parts.join(' · '));
    el.syncDot.classList.toggle('is-ok', state.lastSyncOk === true);
    el.syncDot.classList.toggle('is-bad', state.lastSyncOk === false);
  }

  /* ---------- API ---------- */

  /**
   * ProblemDetails(RFC 7807)를 사람이 읽을 수 있는 메시지로 변환합니다.
   */
  function describeProblem(problem, status) {
    if (!problem || typeof problem !== 'object') {
      return 'HTTP ' + status + ' 오류가 발생했습니다.';
    }
    var parts = [];
    if (problem.title) {
      parts.push(String(problem.title));
    }
    if (problem.detail) {
      parts.push(String(problem.detail));
    }
    if (problem.errors && typeof problem.errors === 'object') {
      Object.keys(problem.errors).forEach(function (key) {
        var messages = problem.errors[key];
        if (Array.isArray(messages)) {
          parts.push(key + ': ' + messages.join(' '));
        }
      });
    }
    if (parts.length === 0) {
      parts.push('HTTP ' + status + ' 오류가 발생했습니다.');
    }
    return parts.join(' — ');
  }

  function ApiError(message, status) {
    this.name = 'ApiError';
    this.message = message;
    this.status = status;
  }
  ApiError.prototype = Object.create(Error.prototype);

  function request(path, options) {
    var opts = options || {};
    return fetch(path, {
      method: opts.method || 'GET',
      headers: opts.headers || (opts.json !== undefined
        ? { 'Content-Type': 'application/json', Accept: 'application/json' }
        : { Accept: 'application/json' }),
      body: opts.json !== undefined ? JSON.stringify(opts.json) : opts.body,
      cache: 'no-store',
      credentials: 'same-origin'
    }).then(function (response) {
      var contentType = response.headers.get('content-type') || '';
      if (!response.ok) {
        if (contentType.indexOf('json') !== -1) {
          return response.json().then(function (payload) {
            throw new ApiError(describeProblem(payload, response.status), response.status);
          }, function () {
            throw new ApiError('HTTP ' + response.status + ' 응답을 해석할 수 없습니다.', response.status);
          });
        }
        return response.text().then(function (raw) {
          var trimmed = (raw || '').trim();
          throw new ApiError(
            trimmed ? 'HTTP ' + response.status + ': ' + trimmed.slice(0, 300) : 'HTTP ' + response.status + ' 오류',
            response.status);
        });
      }
      if (opts.raw) {
        return response;
      }
      if (response.status === 204 || contentType.indexOf('json') === -1) {
        return null;
      }
      return response.json();
    }, function () {
      throw new ApiError('백엔드에 연결할 수 없습니다. 로컬 서버가 실행 중인지 확인하세요.', 0);
    });
  }

  /* ---------- 상태 확인 ---------- */

  function checkHealth() {
    return request('/health').then(function (payload) {
      var ok = payload && payload.status === 'ok';
      el.healthDot.classList.toggle('is-ok', !!ok);
      el.healthDot.classList.toggle('is-bad', !ok);
      setText(el.healthText, ok ? '백엔드 연결됨 (로컬)' : '백엔드가 예상과 다른 응답을 보냈습니다.');
    }, function (err) {
      el.healthDot.classList.remove('is-ok');
      el.healthDot.classList.add('is-bad');
      setText(el.healthText, '백엔드 연결 실패 — ' + err.message);
    });
  }

  function checkZoomConnection(showToast) {
    return request('/api/zoom/connection-status').then(function (status) {
      state.zoomConfigured = !!(status && status.configured);
      el.zoomDot.classList.toggle('is-ok', state.zoomConfigured);
      el.zoomDot.classList.toggle('is-bad', !state.zoomConfigured);
      setText(el.zoomStatus, state.zoomConfigured
        ? 'OAuth 자격증명 설정됨 · 필요 권한 ' + (status.requiredScope || 'dashboard:read:list_meeting_participants:admin')
        : 'OAuth 미설정 · Windows 사용자 환경 변수 3개를 설정한 뒤 ZoomCheck를 다시 시작하세요.');
      if (showToast) {
        toast(state.zoomConfigured ? 'ok' : 'warn',
          state.zoomConfigured ? 'Zoom API 설정 확인됨' : 'Zoom API 설정 필요',
          state.zoomConfigured ? '실제 권한은 [지금 참가자 불러오기]에서 검증됩니다.' :
            'ZOOMCHECK_Zoom__AccountId / ClientId / ClientSecret을 설정하세요.');
      }
      return status;
    }, function (err) {
      state.zoomConfigured = false;
      el.zoomDot.classList.remove('is-ok');
      el.zoomDot.classList.add('is-bad');
      setText(el.zoomStatus, 'Zoom API 설정 상태를 확인하지 못했습니다 — ' + err.message);
      if (showToast) { toast('bad', 'Zoom API 상태 확인 실패', err.message); }
    });
  }

  /* ---------- 명단 ---------- */

  function loadRoster(showToast) {
    beginBusy('명단을 불러오는 중…');
    return request('/api/roster').then(function (people) {
      var list = Array.isArray(people) ? people : [];
      state.rosterCount = list.length;
      setText(el.rosterCount, String(list.length));
      if (list.length === 0) {
        setText(el.rosterNote, '등록된 명단이 없습니다. 엑셀(.xlsx / .xls) 파일을 올려 주세요.');
      } else {
        var withEmail = list.filter(function (p) { return p.email; }).length;
        var orgs = {};
        list.forEach(function (p) {
          if (p.organization) { orgs[p.organization] = true; }
        });
        setText(el.rosterNote,
          '명단이 등록되어 있습니다. 이메일 보유 ' + withEmail + '명, 소속 ' + Object.keys(orgs).length + '개.');
      }
      setText(el.statRoster, String(list.length));
      if (showToast) {
        toast('ok', '명단 불러오기 완료', list.length + '명을 확인했습니다.');
      }
      return list;
    }, function (err) {
      state.rosterCount = null;
      setText(el.rosterCount, '–');
      setText(el.rosterNote, '명단을 불러오지 못했습니다 — ' + err.message);
      if (showToast) {
        toast('bad', '명단 불러오기 실패', err.message);
      }
    }).then(function (result) {
      endBusy();
      return result;
    }, function (err) {
      endBusy();
      throw err;
    });
  }

  function uploadRoster() {
    var file = el.rosterFile.files && el.rosterFile.files[0];
    if (!file) {
      toast('warn', '파일을 선택하세요', '업로드할 Excel 명단 파일을 먼저 선택해 주세요.');
      el.rosterFile.focus();
      return;
    }
    var lower = file.name.toLowerCase();
    if (lower.slice(-5) !== '.xlsx' && lower.slice(-4) !== '.xls') {
      toast('warn', '지원하지 않는 형식', 'Excel .xlsx 또는 .xls 파일만 올릴 수 있습니다.');
      return;
    }
    if (file.size > MAX_ROSTER_BYTES) {
      toast('warn', '파일이 너무 큽니다', '20MB 이하의 Excel 파일을 사용해 주세요.');
      return;
    }

    var form = new FormData();
    form.append('file', file, file.name);

    beginBusy('명단을 업로드하는 중…');
    setText(el.rosterNote, '업로드 중입니다…');

    request('/api/roster/upload', {
      method: 'POST',
      headers: { Accept: 'application/json' },
      body: form
    }).then(function (payload) {
      var count = payload && (payload.count !== undefined ? payload.count : payload.imported);
      var name = payload && payload.displayName ? payload.displayName : file.name;
      toast('ok', '명단 업로드 완료',
        name + (count !== undefined && count !== null ? ' · ' + count + '명' : ''));
      el.rosterFile.value = '';
      endBusy();
      return loadRoster(false);
    }, function (err) {
      endBusy();
      if (err.status === 404 || err.status === 405) {
        setText(el.rosterNote, '이 백엔드에는 업로드 엔드포인트(POST /api/roster/upload)가 아직 없습니다.');
        toast('bad', '업로드 엔드포인트 없음',
          '백엔드가 POST /api/roster/upload 를 지원하지 않습니다. 최신 ZoomCheck로 다시 설치하세요.');
      } else {
        setText(el.rosterNote, '업로드 실패 — ' + err.message);
        toast('bad', '명단 업로드 실패', err.message);
      }
    });
  }

  /* ---------- 스냅샷 ---------- */

  function parseNames(raw) {
    var seen = {};
    var names = [];
    String(raw || '').split(/\r?\n/).forEach(function (line) {
      var name = line.replace(/\s+/g, ' ').trim();
      if (!name) {
        return;
      }
      var key = name.toLowerCase();
      if (seen[key]) {
        return;
      }
      seen[key] = true;
      names.push(name);
    });
    return names;
  }

  function updateParsedCount() {
    var names = parseNames(el.snapshotNames.value);
    var text = '인식된 이름: ' + names.length + '명';
    if (names.length > MAX_SNAPSHOT) {
      text += ' — 최대 ' + MAX_SNAPSHOT + '명을 초과했습니다.';
    }
    setText(el.snapshotParsed, text);
    return names;
  }

  function currentMeetingId() {
    return el.meetingId.value.trim();
  }

  function requireMeetingId() {
    var id = currentMeetingId();
    if (!id) {
      el.meetingId.setAttribute('aria-invalid', 'true');
      el.meetingId.focus();
      toast('warn', '회의 ID가 필요합니다', '먼저 Zoom 회의 ID를 입력하세요.');
      return null;
    }
    el.meetingId.removeAttribute('aria-invalid');
    return id;
  }

  function submitSnapshot() {
    var meetingId = requireMeetingId();
    if (!meetingId) {
      return;
    }
    var names = updateParsedCount();
    if (names.length > MAX_SNAPSHOT) {
      toast('bad', '목록이 너무 깁니다', '한 번에 최대 ' + MAX_SNAPSHOT + '명까지 보낼 수 있습니다.');
      return;
    }
    if (names.length === 0 && !el.chkEmptyOk.checked) {
      toast('warn', '붙여넣은 이름이 없습니다',
        '전원 퇴장으로 기록하려면 [빈 목록 제출 허용]을 체크하세요.');
      el.snapshotNames.focus();
      return;
    }

    beginBusy('스냅샷을 적용하는 중…');
    request('/api/meetings/' + encodeURIComponent(meetingId) + '/participant-snapshot', {
      method: 'POST',
      json: {
        participantNames: names,
        source: 'web-dashboard',
        capturedAt: new Date().toISOString()
      }
    }).then(function (result) {
      endBusy();
      applySnapshotResult(result);
      toast('ok', '스냅샷 적용 완료',
        '현재 참석 ' + (result && result.presentCount !== undefined ? result.presentCount : names.length) + '명으로 갱신했습니다.');
    }, function (err) {
      endBusy();
      toast('bad', '스냅샷 적용 실패', err.message);
      setText(el.boardMeta, '스냅샷 적용 실패 — ' + err.message);
    });
  }

  function syncZoomParticipants(options) {
    var opts = options || {};
    var meetingId = opts.silent ? currentMeetingId() : requireMeetingId();
    if (!meetingId) { return Promise.resolve(); }
    var allowEmpty = !opts.silent && !!(el.chkApiEmptyOk && el.chkApiEmptyOk.checked);
    var syncPath = '/api/zoom/meetings/' + encodeURIComponent(meetingId) + '/sync' +
      (allowEmpty ? '?allowEmptySnapshot=true' : '');

    if (!opts.silent) { beginBusy('Zoom 현재 참가자를 불러오는 중…'); }
    return request(syncPath, { method: 'POST' })
      .then(function (result) {
        if (!opts.silent) { endBusy(); }
        if (allowEmpty) { el.chkApiEmptyOk.checked = false; }
        state.zoomConfigured = true;
        el.zoomDot.classList.add('is-ok');
        el.zoomDot.classList.remove('is-bad');
        setText(el.zoomStatus, 'Zoom API 연결됨 · 현재 참가자 ' + result.activeParticipants + '명');
        applySnapshotResult(result.snapshot);
        markSync(true, 'Zoom 참가자 ' + result.activeParticipants + '명');
        logActivity('ok', 'Zoom API 동기화',
          '현재 ' + result.activeParticipants + '명 · 입장 ' + result.snapshot.joinedNames.length +
          '명 · 퇴장 ' + result.snapshot.leftNames.length + '명');
        if (!opts.silent) { toast('ok', 'Zoom 참가자 동기화 완료', '현재 ' + result.activeParticipants + '명'); }
        return result;
      }, function (err) {
        if (!opts.silent) { endBusy(); }
        markSync(false, err.message);
        logActivity('bad', 'Zoom API 동기화 실패', err.message);
        if (!opts.silent) { toast('bad', 'Zoom 참가자 동기화 실패', err.message); }
      });
  }

  /**
   * ParticipantSnapshotResult 처리: 변경 요약 + 포함된 보드 렌더링.
   */
  function applySnapshotResult(result) {
    if (!result || typeof result !== 'object') {
      return;
    }
    state.lastSnapshot = result;

    var joined = Array.isArray(result.joinedNames) ? result.joinedNames : [];
    var left = Array.isArray(result.leftNames) ? result.leftNames : [];
    var ignored = Array.isArray(result.ignoredNames) ? result.ignoredNames : [];

    el.diffList.textContent = '';
    var rows = [
      ['적용 시각', formatDateTime(result.capturedAt) + ' · 출처 ' + (result.source || '–')],
      ['현재 참석으로 기록', (result.presentCount !== undefined ? result.presentCount : 0) + '명'],
      ['새로 입장', joined.length + '명' + (joined.length ? ' — ' + joined.join(', ') : '')],
      ['퇴장 처리', left.length + '명' + (left.length ? ' — ' + left.join(', ') : '')],
      ['무시된 항목', ignored.length + '건' + (ignored.length ? ' — ' + ignored.join(', ') : '')]
    ];
    rows.forEach(function (pair) {
      var li = document.createElement('li');
      var b = document.createElement('strong');
      b.textContent = pair[0] + ': ';
      li.appendChild(b);
      li.appendChild(document.createTextNode(pair[1]));
      el.diffList.appendChild(li);
    });
    el.snapshotDiff.hidden = false;

    if (result.board) {
      renderBoard(result.board);
    }
  }

  /* ---------- 보드 ---------- */

  function refreshBoard(options) {
    var opts = options || {};
    var meetingId = opts.silent ? currentMeetingId() : requireMeetingId();
    if (!meetingId) {
      return Promise.resolve();
    }
    if (!opts.silent) {
      beginBusy('보드를 불러오는 중…');
    }
    return request('/api/meetings/' + encodeURIComponent(meetingId) + '/board').then(function (board) {
      if (!opts.silent) {
        endBusy();
      }
      renderBoard(board);
      markSync(true, '');
      if (opts.notify) {
        toast('ok', '보드 새로고침 완료', '');
      }
    }, function (err) {
      if (!opts.silent) {
        endBusy();
      }
      setText(el.boardMeta, '보드를 불러오지 못했습니다 — ' + err.message);
      markSync(false, err.message);
      if (!opts.silent) {
        toast('bad', '보드 불러오기 실패', err.message);
      } else {
        logActivity('bad', '자동 동기화 실패', err.message);
      }
    });
  }

  function isReview(person) {
    return REVIEW_CONFIDENCES.indexOf(person.confidence) !== -1;
  }

  function renderBoard(board) {
    if (!board || typeof board !== 'object') {
      return;
    }
    state.board = board;

    var people = Array.isArray(board.people) ? board.people : [];
    var unmatched = Array.isArray(board.unmatchedParticipants) ? board.unmatchedParticipants : [];
    var events = Array.isArray(board.recentEvents) ? board.recentEvents : [];

    var present = people.filter(function (p) { return p.attendanceState === 'Present'; });
    var absent = people.filter(function (p) { return p.attendanceState !== 'Present'; });
    var review = people.filter(isReview);

    setText(el.boardMeta,
      '회의 ' + (board.meetingId || '–') + ' · 생성 시각 ' + formatDateTime(board.generatedAt) +
      ' · 명단 ' + people.length + '명');

    setText(el.statRoster, String(state.rosterCount !== null ? state.rosterCount : people.length));
    setText(el.statPresent, String(present.length));
    setText(el.statAbsent, String(absent.length));
    setText(el.statReview, String(review.length));
    setText(el.statUnmatched, String(unmatched.length));
    setText(el.statRate, people.length
      ? Math.round((present.length / people.length) * 100) + '%'
      : '–');

    setText(el.cntPresent, String(present.length));
    setText(el.cntAbsent, String(absent.length));
    setText(el.cntReview, String(review.length));
    setText(el.cntUnmatched, String(unmatched.length));
    setText(el.cntEvents, String(events.length));

    renderPresent(present);
    renderAbsent(absent);
    renderReview(review);
    renderUnmatched(unmatched);
    renderEvents(events);
  }

  function cell(row, value, className) {
    var td = document.createElement('td');
    td.textContent = value === null || value === undefined || value === '' ? '–' : String(value);
    if (className) {
      td.className = className;
    }
    row.appendChild(td);
    return td;
  }

  function tagCell(row, label, tone) {
    var td = document.createElement('td');
    var span = document.createElement('span');
    span.className = 'tag' + (tone ? ' ' + tone : '');
    span.textContent = label;
    td.appendChild(span);
    row.appendChild(td);
    return td;
  }

  function fillTable(tbody, emptyNode, rows, builder) {
    tbody.textContent = '';
    if (!rows.length) {
      emptyNode.hidden = false;
      tbody.closest('.table-scroll').hidden = true;
      return;
    }
    emptyNode.hidden = true;
    tbody.closest('.table-scroll').hidden = false;
    var fragment = document.createDocumentFragment();
    rows.forEach(function (item) {
      var tr = document.createElement('tr');
      builder(tr, item);
      fragment.appendChild(tr);
    });
    tbody.appendChild(fragment);
  }

  function confidenceLabel(value) {
    return CONFIDENCE_LABELS[value] || value || '–';
  }

  function renderPresent(rows) {
    fillTable(el.tbodyPresent, el.emptyPresent, rows, function (tr, p) {
      cell(tr, p.sequence, 'num');
      cell(tr, p.name);
      cell(tr, p.organization);
      tagCell(tr, confidenceLabel(p.confidence), CONFIDENCE_TONE[p.confidence]);
      cell(tr, formatTime(p.lastJoinedAt), 'num');
      cell(tr, p.joinCount, 'num');
    });
  }

  function renderAbsent(rows) {
    fillTable(el.tbodyAbsent, el.emptyAbsent, rows, function (tr, p) {
      cell(tr, p.sequence, 'num');
      cell(tr, p.name);
      cell(tr, p.organization);
      tagCell(tr, STATE_LABELS[p.attendanceState] || p.attendanceState, STATE_TONE[p.attendanceState]);
      cell(tr, formatTime(p.lastLeftAt), 'num');
      cell(tr, p.joinCount, 'num');
    });
  }

  function renderReview(rows) {
    fillTable(el.tbodyReview, el.emptyReview, rows, function (tr, p) {
      cell(tr, p.sequence, 'num');
      cell(tr, p.name);
      cell(tr, p.organization);
      tagCell(tr, STATE_LABELS[p.attendanceState] || p.attendanceState, STATE_TONE[p.attendanceState]);
      tagCell(tr, confidenceLabel(p.confidence), CONFIDENCE_TONE[p.confidence]);
      cell(tr, p.confidenceReason);
    });
  }

  function renderUnmatched(rows) {
    fillTable(el.tbodyUnmatched, el.emptyUnmatched, rows, function (tr, u) {
      cell(tr, u.participantName);
      tagCell(tr, STATE_LABELS[u.attendanceState] || u.attendanceState, STATE_TONE[u.attendanceState]);
      cell(tr, formatDateTime(u.lastSeenAt), 'num');
      cell(tr, u.eventCount, 'num');
    });
  }

  function renderEvents(rows) {
    fillTable(el.tbodyEvents, el.emptyEvents, rows, function (tr, e) {
      cell(tr, formatDateTime(e.occurredAt), 'num');
      tagCell(tr, EVENT_LABELS[e.eventType] || e.eventType, e.eventType === 'Joined' ? 'ok' : 'warn');
      cell(tr, e.participantName);
      tagCell(tr, confidenceLabel(e.confidence), CONFIDENCE_TONE[e.confidence]);
      cell(tr, e.source);
    });
  }

  /* ---------- CSV ---------- */

  function exportCsv() {
    var meetingId = requireMeetingId();
    if (!meetingId) {
      return;
    }
    beginBusy('CSV를 준비하는 중…');
    request('/api/meetings/' + encodeURIComponent(meetingId) + '/export', { raw: true })
      .then(function (response) {
        return response.blob();
      })
      .then(function (blob) {
        endBusy();
        var url = URL.createObjectURL(blob);
        var link = document.createElement('a');
        link.href = url;
        link.download = meetingId + '-attendance.csv';
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        window.setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
        toast('ok', 'CSV 내려받기 시작', meetingId + '-attendance.csv');
      }, function (err) {
        endBusy();
        toast('bad', 'CSV 내려받기 실패', err.message);
      });
  }

  /* ---------- 자동 새로고침 ---------- */

  function intervalSeconds() {
    var value = parseInt(el.intervalInput.value, 10);
    if (isNaN(value) || value < 5) {
      value = 5;
    }
    if (value > 600) {
      value = 600;
    }
    return value;
  }

  function stopAutoRefresh() {
    if (state.timerId !== null) {
      window.clearInterval(state.timerId);
      state.timerId = null;
    }
  }

  function syncAutoRefresh() {
    stopAutoRefresh();
    writeStore(STORAGE_KEYS.autoRefresh, el.chkAutoRefresh.checked ? '1' : '0');
    writeStore(STORAGE_KEYS.interval, intervalSeconds());
    if (!el.chkAutoRefresh.checked) {
      updateSyncLine('');
      return;
    }
    state.timerId = window.setInterval(function () {
      if (document.hidden || state.pending > 0 || !currentMeetingId()) {
        return;
      }
      syncZoomParticipants({ silent: true });
    }, intervalSeconds() * 1000);
    updateSyncLine('');
  }

  /* ---------- 탭 ---------- */

  function activateTab(name, focusTab) {
    TABS.forEach(function (key) {
      var tab = el.tabs[key];
      var panel = el.panels[key];
      var selected = key === name;
      tab.setAttribute('aria-selected', selected ? 'true' : 'false');
      tab.tabIndex = selected ? 0 : -1;
      panel.hidden = !selected;
    });
    writeStore(STORAGE_KEYS.activeTab, name);
    if (focusTab) {
      el.tabs[name].focus();
    }
  }

  function bindTabs() {
    TABS.forEach(function (key, index) {
      var tab = el.tabs[key];
      tab.addEventListener('click', function () {
        activateTab(key, false);
      });
      tab.addEventListener('keydown', function (event) {
        var next = null;
        if (event.key === 'ArrowRight' || event.key === 'ArrowDown') {
          next = TABS[(index + 1) % TABS.length];
        } else if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') {
          next = TABS[(index - 1 + TABS.length) % TABS.length];
        } else if (event.key === 'Home') {
          next = TABS[0];
        } else if (event.key === 'End') {
          next = TABS[TABS.length - 1];
        }
        if (next) {
          event.preventDefault();
          activateTab(next, true);
        }
      });
    });
  }

  /* ---------- 초기화 ---------- */

  function cacheElements() {
    el.healthDot = $('health-dot');
    el.healthText = $('health-text');
    el.meetingId = $('meeting-id');
    el.rosterFile = $('roster-file');
    el.rosterCount = $('roster-count');
    el.rosterNote = $('roster-note');
    el.snapshotNames = $('snapshot-names');
    el.snapshotParsed = $('snapshot-parsed');
    el.chkEmptyOk = $('chk-empty-ok');
    el.boardMeta = $('board-meta');
    el.statRoster = $('stat-roster');
    el.statPresent = $('stat-present');
    el.statAbsent = $('stat-absent');
    el.statReview = $('stat-review');
    el.statUnmatched = $('stat-unmatched');
    el.statRate = $('stat-rate');
    el.snapshotDiff = $('snapshot-diff');
    el.diffList = $('snapshot-diff-list');
    el.cntPresent = $('cnt-present');
    el.cntAbsent = $('cnt-absent');
    el.cntReview = $('cnt-review');
    el.cntUnmatched = $('cnt-unmatched');
    el.cntEvents = $('cnt-events');
    el.tbodyPresent = $('tbody-present');
    el.tbodyAbsent = $('tbody-absent');
    el.tbodyReview = $('tbody-review');
    el.tbodyUnmatched = $('tbody-unmatched');
    el.tbodyEvents = $('tbody-events');
    el.emptyPresent = $('empty-present');
    el.emptyAbsent = $('empty-absent');
    el.emptyReview = $('empty-review');
    el.emptyUnmatched = $('empty-unmatched');
    el.emptyEvents = $('empty-events');
    el.chkAutoRefresh = $('chk-autorefresh');
    el.chkApiEmptyOk = $('chk-api-empty-ok');
    el.intervalInput = $('autorefresh-interval');
    el.zoomDot = $('zoom-dot');
    el.zoomStatus = $('zoom-status');
    el.syncDot = $('sync-dot');
    el.syncText = $('sync-text');
    el.activityLog = $('activity-log');
    el.emptyActivity = $('empty-activity');
    el.cntActivity = $('cnt-activity');
    el.toastRegion = $('toast-region');
    el.busy = $('busy');
    el.busyText = $('busy-text');
    el.tabs = {};
    el.panels = {};
    TABS.forEach(function (key) {
      el.tabs[key] = $('tab-' + key);
      el.panels[key] = $('panel-' + key);
    });
  }

  function bindEvents() {
    $('setup-form').addEventListener('submit', function (event) {
      event.preventDefault();
      refreshBoard({ notify: true });
    });
    $('btn-upload-roster').addEventListener('click', uploadRoster);
    $('btn-reload-roster').addEventListener('click', function () { loadRoster(true); });
    $('btn-submit-snapshot').addEventListener('click', submitSnapshot);
    $('btn-clear-snapshot').addEventListener('click', function () {
      el.snapshotNames.value = '';
      updateParsedCount();
      el.snapshotNames.focus();
    });
    $('btn-refresh').addEventListener('click', function () { refreshBoard({ notify: true }); });
    $('btn-sync-zoom').addEventListener('click', function () { syncZoomParticipants({ silent: false }); });
    $('btn-check-zoom').addEventListener('click', function () { checkZoomConnection(true); });
    $('btn-export').addEventListener('click', exportCsv);
    $('btn-clear-log').addEventListener('click', function () {
      state.log = [];
      renderActivityLog();
    });

    el.snapshotNames.addEventListener('input', updateParsedCount);
    el.meetingId.addEventListener('change', function () {
      writeStore(STORAGE_KEYS.meetingId, currentMeetingId());
      if (currentMeetingId()) {
        el.meetingId.removeAttribute('aria-invalid');
        syncZoomParticipants({ silent: true });
      }
    });
    el.chkAutoRefresh.addEventListener('change', syncAutoRefresh);
    el.intervalInput.addEventListener('change', syncAutoRefresh);
    document.addEventListener('visibilitychange', function () {
      if (!document.hidden && el.chkAutoRefresh.checked && currentMeetingId()) {
        refreshBoard({ silent: true });
      }
    });
    bindTabs();
  }

  function restoreState() {
    var savedMeeting = readStore(STORAGE_KEYS.meetingId, '');
    if (savedMeeting) {
      el.meetingId.value = savedMeeting;
    }
    var savedInterval = parseInt(readStore(STORAGE_KEYS.interval, '10'), 10);
    if (!isNaN(savedInterval)) {
      el.intervalInput.value = String(Math.min(600, Math.max(5, savedInterval)));
    }
    el.chkAutoRefresh.checked = readStore(STORAGE_KEYS.autoRefresh, '0') === '1';
    var savedTab = readStore(STORAGE_KEYS.activeTab, 'present');
    activateTab(TABS.indexOf(savedTab) !== -1 ? savedTab : 'present', false);
  }

  function init() {
    cacheElements();
    restoreState();
    bindEvents();
    updateParsedCount();
    renderActivityLog();
    updateSyncLine('');
    checkHealth();
    checkZoomConnection(false);
    loadRoster(false);
    if (currentMeetingId()) {
      refreshBoard({ silent: true });
    } else {
      setText(el.boardMeta, '회의 ID를 입력하고 [보드 새로고침]을 누르세요.');
    }
    syncAutoRefresh();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
