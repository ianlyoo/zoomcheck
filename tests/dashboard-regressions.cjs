'use strict';

// Dependency-free DOM/fetch/timer harness. Executes the dashboard's real source;
// all requests and clocks stay in memory. Run: node --test tests/dashboard-regressions.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const source = fs.readFileSync(path.join(__dirname, '../src/ZoomCheck.Backend/wwwroot/app.js'), 'utf8');

class Element {
  constructor(tag = 'div', document = null) {
    this.tagName = tag.toUpperCase(); this.ownerDocument = document;
    this.value = ''; this.checked = false; this.hidden = true; this.disabled = false;
    this.dataset = {}; this.children = []; this.listeners = {}; this.attributes = {};
    this.style = { setProperty() {} }; this.className = '';
    this.classList = {
      contains: value => this.className.split(/\s+/).includes(value),
      add: (...values) => { this.className = [...new Set([...this.className.split(/\s+/), ...values])].join(' ').trim(); },
      remove: (...values) => { this.className = this.className.split(/\s+/).filter(x => !values.includes(x)).join(' '); },
      toggle: (value, force) => { const on = force === undefined ? !this.classList.contains(value) : force; on ? this.classList.add(value) : this.classList.remove(value); }
    };
    this.lastChild = { textContent: '' }; this.dot = { className: '' }; this.scrollTop = 0;
  }
  set textContent(value) { this.text = String(value); for (const child of this.children) child.parentNode = null; this.children = []; }
  get textContent() { return (this.text || '') + this.children.map(child => child.textContent).join(''); }
  appendChild(child) { this.children.push(child); child.parentNode = this; return child; }
  addEventListener(name, handler) { (this.listeners[name] ||= []).push(handler); }
  dispatch(name, event = {}) {
    for (const handler of this.listeners[name] || []) handler({ target: this, stopPropagation() {}, preventDefault() {}, ...event });
  }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  getAttribute(name) { return this.attributes[name] ?? null; }
  removeAttribute(name) { delete this.attributes[name]; }
  matches(selector) {
    return selector.split(',').some(part => {
      const text = part.trim();
      if (text.startsWith('.')) return this.classList.contains(text.slice(1));
      const data = text.match(/^\[data-([a-z-]+)\]$/);
      if (data) return this.dataset[data[1].replace(/-([a-z])/g, (_, c) => c.toUpperCase())] !== undefined;
      return this.tagName.toLowerCase() === text;
    });
  }
  querySelectorAll(selector) {
    return this.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]);
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || (selector === '.status-dot' ? this.dot : null); }
  closest(selector) { return this.matches(selector) ? this : this.parentNode?.closest(selector) || null; }
  contains(node) { return this === node || this.children.some(child => child.contains(node)); }
  focus() { if (this.ownerDocument) this.ownerDocument.activeElement = this; }
  setSelectionRange(start, end) { this.selectionStart = start; this.selectionEnd = end; }
  scrollIntoView() { this.scrolledIntoView = true; }
  showModal() { this.open = true; }
  close(value = '') { this.returnValue = value; this.open = false; this.dispatch('close'); }
}

function harness({ autoConfirm = true } = {}) {
  const elements = new Map();
  const requests = [];
  const intervals = new Map();
  const timeouts = new Map();
  const confirmations = [];
  let timerId = 0;
  const document = {
    readyState: 'loading', hidden: false,
    documentElement: new Element(), activeElement: { tagName: 'BODY' },
    getElementById(id) {
      if (!elements.has(id)) {
        const element = new Element('div', document);
        if (id === 'action-confirm') {
          element.showModal = () => {
            element.open = true;
            if (autoConfirm) element.close(window.confirm(document.getElementById('action-confirm-message').textContent) ? 'apply' : 'cancel');
          };
        }
        elements.set(id, element);
      }
      return elements.get(id);
    },
    createElement: tag => new Element(tag, document),
    querySelectorAll: selector => selector === '[data-filter]' ? filters : [],
    addEventListener() {}
  };
  const filters = ['all', 'present', 'absent', 'review', 'unmatched', 'excluded'].map(filter => {
    const node = new Element('button', document); node.dataset.filter = filter; return node;
  });
  const window = {
    confirm(message) { confirmations.push(message); return true; },
    setInterval(fn, delay) { const id = ++timerId; intervals.set(id, { fn, delay }); return id; },
    setTimeout(fn, delay) { const id = ++timerId; timeouts.set(id, { fn, delay }); return id; },
    requestAnimationFrame(fn) { fn(); }
  };
  const storage = new Map([['zoomcheck.tutorialSeen.v1', 'seen']]);
  const context = vm.createContext({
    document, window,
    URL: { createObjectURL: () => 'blob:in-memory', revokeObjectURL() {} },
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
    clearInterval: id => intervals.delete(id), setTimeout: window.setTimeout,
    fetch: (url, options) => new Promise((resolve, reject) => requests.push({ url, options, resolve, reject }))
  });
  // Expose closure members only in this in-memory copy; production has no test hook.
  const instrumented = source.replace(/\}\)\(\);\s*$/, `
    globalThis.dashboard = { state, el, cacheElements, bindEvents, init,
      refreshBoard, syncZoomParticipants, submitSnapshot, requestZoomAppSync,
      renameZoomParticipant, renderBoard, beginBusy, endBusy, renderParticipants,
      setPersonExcluded, setPersonReview, matchConnection, reviewConnection, meetingScope,
      renderFreshness, renderReadiness, renderModeControls, applyAutoRefresh, applyConnectionMode,
      updateParsedCount, confidenceInfo, exportCsv, renderGroupOptions, applyGroup, installUpdate };
  })();`);
  vm.runInContext(instrumented, context, { filename: 'app.js' });
  const dashboard = context.dashboard;
  dashboard.cacheElements();
  dashboard.bindEvents();
  dashboard.el.meetingId.value = '111';
  dashboard.el.intervalInput.value = '10';
  dashboard.el.snapshotNames.value = 'Alice';
  return { ...dashboard, document, window, requests, intervals, timeouts, confirmations, filters };
}

const drain = () => new Promise(resolve => setImmediate(resolve));

test('confirmation dialog defers writes until apply and cancellation restores focus', async () => {
  const h = harness({ autoConfirm: false }); h.renderBoard(decisionBoard());
  h.el.participantSearch.focus();
  const cancelled = h.setPersonExcluded(h.state.rows[1], true);
  assert.equal(h.requests.length, 0);
  assert.equal(h.el.actionConfirm.open, true);
  assert.equal(h.document.activeElement, h.el.btnConfirmCancel);
  h.el.btnConfirmCancel.dispatch('click');
  await cancelled;
  assert.equal(h.requests.length, 0);
  assert.equal(h.document.activeElement, h.el.participantSearch);
  const applied = h.setPersonExcluded(h.state.rows[1], true);
  h.el.btnConfirmApply.dispatch('click');
  assert.equal(h.requests.length, 1);
  await respond(h.requests[0], decisionBoard());
  await applied;
});

test('dialog close via Escape never applies and a second modal cannot replace its decision', async () => {
  const h = harness({ autoConfirm: false }); h.renderBoard(decisionBoard());
  const operation = h.setPersonExcluded(h.state.rows[1], true);
  await h.setPersonReview(h.state.rows[0], 'identity', 'confirmed');
  assert.match(h.el.actionConfirmMessage.textContent, /Bob/);
  h.el.actionConfirm.close();
  await operation;
  assert.equal(h.requests.length, 0);
});

test('switching meetings while a confirmation is open prevents a later apply', async () => {
  const h = harness({ autoConfirm: false }); h.renderBoard(decisionBoard());
  const operation = h.setPersonExcluded(h.state.rows[1], true);
  select(h, '222'); select(h, '111');
  h.el.btnConfirmApply.dispatch('click');
  await operation;
  assert.equal(h.requests.length, 0);
});

test('completed manual snapshot cannot be replaced by a read started while confirmation was open', async () => {
  const h = harness({ autoConfirm: false }); h.renderBoard(board('111'));
  const operation = h.submitSnapshot();
  h.refreshBoard({ silent: true });
  h.el.btnConfirmApply.dispatch('click');
  const updated = board('111'); updated.people[0].name = 'Updated snapshot';
  await respond(h.requests[1], { presentCount: 1, board: updated });
  await operation;
  await respond(h.requests[0], board('111'));
  assert.equal(h.state.board.people[0].name, 'Updated snapshot');
});

test('installer readiness is rechecked after its confirmation closes', async () => {
  const h = harness({ autoConfirm: false });
  h.state.update = { phase: 'ready', version: '0.6.3' };
  const operation = h.installUpdate();
  h.state.update = { phase: 'idle', version: '0.6.2' };
  h.el.btnConfirmApply.dispatch('click');
  await operation;
  assert.equal(h.requests.length, 0);
});

test('repeated unmatched names form one name row without losing individual connections', () => {
  const h = harness(); const payload = decisionBoard();
  const original = payload.unmatchedParticipants[0];
  payload.unmatchedParticipants.push({ ...original });
  h.renderBoard(payload);
  const rows = h.state.rows.filter(row => row.kind === 'unmatched');
  assert.equal(rows.length, 1);
  assert.ok(rows[0].connections.length > 0);
  assert.equal(h.el.countUnmatched.textContent, '1');
});

test('review explanation translates matching evidence into operator language', () => {
  const h = harness(); const payload = decisionBoard();
  payload.people[0].confidenceReason = 'Normalized name exact match';
  h.renderBoard(payload); h.state.selectedKey = 'roster:a'; h.renderParticipants();
  assert.match(h.el.participantRows.textContent, /이름만 일치합니다/);
  assert.doesNotMatch(h.el.participantRows.textContent, /Normalized name exact match/);
});
async function respond(request, payload, status = 200) {
  request.resolve({
    ok: status >= 200 && status < 300, status,
    headers: { get: () => 'application/json' }, json: () => Promise.resolve(payload)
  });
  await drain();
}
function board(meetingId) {
  return {
    meetingId, generatedAt: '2026-09-05T01:00:00Z', attendanceDate: '2026-09-05',
    people: [{ rosterPersonId: meetingId, name: meetingId, attendanceState: 'Present', confidence: 'Verified' }],
    currentConnections: [], unmatchedParticipants: [], recentEvents: []
  };
}
function select(h, meetingId) {
  h.el.meetingId.value = meetingId;
  h.el.meetingId.dispatch('input');
}
function visibleState(h) {
  return JSON.stringify({
    board: h.state.board, pending: h.state.pending, lastSyncAt: h.state.lastSyncAt,
    lastSyncOk: h.state.lastSyncOk, nextSyncAt: h.state.nextSyncAt,
    log: h.state.sessionLog, busy: h.el.busy.hidden, busyText: h.el.busyText.textContent,
    summary: h.el.summaryTime.textContent, sync: h.el.syncSummary.lastChild.textContent,
    alert: h.el.alertBar.hidden, zoomConfigured: h.state.zoomConfigured
  });
}

const operations = {
  refresh: h => h.refreshBoard({ notify: true }),
  sync: h => h.syncZoomParticipants({ silent: false }),
  snapshot: h => h.submitSnapshot(),
  zoomApp: h => h.requestZoomAppSync({ silent: false }),
  rename: h => h.renameZoomParticipant({ presenceKey: 'zoom-app:1', rawName: 'Old', target: 'Alice' })
};
function resultFor(name, meetingId) {
  if (name === 'refresh') return board(meetingId);
  if (name === 'sync') return { activeParticipants: 1, snapshot: { board: board(meetingId) } };
  if (name === 'snapshot') return { presentCount: 1, board: board(meetingId) };
  return { requestedRevision: 1 };
}

for (const [name, start] of Object.entries(operations)) {
  for (const failed of [false, true]) {
    test(`${name}: old ${failed ? 'failure' : 'success'} cannot replace meeting B or its status`, async () => {
      const h = harness();
      h.renderBoard(board('111'));
      const operation = start(h);
      const old = h.requests[0];
      select(h, '222');
      assert.equal(h.state.board, null, 'old attendance clears immediately');
      assert.equal(h.state.pending, 0, 'old busy ownership is released on selection');
      h.syncZoomParticipants({ silent: false });
      await respond(h.requests[1], resultFor('sync', '222'));
      const before = visibleState(h);
      await respond(old, failed ? { title: 'Old failure' } : resultFor(name, '111'), failed ? 409 : 200);
      await operation;
      assert.equal(visibleState(h), before);
      assert.equal(h.requests.length, 2, 'stale callbacks must not start follow-up requests');
      assert.equal(h.state.meetingRequests.length, 0);
    });
  }
}

test('returning to meeting A still rejects responses from its previous selection', async () => {
  const h = harness();
  h.refreshBoard({ silent: true });
  select(h, '222');
  select(h, '111');
  await respond(h.requests[0], board('111'));
  assert.equal(h.state.board, null);
});

test('old completion cannot release a newer busy operation or unrelated work', async () => {
  const h = harness();
  const upload = h.beginBusy('roster upload');
  h.refreshBoard({ silent: false });
  select(h, '222');
  assert.equal(h.state.pending, 1);
  assert.equal(h.el.busyText.textContent, 'roster upload');
  h.syncZoomParticipants({ silent: false });
  const before = visibleState(h);
  await respond(h.requests[0], board('111'));
  assert.equal(visibleState(h), before);
  await respond(h.requests[1], resultFor('sync', '222'));
  assert.equal(h.state.pending, 1);
  assert.equal(h.el.busy.hidden, false);
  assert.equal(h.el.busyText.textContent, 'roster upload');
  h.endBusy(upload);
  assert.equal(h.state.pending, 0);
  assert.equal(h.el.busy.hidden, true);
});

test('unrelated completion cannot consume a meeting request busy token', async () => {
  const h = harness();
  const upload = h.beginBusy('roster upload');
  h.refreshBoard({ silent: false });
  h.endBusy(upload);
  assert.equal(h.state.pending, 1);
  assert.equal(h.el.busy.hidden, false);
  select(h, '222');
  assert.equal(h.state.pending, 0);
  await respond(h.requests[0], board('111'));
  assert.equal(h.state.pending, 0);
});

test('change handler invalidates selection even without an input event', async () => {
  const h = harness();
  h.refreshBoard({ silent: true });
  h.el.meetingId.value = '222';
  h.el.meetingId.dispatch('change');
  await respond(h.requests[1], board('222'));
  await respond(h.requests[0], board('111'));
  assert.equal(h.state.board.meetingId, '222');
});

for (const name of ['refresh', 'sync', 'snapshot']) {
  test(`${name}: current selection still renders successfully`, async () => {
    const h = harness();
    const operation = operations[name](h);
    await respond(h.requests[0], resultFor(name, '111'));
    await operation;
    assert.equal(h.state.board.meetingId, '111');
    assert.equal(h.state.pending, 0);
    assert.equal(h.el.busy.hidden, true);
  });

  test(`${name}: current failure releases busy state and reports the error`, async () => {
    const h = harness();
    const operation = operations[name](h);
    await respond(h.requests[0], { title: 'Current failure' }, 500);
    await operation;
    assert.equal(h.state.pending, 0);
    assert.equal(h.el.busy.hidden, true);
    assert.ok(h.state.sessionLog.some(entry => entry.message.includes('Current failure')));
  });
}

test('Zoom App delayed refresh does not follow the user into another meeting', async () => {
  const h = harness();
  h.requestZoomAppSync({ silent: true });
  await respond(h.requests[0], { requestedRevision: 1 });
  const delayed = [...h.timeouts.values()].find(timer => timer.delay === 2500);
  assert.ok(delayed);
  select(h, '222');
  delayed.fn();
  assert.equal(h.requests.length, 1);
});

test('rename follow-up sync cannot refresh another meeting after selection changes', async () => {
  const h = harness();
  h.state.connectionMode = 'business';
  const operation = operations.rename(h);
  await respond(h.requests[0], {});
  assert.equal(h.requests.length, 2);
  select(h, '222');
  await respond(h.requests[1], resultFor('sync', '111'));
  await operation;
  assert.equal(h.requests.length, 2);
  assert.equal(h.state.board, null);
});

for (const phase of ['idle', 'error']) {
  test(`update polling recovers from ${phase} and discovers a ready installer`, async () => {
    const h = harness();
    h.init();
    const initial = h.requests.find(request => request.url === '/api/update/status');
    if (phase === 'idle') {
      await respond(initial, { enabled: true, supportedPlatform: true, availability: 'UpToDate', downloadState: 'None' });
    } else {
      initial.reject(new Error('offline'));
      await drain();
    }
    assert.equal(h.state.update.phase, phase);
    const poll = [...h.intervals.values()].find(timer => timer.delay === 15000);
    const before = h.requests.length;
    poll.fn();
    assert.equal(h.requests.length, before + 1);
    await respond(h.requests.at(-1), {
      enabled: true, supportedPlatform: true, currentVersion: '0.6.1', latestVersion: '0.6.2',
      availability: 'UpdateAvailable', downloadState: 'Verified', installerReady: true
    });
    assert.equal(h.state.update.phase, 'ready');
    assert.equal(h.el.btnInstallUpdate.hidden, false);
    assert.equal(h.state.updateAnnouncedFor, '0.6.2');

    const logCount = h.state.sessionLog.length;
    poll.fn();
    await respond(h.requests.at(-1), {
      enabled: true, supportedPlatform: true, latestVersion: '0.6.2',
      availability: 'UpdateAvailable', downloadState: 'Verified', installerReady: true
    });
    assert.equal(h.state.sessionLog.length, logCount, 'readiness announcement stays deduplicated');

    h.document.hidden = true;
    poll.fn();
    assert.equal(h.requests.length, before + 2, 'hidden tabs still pause polling');
  });
}

function decisionBoard() {
  return {
    ...board('111'),
    people: [
      { rosterPersonId: 'a', name: 'Alice', group: 'A', attendanceState: 'Present', confidence: 'NameOnly',
        identityReviewStatus: 'pending', duplicateReviewStatus: 'pending', reviewRequired: true,
        identityEvidenceToken: 'identity-a', duplicateEvidenceToken: 'duplicate-a', confidenceReason: '이름만 일치합니다.' },
      { rosterPersonId: 'b', name: 'Bob', group: 'A', attendanceState: 'NotJoined', confidence: 'Unmatched', isExcluded: false, reviewRequired: false },
      { rosterPersonId: 'c', name: 'Carol', group: 'B', attendanceState: 'Left', confidence: 'Verified', isExcluded: true, reviewRequired: false }
    ],
    lastReceivedAt: '2026-09-05T01:00:00Z', snapshotSources: [{ source: 'web-dashboard', capturedAt: '2026-09-05T01:00:00Z' }],
    currentConnections: [
      { presenceKey: 'same-key', source: 'source-one', rawName: 'iPad', displayName: 'iPad', confidence: 'Unmatched', reviewStatus: 'pending', evidenceToken: 'connection-1' },
      { presenceKey: 'same-key', source: 'source-two', rawName: 'iPad', displayName: 'iPad', confidence: 'Unmatched', reviewStatus: 'deferred', evidenceToken: 'connection-2' },
      { presenceKey: 'alice', source: 'source-one', rawName: 'Alice phone', displayName: 'Alice', matchedRosterPersonId: 'a', manualMatch: true, evidenceToken: 'mapped-a', reviewStatus: 'none' },
      { presenceKey: 'carol', source: 'source-one', rawName: 'Carol', displayName: 'Carol', matchedRosterPersonId: 'c', evidenceToken: 'excluded-c' }
    ],
    unmatchedParticipants: [{ participantName: 'iPad', attendanceState: 'Present' }], duplicateConnectionGroups: []
  };
}

test('excluded roster people are absent from normal totals, review and rows, but can be restored', async () => {
  const h = harness();
  h.state.roster = [{ id: 'a' }, { id: 'b' }, { id: 'c' }];
  h.renderBoard(decisionBoard());
  assert.equal(h.el.summaryTotal.textContent, '2');
  assert.equal(h.el.summaryPresent.textContent, '1');
  assert.equal(h.el.countExcluded.textContent, '1');
  assert.equal(h.el.countAll.textContent, '3');
  assert.equal(h.el.countReview.textContent, '2');
  assert.match(h.el.participantsMeta.textContent, /기기 연결 3건/);
  assert.ok(!h.el.participantRows.textContent.includes('Carol'));
  h.state.filter = 'excluded'; h.renderParticipants();
  assert.match(h.el.participantRows.textContent, /Carol/);
  const row = h.state.rows.find(row => row.rosterPersonId === 'c');
  const operation = h.setPersonExcluded(row, false);
  assert.equal(h.requests[0].url, '/api/meetings/111/people/c/exclusion');
  assert.deepEqual(JSON.parse(h.requests[0].options.body), { excluded: false, attendanceDate: '2026-09-05' });
  const restored = decisionBoard(); restored.people[2].isExcluded = false;
  await respond(h.requests[0], restored); await operation;
  assert.equal(h.el.summaryTotal.textContent, '3');
  assert.equal(h.el.countExcluded.textContent, '0');
});

test('all-excluded board has zero denominator even with a loaded global roster', () => {
  const h = harness(); const payload = decisionBoard();
  payload.people.forEach(person => { person.isExcluded = true; });
  payload.currentConnections = []; payload.unmatchedParticipants = [];
  h.state.roster = [{ id: 'a' }, { id: 'b' }, { id: 'c' }]; h.renderBoard(payload);
  assert.equal(h.el.summaryTotal.textContent, '0');
  assert.equal(h.el.summaryPresent.textContent, '0');
  assert.equal(h.el.countReview.textContent, '0');
  assert.equal(h.el.countAll.textContent, '0');
  assert.equal(h.el.countExcluded.textContent, '3');
  assert.equal(h.el.btnExport.disabled, true);
  assert.match(h.el.emptyNote.textContent, /제외됨/);
});

test('exclusion confirms the human identity, meeting and date', () => {
  const h = harness(); h.renderBoard(decisionBoard());
  h.setPersonExcluded(h.state.rows.find(row => row.rosterPersonId === 'b'), true);
  assert.match(h.confirmations[0], /회의 111/);
  assert.match(h.confirmations[0], /2026-09-05 · Bob · A/);
  assert.doesNotMatch(h.confirmations[0], /\(b\)/);
  assert.match(h.confirmations[0], /다음 날 다시 포함/);
  assert.ok(!('expectedEvidenceToken' in JSON.parse(h.requests[0].options.body)));
});

test('currently connected people can be excluded with an explicit connection-preserving warning', () => {
  const h = harness(); h.renderBoard(decisionBoard());
  h.state.selectedKey = 'roster:a'; h.renderParticipants();
  const action = h.el.participantRows.querySelectorAll('button').find(button => button.dataset.focusKey === 'roster:a:exclusion');
  assert.ok(action);
  action.dispatch('click');
  assert.equal(h.requests.length, 1);
  assert.equal(h.requests[0].url, '/api/meetings/111/people/a/exclusion');
  assert.match(h.confirmations[0], /현재 연결 중인 참가자입니다/);
  assert.match(h.confirmations[0], /Zoom 연결은 유지되고 오늘 집계에서만 제외됩니다/);
  assert.equal(JSON.parse(h.requests[0].options.body).excluded, true);
});

test('midnight exclusion conflict preserves the old roster and requests explicit reload', async () => {
  const h = harness(); h.renderBoard(decisionBoard());
  const operation = h.setPersonExcluded(h.state.rows[1], true);
  await respond(h.requests[0], { title: 'Attendance date changed' }, 409); await operation;
  assert.equal(h.state.rows[1].isExcluded, false);
  assert.match(h.el.alertTitle.textContent, /날짜/);
  assert.equal(h.requests.length, 1);
  h.state.alertAction();
  assert.equal(h.requests[1].url, '/api/meetings/111/board');
});

for (const kind of ['identity', 'duplicate']) {
  test(`${kind} review sends only its own evidence token and preserves factual confidence`, async () => {
    const h = harness(); h.renderBoard(decisionBoard());
    const row = h.state.rows[0];
    const operation = h.setPersonReview(row, kind, 'confirmed');
    assert.deepEqual(JSON.parse(h.requests[0].options.body), { kind, status: 'confirmed', expectedEvidenceToken: `${kind}-a` });
    const updated = decisionBoard(); updated.people[0][kind + 'ReviewStatus'] = 'confirmed';
    await respond(h.requests[0], updated); await operation;
    assert.equal(h.state.rows[0].confidence, 'NameOnly');
    assert.equal(h.state.rows[0][kind + 'ReviewStatus'], 'confirmed');
    assert.equal(h.state.rows[0][(kind === 'identity' ? 'duplicate' : 'identity') + 'ReviewStatus'], 'pending');
    h.setPersonReview(h.state.rows[0], kind, 'pending');
    assert.equal(JSON.parse(h.requests[1].options.body).status, 'pending');
  });
}

test('deferred reviews remain in review; confirmed evidence does not turn a NameOnly badge green', () => {
  const h = harness(); const payload = decisionBoard();
  payload.people[0].identityReviewStatus = 'deferred';
  h.renderBoard(payload); h.state.selectedKey = 'roster:a'; h.renderParticipants();
  assert.match(h.el.participantRows.textContent, /신원 · 보류/);
  assert.equal(h.state.rows[0].review, true);
  payload.people[0].identityReviewStatus = 'confirmed'; payload.people[0].duplicateReviewStatus = 'confirmed'; payload.people[0].reviewRequired = false;
  h.renderBoard(payload);
  assert.equal(h.state.rows[0].review, false);
  assert.equal(h.confidenceInfo(h.state.rows[0]).className, 'review');
  assert.equal(h.confidenceInfo(h.state.rows[1]).className, 'neutral');
  assert.equal(h.confidenceInfo(h.state.rows[1]).label, '입장 전');
});

test('same-name connections with the same key in different sources retain separate controls and tokens', async () => {
  const h = harness(); h.renderBoard(decisionBoard());
  const unmatched = h.state.rows.find(row => row.kind === 'unmatched');
  h.state.selectedKey = unmatched.key; h.renderParticipants();
  assert.equal(unmatched.connections.length, 2);
  const selects = h.el.participantRows.querySelectorAll('select');
  assert.equal(selects.length, 2);
  assert.notEqual(selects[0].dataset.focusKey, selects[1].dataset.focusKey);
  assert.match(h.el.participantRows.textContent, /보류/);
  assert.equal(h.el.participantRows.querySelectorAll('button').filter(button => button.textContent === '이 명단 인물과 연결').every(button => button.disabled), true);
  const operation = h.matchConnection(unmatched.connections[1], 'b');
  assert.equal(h.requests[0].url, '/api/meetings/111/connections/match');
  assert.deepEqual(JSON.parse(h.requests[0].options.body), { source: 'source-two', presenceKey: 'same-key', rosterPersonId: 'b', expectedEvidenceToken: 'connection-2' });
  await respond(h.requests[0], decisionBoard()); await operation;
  assert.match(h.confirmations[0], /영구 별명은 저장하지 않습니다/);
});

test('manual mapping undo sends explicit null and unmatched defer uses connection evidence', () => {
  const h = harness(); h.renderBoard(decisionBoard());
  h.matchConnection(h.state.connections[2], null);
  assert.equal(JSON.parse(h.requests[0].options.body).rosterPersonId, null);
  assert.equal(JSON.parse(h.requests[0].options.body).expectedEvidenceToken, 'mapped-a');
  h.reviewConnection(h.state.connections[1], 'pending');
  assert.deepEqual(JSON.parse(h.requests[1].options.body), { source: 'source-two', presenceKey: 'same-key', status: 'pending', expectedEvidenceToken: 'connection-2' });
});

test('excluded people cannot be manual-match targets and missing evidence never submits', () => {
  const h = harness(); h.renderBoard(decisionBoard());
  h.matchConnection(h.state.connections[0], 'c');
  h.matchConnection({ ...h.state.connections[0], evidenceToken: '' }, 'b');
  h.setPersonReview({ ...h.state.rows[0], identityEvidenceToken: '' }, 'identity', 'confirmed');
  assert.equal(h.requests.length, 0);
});

test('evidence conflict does not optimistically confirm or automatically retry', async () => {
  const h = harness(); h.renderBoard(decisionBoard());
  const operation = h.setPersonReview(h.state.rows[0], 'identity', 'confirmed');
  await respond(h.requests[0], { title: 'Evidence changed' }, 409); await operation;
  assert.equal(h.state.rows[0].identityReviewStatus, 'pending');
  assert.equal(h.state.pending, 0);
  assert.equal(h.requests.length, 1);
  assert.match(h.el.alertTitle.textContent, /참가자 정보가 변경/);
});

test('old rendered action and cancelled confirmation cannot mutate the new selection', () => {
  const h = harness(); h.renderBoard(decisionBoard()); const scope = h.meetingScope(); const row = h.state.rows[1];
  select(h, '222'); h.setPersonExcluded(row, true, scope);
  assert.equal(h.requests.length, 0);
  select(h, '111'); h.renderBoard(decisionBoard());
  h.window.confirm = () => false; h.setPersonExcluded(h.state.rows[1], true);
  assert.equal(h.requests.length, 0);
});

test('old mutation response and pre-mutation board read cannot overwrite current decisions', async () => {
  const h = harness(); h.renderBoard(decisionBoard());
  h.refreshBoard({ silent: true });
  const operation = h.setPersonReview(h.state.rows[0], 'identity', 'confirmed');
  const updated = decisionBoard(); updated.people[0].identityReviewStatus = 'confirmed';
  await respond(h.requests[1], updated); await operation;
  await respond(h.requests[0], decisionBoard());
  assert.equal(h.state.rows[0].identityReviewStatus, 'confirmed');
  h.setPersonExcluded(h.state.rows[1], true);
  select(h, '222'); h.renderBoard(board('222')); const before = visibleState(h);
  await respond(h.requests[2], updated);
  assert.equal(visibleState(h), before);
});

test('receipt timestamp survives screen reads, and an accepted request stays pending until a new snapshot', async () => {
  const h = harness(); h.renderBoard(decisionBoard());
  const receipt = h.el.lastSyncTime.textContent;
  const reread = decisionBoard(); reread.generatedAt = '2026-09-05T02:00:00Z'; h.renderBoard(reread);
  assert.equal(h.el.lastSyncTime.textContent, receipt);
  h.requestZoomAppSync({ silent: true }); await respond(h.requests[0], { requestedRevision: 12 });
  assert.equal(h.state.awaitingSnapshot, true);
  assert.equal(h.el.lastSyncTime.textContent, receipt);
  assert.match(h.el.sourceStatus.textContent, /수신 대기/);
  reread.lastReceivedAt = '2026-09-05T01:00:05Z'; h.renderBoard(reread);
  assert.equal(h.state.awaitingSnapshot, false);
  assert.notEqual(h.el.lastSyncTime.textContent, receipt);
});

test('event-only boards label recent activity, never participant receipt', () => {
  const h = harness(); const payload = board('111'); payload.latestEventAt = '2026-09-05T01:10:00Z'; h.renderBoard(payload);
  assert.match(h.el.summaryTime.textContent, /^최근 활동/);
  assert.equal(h.el.lastSyncTime.textContent, '–');
});

test('manual mode primary action opens its textarea and auto-sync cannot claim activity', () => {
  const h = harness(); h.applyConnectionMode('manual'); h.applyAutoRefresh(true);
  assert.equal(h.el.btnSyncZoom.textContent, '참가자 목록 입력');
  assert.equal(h.el.chkAutoRefresh.disabled, true);
  assert.equal(h.el.autoStatus.textContent, '직접 입력');
  assert.equal(h.state.nextSyncAt, null);
  h.el.btnSyncZoom.dispatch('click');
  assert.equal(h.document.activeElement, h.el.snapshotNames);
  assert.equal(h.requests.length, 0);
});

test('manual list shows dedup, confirms exact meeting and exposes returned changes', async () => {
  const h = harness(); h.el.snapshotNames.value = 'Alice\n Alice \nBob\n\n'; h.updateParsedCount();
  assert.equal(h.el.snapshotParsed.textContent, '입력 3줄 · 서로 다른 이름 2개 · 중복 1줄');
  const operation = h.submitSnapshot();
  assert.match(h.confirmations[0], /회의 111/); assert.match(h.confirmations[0], /서로 다른 이름 2개/);
  assert.deepEqual(JSON.parse(h.requests[0].options.body).participantNames, ['Alice', 'Bob']);
  await respond(h.requests[0], { board: decisionBoard(), joinedNames: ['Alice'], leftNames: ['Carol'], ignoredNames: ['Host'] }); await operation;
  assert.match(h.el.snapshotResultSummary.textContent, /입장 1 · 퇴장 1 · 처리 제외 1/);
  assert.match(h.el.snapshotResultDetails.textContent, /퇴장: Carol/);
});

test('readiness guides empty roster, missing meeting and first participant receipt', () => {
  const h = harness(); h.state.rosterLoaded = true; h.renderReadiness();
  assert.equal(h.el.btnNextStep.textContent, '명단 올리기');
  h.el.btnNextStep.dispatch('click'); assert.equal(h.document.activeElement, h.el.rosterFile);
  h.state.roster = [{ id: 'a' }]; select(h, ''); h.renderReadiness();
  assert.equal(h.el.btnNextStep.textContent, '회의 ID 입력');
  select(h, '111'); h.applyConnectionMode('manual'); h.renderReadiness();
  assert.equal(h.el.btnNextStep.textContent, '참가자 목록 입력');
});

for (const [status, label] of [[403, '권한 설정 확인'], [404, '회의 ID 확인'], [429, '간격 조정'], [502, '다시 시도']]) {
  test(`sync error ${status} offers ${label} with separate technical detail`, async () => {
    const h = harness(); h.syncZoomParticipants({ silent: false });
    await respond(h.requests[0], { title: 'Technical detail' }, status);
    assert.equal(h.el.alertAction.textContent, label);
    assert.match(h.el.alertTechnical.textContent, /Technical detail/);
  });
}

test('row disclosure, review action and connection drafts retain focus across renders', () => {
  const h = harness(); h.renderBoard(decisionBoard());
  const toggle = h.el.participantRows.querySelectorAll('button').find(button => button.dataset.focusKey === 'row:roster:a');
  toggle.focus(); toggle.dispatch('click');
  assert.equal(h.document.activeElement.dataset.focusKey, 'row:roster:a');
  const confirm = h.el.participantRows.querySelectorAll('button').find(button => button.dataset.focusKey === 'roster:a:identity:confirm');
  confirm.focus(); h.renderBoard(decisionBoard());
  assert.equal(h.document.activeElement.dataset.focusKey, confirm.dataset.focusKey);
  h.state.selectedKey = 'unmatched:ipad'; h.renderParticipants();
  const search = h.el.participantRows.querySelectorAll('input')[1];
  search.focus(); search.value = 'Bo'; search.dispatch('input'); search.setSelectionRange(1, 2);
  h.renderBoard(decisionBoard());
  assert.equal(h.document.activeElement.dataset.focusKey, search.dataset.focusKey);
  assert.equal(h.document.activeElement.value, 'Bo');
  assert.equal(h.document.activeElement.selectionStart, 1);
});

test('new filter and search results start at the top while refresh preserves reading position', () => {
  const h = harness(); h.renderBoard(decisionBoard());
  h.el.participantTableWrap.scrollTop = 450;
  h.renderBoard(decisionBoard());
  assert.equal(h.el.participantTableWrap.scrollTop, 450);
  h.filters.find(button => button.dataset.filter === 'excluded').dispatch('click');
  assert.equal(h.el.participantTableWrap.scrollTop, 0);
  h.el.participantTableWrap.scrollTop = 250;
  h.el.participantSearch.value = 'Carol'; h.el.participantSearch.dispatch('input');
  assert.equal(h.el.participantTableWrap.scrollTop, 0);
  h.el.participantTableWrap.scrollTop = 250;
  h.applyGroup('A');
  assert.equal(h.el.participantTableWrap.scrollTop, 0);
  h.el.participantTableWrap.scrollTop = 250;
  h.el.btnReview.dispatch('click');
  assert.equal(h.el.participantTableWrap.scrollTop, 0);
});

test('filter buttons expose aria-pressed and keep excluded filter separate', () => {
  const h = harness(); h.renderBoard(decisionBoard());
  h.filters.find(button => button.dataset.filter === 'excluded').dispatch('click');
  assert.equal(h.filters.find(button => button.dataset.filter === 'excluded').getAttribute('aria-pressed'), 'true');
  assert.equal(h.filters.find(button => button.dataset.filter === 'all').getAttribute('aria-pressed'), 'false');
  assert.match(h.el.participantRows.textContent, /Carol/);
  assert.ok(!h.el.participantRows.textContent.includes('Alice'));
});

test('group scope and CSV label omit exclusions but preserve unmatched review visibility', () => {
  const h = harness(); h.renderBoard(decisionBoard()); h.applyGroup('A');
  assert.equal(h.el.summaryTotal.textContent, '2');
  assert.equal(h.el.countUnmatched.textContent, '1');
  assert.match(h.el.btnExport.textContent, /A 명단 CSV · 2명/);
  assert.match(h.el.exportScope.textContent, /검색·상태 필터와 미매칭 항목은 미포함/);
  h.state.filter = 'review'; h.el.participantSearch.value = 'Alice';
  h.exportCsv();
  assert.equal(h.requests[0].url, '/api/meetings/111/export?group=A');
});

test('activity and connection details translate known sources without guessing shared connector provenance', () => {
  const h = harness(); const data = decisionBoard();
  data.currentConnections[0].source = 'web-dashboard';
  data.recentEvents = ['web-dashboard', 'zoom-app', 'zoom-api', 'zoom-live-participants'].map((source, i) => ({
    id: String(i), source, participantName: 'Alice', eventType: 'Joined', occurredAt: '2026-09-05T01:00:00Z'
  }));
  h.renderBoard(data);
  for (const label of ['직접 입력', 'Zoom 앱', 'Zoom API', 'Zoom 참가자']) assert.ok(h.el.activityFeed.textContent.includes(label));
  assert.doesNotMatch(h.el.activityFeed.textContent, /web-dashboard|zoom-live-participants/);
  h.state.selectedKey = 'unmatched:ipad'; h.renderParticipants();
  assert.match(h.el.participantRows.textContent, /직접 입력/);
  assert.doesNotMatch(h.el.participantRows.textContent, /web-dashboard/);
});

test('duplicate person is an accessible button that clears filters, expands, focuses and scrolls to its row', () => {
  const h = harness(); const data = decisionBoard();
  data.duplicateConnectionGroups = [{ rosterPersonId: 'a', personName: 'Alice', connections: [
    data.currentConnections[2], { ...data.currentConnections[2], presenceKey: 'alice-2' }
  ] }];
  h.renderBoard(data);
  h.state.filter = 'absent'; h.el.participantSearch.value = 'Bob'; h.renderParticipants();
  const button = h.el.duplicateList.querySelector('button');
  assert.equal(button.type, 'button');
  assert.equal(button.getAttribute('aria-label'), 'Alice 참가자 상세 보기');
  button.dispatch('click');
  assert.equal(h.state.filter, 'all'); assert.equal(h.el.participantSearch.value, '');
  assert.equal(h.state.selectedKey, 'roster:a');
  assert.equal(h.document.activeElement.dataset.focusKey, 'row:roster:a');
  assert.equal(h.document.activeElement.getAttribute('aria-expanded'), 'true');
  assert.equal(h.document.activeElement.scrolledIntoView, true);
});
