'use strict';

// Dependency-free DOM/fetch/timer harness. Executes the dashboard's real source;
// all requests and clocks stay in memory. Run: node --test tests/dashboard-regressions.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const source = fs.readFileSync(path.join(__dirname, '../src/ZoomCheck.Backend/wwwroot/app.js'), 'utf8');
const localesSource = fs.readFileSync(path.join(__dirname, '../src/ZoomCheck.Backend/wwwroot/locales.js'), 'utf8');
const markup = fs.readFileSync(path.join(__dirname, '../src/ZoomCheck.Backend/wwwroot/index.html'), 'utf8');
const stylesSource = fs.readFileSync(path.join(__dirname, '../src/ZoomCheck.Backend/wwwroot/styles.css'), 'utf8');

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
  get childNodes() { return this.children; }
  replaceChildren(...children) { this.textContent = ''; children.forEach(child => this.appendChild(child)); }
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
  click() { this.clicked = true; }
  remove() { if (this.parentNode) this.parentNode.children = this.parentNode.children.filter(child => child !== this); }
  showModal() { this.open = true; }
  close(value = '') { this.returnValue = value; this.open = false; this.dispatch('close'); }
}

function harness({ autoConfirm = true, storage = new Map([['zoomcheck.tutorialSeen.v1', 'seen']]), clipboard } = {}) {
  const elements = new Map();
  const saved = [];
  const requests = [];
  const intervals = new Map();
  const timeouts = new Map();
  const confirmations = [];
  let timerId = 0;
  const docListeners = {};
  const document = {
    readyState: 'loading', hidden: false,
    documentElement: new Element(), body: new Element('body'), activeElement: { tagName: 'BODY' },
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
    createTextNode: text => ({ textContent: String(text) }),
    querySelectorAll: selector => selector === '[data-filter]' ? filters : selector === '[data-sort]' ? sortButtons : [],
    addEventListener(name, handler) { (docListeners[name] ||= []).push(handler); },
    dispatch(name, event = {}) {
      for (const handler of docListeners[name] || []) handler({ preventDefault() {}, ...event });
    }
  };
  const filters = ['all', 'present', 'absent', 'review', 'unmatched', 'excluded'].map(filter => {
    const node = new Element('button', document); node.dataset.filter = filter; return node;
  });
  const sortButtons = ['number', 'name'].map(key => {
    const header = new Element('th', document); const button = new Element('button', document);
    button.dataset.sort = key; header.appendChild(button); return button;
  });
  const window = {
    confirm(message) { confirmations.push(message); return true; },
    setInterval(fn, delay) { const id = ++timerId; intervals.set(id, { fn, delay }); return id; },
    setTimeout(fn, delay) { const id = ++timerId; timeouts.set(id, { fn, delay }); return id; },
    requestAnimationFrame(fn) { fn(); }
  };
  const context = vm.createContext({
    document, window,
    URL: { createObjectURL: blob => { saved.push(blob); return 'blob:in-memory'; }, revokeObjectURL() {} },
    Blob: class { constructor(parts, options) { this.parts = parts; this.type = options.type; } },
    FormData: class { constructor() { this.entries = []; } append(...args) { this.entries.push(args); } },
    navigator: { clipboard },
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) },
    clearInterval: id => intervals.delete(id), setTimeout: window.setTimeout,
    fetch: (url, options) => new Promise((resolve, reject) => requests.push({ url, options, resolve, reject }))
  });
  vm.runInContext(localesSource, context, { filename: 'locales.js' });
  // Expose closure members only in this in-memory copy; production has no test hook.
  const instrumented = source.replace(/\}\)\(\);\s*$/, `
    globalThis.dashboard = { state, el, cacheElements, bindEvents, init,
      refreshBoard, syncZoomParticipants, submitSnapshot, requestZoomAppSync,
      renameZoomParticipant, renderBoard, beginBusy, endBusy, renderParticipants,
      setPersonExcluded, setPersonReview, matchConnection, reviewConnection, meetingScope,
      renderFreshness, renderReadiness, renderModeControls, applyAutoRefresh, applyConnectionMode,
      updateParsedCount, confidenceInfo, exportCsv, renderGroupOptions, applyGroup, installUpdate,
      loadRelaySettings, saveRelaySettings, checkRelayHealth, pairingDisabledReason, renderRelayHealth,
      createPairingCode, checkHealth, checkZoomConnection, renderZoomAppStrip, renderUpdate, applyUpdateStatus,
      applyLanguage, toggleLanguage, restoreState, t,
      uploadRoster, renderRosterFeedback,
      applySort, buildStatusText, copyMeetingStatus, downloadStatusText };
  })();`);
  vm.runInContext(instrumented, context, { filename: 'app.js' });
  const dashboard = context.dashboard;
  dashboard.cacheElements();
  dashboard.bindEvents();
  dashboard.el.meetingId.value = '111';
  dashboard.el.intervalInput.value = '10';
  dashboard.el.snapshotNames.value = 'Alice';
  return { ...dashboard, document, window, requests, intervals, timeouts, confirmations, filters, sortButtons, saved };
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
  assert.equal(h.el.countAll.textContent, '2');
  assert.equal(h.el.countPresent.textContent, '1');
  assert.equal(h.el.countAbsent.textContent, '1');
  assert.equal(h.el.countReview.textContent, '2');
  assert.equal(h.el.countUnmatched.textContent, '1');
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
  h.state.filter = 'unmatched'; h.state.selectedKey = unmatched.key; h.renderParticipants();
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
  h.state.filter = 'unmatched'; h.state.selectedKey = 'unmatched:ipad'; h.renderParticipants();
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

test('group scope and tab counts follow the roster and exclude unmatched under a group filter', () => {
  const h = harness(); h.renderBoard(decisionBoard()); h.applyGroup('A');
  assert.equal(h.el.summaryTotal.textContent, '2');
  assert.equal(h.el.countAll.textContent, '2');
  assert.equal(h.el.countPresent.textContent, '1');
  assert.equal(h.el.countAbsent.textContent, '1');
  assert.equal(h.el.countReview.textContent, '1');
  assert.equal(h.el.countUnmatched.textContent, '0');
  assert.match(h.el.scopeNote.textContent, /미매칭 1개는 전체 그룹에서 확인/);
  assert.match(h.el.btnExport.textContent, /A 명단 CSV · 2명/);
  assert.match(h.el.exportScope.textContent, /검색·상태 필터와 미매칭 항목은 미포함/);
  h.state.filter = 'unmatched'; h.renderParticipants();
  assert.match(h.el.emptyTitle.textContent, /그룹 필터에서는 미매칭을 표시하지 않습니다/);
  assert.match(h.el.emptyNote.textContent, /미매칭 1개/);
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
  h.state.filter = 'unmatched'; h.state.selectedKey = 'unmatched:ipad'; h.renderParticipants();
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

const activeRelay = { configured: true, baseUrl: 'https://old.example/', restartRequired: false };
const updatedRelay = { configured: true, baseUrl: 'https://new.example/', restartRequired: false };
const pendingRelay = { configured: true, baseUrl: 'https://new.example/', restartRequired: true };
async function loadRelay(h, settings = activeRelay) {
  const operation = h.loadRelaySettings();
  await respond(h.requests.at(-1), settings); await operation;
}

test('disabled pairing button always explains why it is disabled and clears the reason once relay is saved', async () => {
  const h = harness();
  h.renderZoomAppStrip();
  assert.equal(h.el.btnCreatePairingCode.disabled, true);
  assert.equal(h.el.pairingDisabledReason.hidden, false);
  assert.match(h.el.pairingDisabledReason.textContent, /릴레이 설정을 확인하는 중/);
  assert.equal(h.el.btnCreatePairingCode.getAttribute('aria-describedby'), 'pairing-disabled-reason');

  await loadRelay(h, { configured: false, baseUrl: '', restartRequired: false });
  assert.equal(h.el.btnCreatePairingCode.disabled, true);
  assert.equal(h.el.pairingDisabledReason.hidden, false);
  assert.match(h.el.pairingDisabledReason.textContent, /HTTPS 릴레이 주소를 입력하고 저장/);
  assert.equal(h.el.btnCopyHomeUrl.disabled, true);

  h.applyLanguage('en');
  assert.match(h.el.pairingDisabledReason.textContent, /Enter and save an HTTPS relay URL/);
  h.applyLanguage('es');
  assert.match(h.el.pairingDisabledReason.textContent, /URL de relay HTTPS/);
  h.applyLanguage('ko');

  h.el.relayBaseUrl.value = updatedRelay.baseUrl;
  const save = h.saveRelaySettings();
  assert.equal(h.el.btnCreatePairingCode.disabled, true);
  assert.match(h.el.pairingDisabledReason.textContent, /저장하는 중/);
  await respond(h.requests.at(-1), updatedRelay); await save;

  assert.equal(h.el.btnCreatePairingCode.disabled, false);
  assert.equal(h.el.pairingDisabledReason.hidden, true);
  assert.equal(h.el.btnCreatePairingCode.getAttribute('aria-describedby'), null);
  assert.equal(h.el.zoomAppHomeUrl.textContent, 'https://new.example/zoom-app/');
  assert.equal(h.el.btnCopyHomeUrl.disabled, false);
});

test('saving a new relay URL applies immediately without restart, clears stale code, and allows pairing right away', async () => {
  const h = harness(); h.state.zoomApp = { homeUrl: 'https://old.example/zoom-app/' };
  await loadRelay(h);
  h.state.pairing = { code: '123456' };
  h.el.relayBaseUrl.value = updatedRelay.baseUrl;
  const save = h.saveRelaySettings();
  await respond(h.requests.at(-1), updatedRelay); await save;
  assert.equal(h.state.pairing, null);
  assert.equal(h.el.btnCreatePairingCode.disabled, false);
  assert.doesNotMatch(h.el.pairingCode.textContent, /1 2 3 4 5 6/);
  assert.match(h.el.relaySettingsNote.textContent, /바로 적용/);
  assert.doesNotMatch(h.el.relaySettingsNote.textContent, /완전히 종료/);

  const code = h.createPairingCode();
  assert.equal(h.requests.at(-1).url, '/api/zoom-app/pairing-code');
  await respond(h.requests.at(-1), { code: '654321', homeUrl: 'https://new.example/zoom-app/' }); await code;
  assert.equal(h.state.pairing.code, '654321');
  assert.equal(h.el.btnCopyPairingCode.disabled, false);
});

test('relay health check tests /health and displays clear success or failure reasons', async () => {
  const h = harness();
  await loadRelay(h, { configured: false, baseUrl: '', restartRequired: false });

  await h.checkRelayHealth();
  assert.equal(h.requests.length, 1);
  assert.equal(h.el.relayHealthStatus.hidden, false);
  assert.equal(h.el.relayHealthStatus.classList.contains('is-bad'), true);
  assert.match(h.el.relayHealthStatus.textContent, /비어 있습니다/);

  h.el.relayBaseUrl.value = 'http://insecure.example/';
  await h.checkRelayHealth();
  assert.equal(h.requests.length, 1);
  assert.match(h.el.relayHealthStatus.textContent, /https:\/\//);

  h.el.relayBaseUrl.value = 'https://relay.example/';
  const okCheck = h.checkRelayHealth();
  assert.equal(h.el.btnCheckRelayHealth.disabled, true);
  assert.equal(h.requests.at(-1).url, '/api/settings/zoom-relay/health');
  assert.deepEqual(JSON.parse(h.requests.at(-1).options.body), { baseUrl: 'https://relay.example/' });
  await respond(h.requests.at(-1), { ok: true, baseUrl: 'https://relay.example/', statusCode: 200, latencyMs: 42 });
  await okCheck;
  assert.equal(h.el.btnCheckRelayHealth.disabled, false);
  assert.equal(h.el.relayHealthStatus.classList.contains('is-ok'), true);
  assert.match(h.el.relayHealthStatus.textContent, /연결 확인됨 \(42ms\)/);

  const timeoutCheck = h.checkRelayHealth();
  await respond(h.requests.at(-1), { ok: false, baseUrl: 'https://relay.example/', errorCode: 'timeout', latencyMs: 10000 });
  await timeoutCheck;
  assert.equal(h.el.relayHealthStatus.classList.contains('is-bad'), true);
  assert.match(h.el.relayHealthStatus.textContent, /응답 시간이 초과/);

  const http503Check = h.checkRelayHealth();
  await respond(h.requests.at(-1), { ok: false, baseUrl: 'https://relay.example/', errorCode: 'http_error', statusCode: 503 });
  await http503Check;
  assert.match(h.el.relayHealthStatus.textContent, /HTTP 503/);

  const invalidPayloadCheck = h.checkRelayHealth();
  await respond(h.requests.at(-1), { ok: false, baseUrl: 'https://relay.example/', errorCode: 'invalid_response', statusCode: 200 });
  await invalidPayloadCheck;
  assert.match(h.el.relayHealthStatus.textContent, /\/health 응답이 아닙니다/);

  h.el.relayBaseUrl.dispatch('input');
  assert.equal(h.el.relayHealthStatus.hidden, true);
});

test('unconfigured relay preserves direct HTTPS pairing, while legacy restartRequired still blocks pairing with a reason', async () => {
  const h = harness(); h.state.zoomApp = { homeUrl: 'https://direct.example/' };
  await loadRelay(h, { configured: false, baseUrl: '', restartRequired: false });
  assert.equal(h.el.btnCreatePairingCode.disabled, false);
  assert.equal(h.el.pairingDisabledReason.hidden, true);
  const code = h.createPairingCode();
  assert.equal(h.requests.at(-1).url, '/api/zoom-app/pairing-code');
  await respond(h.requests.at(-1), { code: '654321', homeUrl: 'https://direct.example/' }); await code;
  assert.equal(h.state.pairing.code, '654321');
  assert.equal(h.state.pending, 0);
  await loadRelay(h, { ...pendingRelay, configured: false });
  assert.equal(h.el.btnCreatePairingCode.disabled, true);
  assert.equal(h.el.pairingDisabledReason.hidden, false);
  assert.match(h.el.pairingDisabledReason.textContent, /다시 실행/);
  assert.equal(h.state.pairing, null);
});

test('overlapping loads and saves cannot erase newly saved relay settings or submit concurrent writes', async () => {
  const h = harness(); h.state.zoomApp = { homeUrl: activeRelay.baseUrl };
  await loadRelay(h);
  const staleLoad = h.loadRelaySettings(); const staleRequest = h.requests.at(-1);
  h.el.relayBaseUrl.value = updatedRelay.baseUrl;
  const save = h.saveRelaySettings(); const saveRequest = h.requests.at(-1);
  assert.equal(h.el.btnSaveRelayUrl.disabled, true);
  assert.equal(h.el.btnCreatePairingCode.disabled, true);
  const count = h.requests.length;
  await h.saveRelaySettings(); await h.loadRelaySettings();
  assert.equal(h.requests.length, count);
  await respond(saveRequest, updatedRelay); await save;
  await respond(staleRequest, activeRelay); await staleLoad;
  assert.equal(h.state.relaySettings.baseUrl, updatedRelay.baseUrl);
  assert.equal(h.el.relayBaseUrl.value, updatedRelay.baseUrl);
  assert.equal(h.el.btnCreatePairingCode.disabled, false);
  assert.equal(h.el.btnSaveRelayUrl.disabled, false);
  const first = h.loadRelaySettings(); const firstRequest = h.requests.at(-1);
  const second = h.loadRelaySettings(); const secondRequest = h.requests.at(-1);
  await respond(secondRequest, updatedRelay); await second;
  await respond(firstRequest, activeRelay); await first;
  assert.equal(h.state.relaySettings.baseUrl, updatedRelay.baseUrl);
});

for (const success of [true, false]) {
  test(`relay save ${success ? 'success' : 'failure'} releases only its busy token and failure retains prior state`, async () => {
    const h = harness(); h.state.zoomApp = { homeUrl: activeRelay.baseUrl }; await loadRelay(h);
    h.state.pairing = { code: '123456' };
    const other = h.beginBusy('Other operation');
    h.el.relayBaseUrl.value = updatedRelay.baseUrl;
    const save = h.saveRelaySettings();
    assert.equal(h.state.pending, 2);
    await respond(h.requests.at(-1), success ? updatedRelay : { title: 'Invalid relay URL' }, success ? 200 : 400); await save;
    assert.equal(h.state.pending, 1); assert.equal(h.el.busy.hidden, false);
    assert.equal(h.el.busyText.textContent, 'Other operation');
    assert.equal(h.el.btnSaveRelayUrl.disabled, false);
    assert.equal(h.state.relaySettings.baseUrl, success ? updatedRelay.baseUrl : activeRelay.baseUrl);
    if (!success) {
      assert.equal(h.state.pairing.code, '123456');
      assert.equal(h.el.btnCreatePairingCode.disabled, false);
    }
    h.endBusy(other); assert.equal(h.state.pending, 0);
  });
}

test('client-invalid save leaves previous relay and code state intact without a request', async () => {
  const h = harness(); h.state.zoomApp = { homeUrl: activeRelay.baseUrl }; await loadRelay(h);
  h.state.pairing = { code: '123456' }; h.el.relayBaseUrl.value = 'http://invalid.example/';
  const count = h.requests.length; await h.saveRelaySettings();
  assert.equal(h.requests.length, count); assert.equal(h.state.pending, 0);
  assert.equal(h.state.relaySettings.baseUrl, activeRelay.baseUrl);
  assert.equal(h.state.pairing.code, '123456');
});

test('saving an empty relay URL clears it back to the unconfigured state', async () => {
  const h = harness();
  await loadRelay(h, activeRelay);
  assert.equal(h.el.zoomAppHomeUrl.textContent, 'https://old.example/zoom-app/');
  h.state.pairing = { code: '123456' };
  h.el.relayBaseUrl.value = '   ';
  const clear = h.saveRelaySettings();
  assert.equal(h.requests.at(-1).url, '/api/settings/zoom-relay');
  assert.equal(h.requests.at(-1).options.method, 'PUT');
  assert.deepEqual(JSON.parse(h.requests.at(-1).options.body), { baseUrl: '' });
  await respond(h.requests.at(-1), { configured: false, baseUrl: '', restartRequired: false });
  await clear;

  assert.equal(h.state.relaySettings.configured, false);
  assert.equal(h.state.relaySettings.baseUrl, '');
  assert.equal(h.el.relayBaseUrl.value, '');
  assert.equal(h.state.pairing, null);
  assert.equal(h.el.btnCreatePairingCode.disabled, true);
  assert.equal(h.el.pairingDisabledReason.hidden, false);
  assert.match(h.el.pairingDisabledReason.textContent, /HTTPS 릴레이 주소를 입력하고 저장/);
  assert.match(h.el.zoomAppHomeUrl.textContent, /릴레이 주소 미설정/);
  assert.equal(h.el.btnCopyHomeUrl.disabled, true);
  assert.match(h.el.toastRegion.textContent, /릴레이 주소를 지웠습니다/);
});

test('pairing response started before endpoint save cannot redisplay an obsolete code', async () => {
  const h = harness(); h.state.zoomApp = { homeUrl: activeRelay.baseUrl }; await loadRelay(h);
  const code = h.createPairingCode(); const codeRequest = h.requests.at(-1);
  h.el.relayBaseUrl.value = updatedRelay.baseUrl;
  const save = h.saveRelaySettings();
  await respond(h.requests.at(-1), updatedRelay); await save;
  assert.equal(h.state.pending, 1);
  await respond(codeRequest, { code: '123456', homeUrl: activeRelay.baseUrl }); await code;
  assert.equal(h.state.pairing, null); assert.equal(h.state.pending, 0);
  assert.equal(h.el.btnCreatePairingCode.disabled, false);
});

test('language switching cycles through Korean, English and Spanish', () => {
  const h = harness();
  assert.equal(h.state.lang, 'ko');
  assert.equal(h.t('attendance_rate'), '출석률');

  h.applyLanguage('en');
  assert.equal(h.state.lang, 'en');
  assert.equal(h.document.documentElement.lang, 'en');
  assert.equal(h.t('attendance_rate'), 'Attendance rate');
  assert.equal(h.el.btnToggleLang.textContent, 'EN');

  h.applyLanguage('es');
  assert.equal(h.state.lang, 'es');
  assert.equal(h.document.documentElement.lang, 'es');
  assert.equal(h.t('attendance_rate'), 'Tasa de asistencia');
  assert.equal(h.el.btnToggleLang.textContent, 'ES');

  h.toggleLanguage();
  assert.equal(h.state.lang, 'ko');
  assert.equal(h.document.documentElement.lang, 'ko');
  assert.equal(h.el.btnToggleLang.textContent, 'KO');
});

test('language switching: translates participant status badges in English and Spanish', async () => {
  const h = harness();
  const presentRow = { isExcluded: false, attendanceState: 'Present', confidence: 'Verified' };
  const unmatchedRow = { kind: 'unmatched', confidence: 'Unmatched' };

  // Korean (default)
  assert.equal(h.confidenceInfo(presentRow).label, '확인됨');

  // English
  h.applyLanguage('en');
  assert.equal(h.confidenceInfo(presentRow).label, 'Verified');
  assert.equal(h.confidenceInfo(unmatchedRow).label, 'Unmatched');

  // Spanish
  h.applyLanguage('es');
  assert.equal(h.confidenceInfo(presentRow).label, 'Verificado');
  assert.equal(h.confidenceInfo(unmatchedRow).label, 'No emparejado');
});

test('language switching translates rendered attendance controls, headline, status chips and duplicates in English and Spanish', async () => {
  const h = harness();
  const bindings = [...markup.matchAll(/<(\w+)\b([^>]*\bdata-i18n="[^"]+"[^>]*)>/g)].map(([, tag, attrs]) => {
    const id = attrs.match(/\bid="([^"]+)"/)?.[1];
    const node = id ? h.document.getElementById(id) : h.document.createElement(tag);
    node.setAttribute('data-i18n', attrs.match(/\bdata-i18n="([^"]+)"/)[1]);
    return node;
  });
  h.document.querySelectorAll = selector => selector === '[data-i18n]' ? bindings : [];
  const groupLabel = bindings.find(node => node.getAttribute('data-i18n') === 'group_filter_aria');
  const guide = bindings.find(node => node.getAttribute('data-i18n') === 'tutorial_s0_p');
  const emphasis = guide.appendChild(h.document.createElement('strong'));
  emphasis.textContent = '호스트 또는 공동호스트';
  const boardData = decisionBoard();
  boardData.recentEvents = [{
    eventType: 'Joined', participantName: 'Alice', source: 'manual-snapshot', occurredAt: '2026-09-05T01:00:00Z'
  }];
  boardData.duplicateConnectionGroups = [{
    rosterPersonId: 'a', rosterPersonName: 'Alice', connectionCount: 2,
    connections: [
      { presenceKey: 'alice-1', source: 'zoom-app', rawName: 'Alice', firstSeenAt: '2026-09-05T01:00:00Z' },
      { presenceKey: 'alice-2', source: 'zoom-app', rawName: 'Alice iPad', firstSeenAt: '2026-09-05T01:01:00Z' }
    ]
  }];
  h.el.tutorialSteps = [...markup.matchAll(/\bdata-tutorial-step="/g)].map(() => h.document.createElement('section'));
  assert.ok(h.el.tutorialSteps.length > 1);
  h.state.rosterLoaded = true;
  const healthCheck = h.checkHealth();
  await respond(h.requests.at(-1), { status: 'ok' });
  await healthCheck;
  const zoomCheck = h.checkZoomConnection(false);
  await respond(h.requests.at(-1), {
    configured: false,
    recommendedMode: 'zoomApp',
    zoomApp: { connected: true, sessionActive: true, role: 'host', meetingId: '111', lastSeenAt: '2026-09-05T01:00:00Z' }
  });
  await zoomCheck;
  h.applyUpdateStatus({ currentVersion: '0.6.3', availability: 'UpToDate', downloadState: 'None', supportedPlatform: true, enabled: true });
  h.renderBoard(boardData);

  assert.equal(h.el.desktopSummary.textContent, '2명 중 1명 출석');
  assert.equal(h.el.healthText.textContent, '연결됨');
  assert.equal(h.el.zoomStatus.textContent, '미설정');
  assert.equal(h.el.zoomAppStatus.textContent, '호스트 연결');

  for (const [lang, expected] of Object.entries({
    en: {
      sync: 'Sync now', readiness: "1. Upload today's roster", step: 'Upload roster',
      prompt: /upload the Excel roster/i,
      allGroups: 'All groups', groupLabel: 'Group filter', totals: /Roster present: 1 \/ 2/,
      desktopSummary: '1 of 2 present', health: 'Connected', zoomStatus: 'Not set', zoomApp: 'Host connected',
      dupReason: /Multiple connections matched to the same roster person/,
      activity: /AliceJoinedManual entry/, activityCount: 'Recent: 2', mode: 'Selected: Manual entry',
      next: 'Next', finish: 'Get started', manual: 'Enter participant list'
    },
    es: {
      sync: 'Sincronizar ahora', readiness: '1. Sube la lista de hoy', step: 'Subir lista',
      prompt: /sube la lista de Excel/i,
      allGroups: 'Todos los grupos', groupLabel: 'Filtro de grupo', totals: /Presentes: 1 de 2/,
      desktopSummary: '1 de 2 presentes', health: 'Conectado', zoomStatus: 'Sin configurar', zoomApp: 'Anfitrión conectado',
      dupReason: /Varias conexiones vinculadas a la misma persona del padrón/,
      activity: /AliceEntróEntrada manual/, activityCount: 'Recientes: 2', mode: 'Seleccionado: Entrada manual',
      next: 'Siguiente', finish: 'Empezar', manual: 'Introducir lista de participantes'
    }
  })) {
    h.applyConnectionMode('auto');
    h.state.tutorialStep = 0;
    h.applyLanguage(lang);
    assert.equal(h.el.desktopSummary.textContent, expected.desktopSummary);
    assert.equal(h.el.summaryTotal.textContent, '2');
    assert.equal(h.el.summaryPresent.textContent, '1');
    assert.equal(h.el.healthText.textContent, expected.health);
    assert.equal(h.el.zoomStatus.textContent, expected.zoomStatus);
    assert.equal(h.el.zoomAppStatus.textContent, expected.zoomApp);
    assert.match(h.el.duplicateList.textContent, expected.dupReason);
    assert.equal(h.el.btnSyncZoom.textContent, expected.sync);
    assert.equal(h.el.readinessTitle.textContent, expected.readiness);
    assert.match(h.el.readinessNote.textContent, expected.prompt);
    assert.equal(h.el.btnNextStep.textContent, expected.step);
    assert.equal(h.el.groupFilter.children[0].textContent, expected.allGroups);
    assert.equal(groupLabel.textContent, expected.groupLabel);
    assert.match(h.el.participantsMeta.textContent, expected.totals);
    assert.match(h.el.activityFeed.textContent, expected.activity);
    assert.equal(h.el.activityCount.textContent, expected.activityCount);
    assert.equal(h.el.btnTutorialNext.textContent, expected.next);
    assert.equal(guide.children.length, 0);

    // Every visible rendered element on the main dashboard & settings must be free of Hangul in en/es.
    const visibleIds = [
      'desktop-summary', 'phone-summary', 'health-text', 'zoom-status', 'zoom-app-status', 'auto-status',
      'summary-time', 'source-status', 'metric-present', 'metric-absent', 'metric-review', 'metric-duplicate',
      'participants-meta', 'scope-note', 'export-scope', 'btn-export', 'btn-next-step', 'visible-count',
      'activity-mode', 'activity-count', 'duplicate-list', 'roster-summary', 'version-text',
      'roster-note', 'mode-note', 'zoom-app-settings-status', 'relay-settings-note', 'pairing-code',
      'pairing-expiry', 'pairing-session', 'zoom-app-home-url', 'api-settings-status', 'snapshot-parsed',
      'update-current-version', 'update-status-text', 'update-badge'
    ];
    for (const id of visibleIds) {
      const text = h.document.getElementById(id).textContent;
      assert.doesNotMatch(text, /[가-힣]/, `${lang}: #${id} still contains Korean: "${text}"`);
    }

    h.applyConnectionMode('manual');
    h.state.tutorialStep = h.el.tutorialSteps.length - 1;
    h.applyLanguage(lang);
    assert.equal(h.el.btnSyncZoom.textContent, expected.manual);
    assert.ok(h.el.modeNote.textContent.startsWith(expected.mode));
    assert.equal(h.el.btnTutorialNext.textContent, expected.finish);
  }
  h.applyLanguage('ko');
  assert.equal(h.el.desktopSummary.textContent, '2명 중 1명 출석');
  assert.equal(guide.children[0], emphasis);
  assert.equal(h.el.btnSyncZoom.textContent, '참가자 목록 입력');
});

test('every locale key exists in ko, en and es and every Hangul element in index.html is wired for i18n', () => {
  const locales = require('../src/ZoomCheck.Backend/wwwroot/locales.js');
  const koKeys = Object.keys(locales.ko).sort();
  assert.deepEqual(Object.keys(locales.en).sort(), koKeys, 'en locale keys must match ko');
  assert.deepEqual(Object.keys(locales.es).sort(), koKeys, 'es locale keys must match ko');

  const placeholders = text => [...String(text).matchAll(/\{(\w+)(?:\|으로)?\}/g)].map(m => m[1]).sort();
  for (const key of koKeys) {
    for (const lang of ['ko', 'en', 'es']) {
      assert.equal(typeof locales[lang][key], 'string', `${lang}.${key} must be a string`);
      assert.ok(locales[lang][key].length > 0, `${lang}.${key} must be non-empty`);
    }
    assert.deepEqual(placeholders(locales.en[key]), placeholders(locales.ko[key]), `en.${key} placeholders`);
    assert.deepEqual(placeholders(locales.es[key]), placeholders(locales.ko[key]), `es.${key} placeholders`);
  }

  // Every data-i18n / placeholder / aria key in index.html must exist in all three locales.
  for (const [, key] of markup.matchAll(/\bdata-i18n(?:-(?:placeholder|aria))?="([^"]+)"/g)) {
    assert.ok(Object.prototype.hasOwnProperty.call(locales.ko, key), `index.html key ${key} missing from ko`);
    assert.ok(Object.prototype.hasOwnProperty.call(locales.en, key), `index.html key ${key} missing from en`);
    assert.ok(Object.prototype.hasOwnProperty.call(locales.es, key), `index.html key ${key} missing from es`);
  }

  // Every element in index.html containing Hangul text outside <script> and comments must either
  // carry data-i18n or be explicitly driven by applyLanguage() via its id (or <title>).
  const dynamicRenderedIds = new Set([
    'health-text', 'zoom-status', 'zoom-app-status', 'auto-status', 'phone-summary', 'desktop-summary',
    'btn-next-step', 'visible-count', 'activity-mode', 'activity-count', 'roster-summary', 'version-text',
    'roster-note', 'snapshot-parsed', 'mode-note', 'zoom-app-settings-status', 'relay-settings-note',
    'pairing-expiry', 'pairing-session', 'zoom-app-home-url', 'api-settings-status', 'update-current-version',
    'metric-present', 'metric-absent', 'metric-review', 'metric-duplicate'
  ]);
  const stripped = markup
    .replace(/<script[\s\S]*?<\/script>/gi, '')
    .replace(/<!--[\s\S]*?-->/g, '');
  const voidTags = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr']);
  const stack = [];
  for (const token of stripped.matchAll(/<\/?([a-zA-Z0-9-]+)([^>]*)>|([^<]+)/g)) {
    const [raw, tagName, attrs, text] = token;
    if (tagName) {
      const lower = tagName.toLowerCase();
      if (raw.startsWith('</')) {
        while (stack.length && stack[stack.length - 1].tag !== lower) { stack.pop(); }
        stack.pop();
      } else if (!voidTags.has(lower) && !raw.endsWith('/>')) {
        const id = attrs.match(/\bid="([^"]+)"/)?.[1] || '';
        const hasI18n = /\bdata-i18n=/.test(attrs) || lower === 'title';
        stack.push({ tag: lower, id, translated: hasI18n || (id && dynamicRenderedIds.has(id)) });
      }
    } else if (text && /[가-힣]/.test(text)) {
      const covered = stack.some(frame => frame.translated);
      const top = stack[stack.length - 1];
      assert.ok(covered,
        `index.html <${top?.tag}${top?.id ? ` id="${top.id}"` : ''}> has untranslated Korean text "${text.trim()}" without data-i18n or a dynamic renderer`);
    }
  }
});

test('language preference is restored in a fresh dashboard session', () => {
  const storage = new Map();
  const first = harness({ storage });
  first.restoreState();
  assert.equal(first.document.documentElement.lang, 'ko');
  first.applyLanguage('es');
  assert.equal(storage.get('zoomcheck.lang'), 'es');

  const next = harness({ storage });
  next.restoreState();
  assert.equal(next.document.documentElement.lang, 'es');
  assert.equal(next.el.btnToggleLang.textContent, 'ES');
  assert.equal(next.el.btnSyncZoom.textContent, 'Sincronizar ahora');
});

function rosterBoard() {
  return {
    ...board('111'), lastReceivedAt: '2026-09-05T01:30:00Z',
    people: [
      { rosterPersonId: 'p10', sequence: '10', name: '학생010', group: 'A', attendanceState: 'NotJoined', confidence: 'Unmatched' },
      { rosterPersonId: 'p2', sequence: '2', name: '학생002', group: 'A', attendanceState: 'Left', confidence: 'Verified' },
      { rosterPersonId: 'px', sequence: '', name: '학생099', group: 'A', attendanceState: 'NotJoined', confidence: 'Unmatched' },
      { rosterPersonId: 'p1', sequence: '1', name: '학생001', group: 'A', attendanceState: 'Present', confidence: 'Verified' },
      { rosterPersonId: 'p3', sequence: '3', name: '학생003', group: 'B', attendanceState: 'NotJoined', confidence: 'Unmatched' },
      { rosterPersonId: 'p4', sequence: '4', name: '학생004', group: 'A', attendanceState: 'NotJoined', confidence: 'Verified', isExcluded: true }
    ],
    currentConnections: [
      { presenceKey: 'p1-a', source: 'zoom-app', displayName: '학생001', matchedRosterPersonId: 'p1' },
      { presenceKey: 'p1-b', source: 'zoom-app', displayName: '학생001 폰', matchedRosterPersonId: 'p1' }
    ],
    unmatchedParticipants: [{ participantName: '손님', attendanceState: 'Present' }],
    duplicateConnectionGroups: [{ rosterPersonId: 'p1', rosterPersonName: '학생001', connectionCount: 2,
      connections: [{ presenceKey: 'p1-a', displayName: '학생001' }, { presenceKey: 'p1-b', displayName: '학생001 폰' }] }]
  };
}
const rowKeys = h => h.el.participantRows.children.filter(row => row.className.includes('participant-row')).map(row => row.dataset.key);

test('participants sort by roster number in numeric order with numberless rows last, and the choice persists', () => {
  const storage = new Map([['zoomcheck.tutorialSeen.v1', 'seen']]);
  const h = harness({ storage }); h.restoreState(); select(h, '111'); h.renderBoard(rosterBoard());
  assert.deepEqual(rowKeys(h), ['roster:p1', 'roster:p2', 'roster:p3', 'roster:p10', 'roster:px']);
  assert.equal(h.el.participantRows.children[0].children[0].textContent, '1');
  assert.equal(h.sortButtons[0].parentNode.getAttribute('aria-sort'), 'ascending');
  h.state.filter = 'review'; h.renderParticipants();
  assert.deepEqual(rowKeys(h), ['roster:p1', 'unmatched:손님']);
  h.sortButtons[1].dispatch('click');
  assert.deepEqual(rowKeys(h), ['unmatched:손님', 'roster:p1']);
  h.state.filter = 'all'; h.renderParticipants();
  assert.deepEqual(rowKeys(h), ['roster:p1', 'roster:p2', 'roster:p3', 'roster:p10', 'roster:px']);
  assert.equal(h.sortButtons[1].parentNode.getAttribute('aria-sort'), 'ascending');
  assert.equal(h.sortButtons[0].parentNode.getAttribute('aria-sort'), null);
  assert.equal(storage.get('zoomcheck.participantSort'), 'name');

  const next = harness({ storage }); next.restoreState(); select(next, '111'); next.renderBoard(rosterBoard());
  assert.equal(next.state.sort, 'name');
  assert.equal(next.sortButtons[1].parentNode.getAttribute('aria-sort'), 'ascending');
});

test('clean NameOnly uses neutral pill and review reasons translate in ko, en and es', () => {
  const h = harness();
  const payload = decisionBoard();
  payload.people[0].identityReviewStatus = 'none';
  payload.people[0].duplicateReviewStatus = 'none';
  payload.people[0].reviewRequired = false;
  h.renderBoard(payload);
  assert.equal(h.confidenceInfo(h.state.rows[0]).className, 'neutral');
  assert.equal(h.el.countReview.textContent, '1');

  const reasons = [
    ['duplicate_roster_name', /같은 이름/, /share this name/, /mismo nombre/],
    ['email_conflict', /이메일이 명단과 다릅니다/, /email differs from the roster/, /correo de Zoom no coincide/],
    ['identifier_conflict', /서로 다른 사람/, /different people on the roster/, /distintas personas/],
    ['duplicate_roster_email', /같은 이메일/, /share this email/, /comparten este correo/],
    ['ambiguous_possible_match', /비슷한 이름이 명단에 여러 명/, /similar to several people/, /parece a varias personas/]
  ];
  for (const [code, koRe, enRe, esRe] of reasons) {
    payload.people[0].identityReviewStatus = 'pending';
    payload.people[0].reviewRequired = true;
    payload.people[0].identityReviewReason = code;
    h.applyLanguage('ko'); h.renderBoard(payload); h.state.selectedKey = 'roster:a'; h.renderParticipants();
    assert.equal(h.confidenceInfo(h.state.rows[0]).className, 'review');
    assert.match(h.el.participantRows.textContent, koRe);
    h.applyLanguage('en');
    assert.match(h.el.participantRows.textContent, enRe);
    h.applyLanguage('es');
    assert.match(h.el.participantRows.textContent, esRe);
  }
});

test('status text follows the group scope: count, absentees by number, duplicate connections', () => {
  const h = harness(); h.renderBoard(rosterBoard());
  assert.equal(h.buildStatusText(), [
    '회의 현황 · 회의 ID 111', h.buildStatusText().split('\n')[1], '',
    '전체 5명 중 1명 접속', '오늘 제외 1명 (집계 미포함)', '',
    '미참석 4명', '2번 학생002 (퇴장)', '3번 학생003', '10번 학생010', '학생099', '',
    '중복 접속 1명', '1번 학생001 · 2회 접속'
  ].join('\n'));
  assert.match(h.buildStatusText().split('\n')[1], /^2026-09-05 \d\d:30 기준$/);
  h.applyGroup('B');
  const groupB = h.buildStatusText().split('\n');
  assert.match(groupB[1], / · B 그룹$/);
  assert.deepEqual(groupB.slice(3), ['전체 1명 중 0명 접속', '', '미참석 1명', '3번 학생003', '', '중복 접속 없음']);
  h.applyGroup('A'); h.applyLanguage('en');
  assert.deepEqual(h.buildStatusText().split('\n').slice(3), [
    '1 of 4 connected', 'Excluded today: 1 (not counted)', '',
    'Not connected: 3', '#2 학생002 (left)', '#10 학생010', '학생099', '',
    'Connected more than once: 1', '#1 학생001 · 2 connections'
  ]);
  assert.doesNotMatch(h.buildStatusText(), /[|*#]{2}|[─│┌]/);
});

test('copy status writes to the clipboard, falls back to a selected text box, and saves the same text as .txt', async () => {
  const written = [];
  const h = harness({ clipboard: { writeText: text => { written.push(text); return Promise.resolve(); } } });
  h.renderBoard(rosterBoard()); h.applyGroup('A');
  assert.equal(h.el.btnCopyStatus.disabled, false);
  h.el.btnCopyStatus.dispatch('click'); await drain();
  assert.equal(written[0], h.buildStatusText());
  assert.equal(h.el.statusTextDialog.open, true);
  assert.equal(h.el.statusTextTitle.textContent, '현황을 복사했습니다');
  assert.equal(h.el.statusText.value, written[0]);
  h.el.btnStatusDownload.dispatch('click');
  const file = h.saved[0];
  assert.equal(file.type, 'text/plain;charset=utf-8');
  assert.equal(file.parts[0], '﻿' + written[0].replace(/\n/g, '\r\n'));
  assert.match(h.el.toastRegion.textContent, /111-A-status-\d{8}-\d{4}\.txt/);
  assert.equal(h.document.body.children.length, 0);
  h.el.btnStatusClose.dispatch('click');
  assert.equal(h.el.statusTextDialog.open, false);
  assert.equal(h.document.activeElement, h.el.btnCopyStatus);

  const refused = harness({ clipboard: { writeText: () => Promise.reject(new Error('NotAllowedError')) } });
  refused.renderBoard(rosterBoard()); await refused.copyMeetingStatus();
  assert.equal(refused.el.statusTextTitle.textContent, '직접 복사하세요');
  assert.equal(refused.document.activeElement, refused.el.statusText);
  assert.equal(refused.el.statusText.selectionEnd, refused.el.statusText.value.length);
  const missing = harness(); missing.renderBoard(rosterBoard()); await missing.copyMeetingStatus();
  assert.equal(missing.el.statusTextTitle.textContent, '직접 복사하세요');

  const empty = harness(); empty.renderBoard({ ...board('111'), people: [] });
  assert.equal(empty.el.btnCopyStatus.disabled, true);
});

test('connection status polls on one 5s schedule, skips hidden/overlapping ticks, refreshes active Zoom App sessions, and resumes on visibility', async () => {
  const h = harness({ storage: new Map([['zoomcheck.tutorialSeen.v1', 'seen'], ['zoomcheck.meetingId', '111']]) });
  h.init();

  const statusTimers = [...h.intervals.values()].filter(timer => timer.delay === 5000 || timer.delay === 10000);
  assert.equal(statusTimers.length, 1, 'only one connection-status interval is registered');
  assert.equal(statusTimers[0].delay, 5000);
  assert.equal(h.state.statusPollId, undefined);

  // Drain initial load requests.
  const initialCount = h.requests.length;
  const poll = statusTimers[0];

  // Overlapping tick while a status request is still in flight does not stack a second request.
  poll.fn();
  poll.fn();
  const pollRequests = () => h.requests.slice(initialCount).filter(r => r.url === '/api/zoom/connection-status');
  const boardRequests = () => h.requests.slice(initialCount).filter(r => r.url === '/api/meetings/111/board');
  assert.equal(pollRequests().length, 1);

  // Active Zoom App session triggers a silent board refresh after status resolves.
  await respond(pollRequests()[0], { zoomApp: { sessionActive: true, connected: true, meetingId: '111' } });
  assert.equal(boardRequests().length, 1);
  await respond(boardRequests()[0], board('111'));

  // Hidden tab pauses connection-status polling completely.
  h.document.hidden = true;
  poll.fn();
  poll.fn();
  assert.equal(pollRequests().length, 1);

  // Returning to visible triggers an immediate check and restores 5s cadence (12 polls/min).
  h.document.hidden = false;
  h.document.dispatch('visibilitychange');
  assert.equal(pollRequests().length, 2);
  await respond(pollRequests()[1], { zoomApp: { sessionActive: false, connected: false } });
  assert.equal(boardRequests().length, 1, 'inactive Zoom App session does not trigger board refresh');

  for (let tick = 0; tick < 11; tick++) {
    poll.fn();
    await respond(pollRequests().at(-1), { zoomApp: { sessionActive: false, connected: false } });
  }
  assert.equal(pollRequests().length, 13, '12 polls across 60s plus visibility resume');
});

test('phone search matches phone numbers typed with or without hyphens', () => {
  const h = harness();
  h.state.roster = [
    { id: 'p1', name: '학생001', phone: '01000000123' },
    { id: 'p2', name: '학생002', phone: '010-1234-5678' },
    { id: 'p3', name: '학생003', phone: '' }
  ];
  h.renderBoard({
    ...board('111'),
    people: [
      { rosterPersonId: 'p1', sequence: '1', name: '학생001', attendanceState: 'Present', confidence: 'Verified' },
      { rosterPersonId: 'p2', sequence: '2', name: '학생002', attendanceState: 'NotJoined', confidence: 'Unmatched' },
      { rosterPersonId: 'p3', sequence: '3', name: '학생003', attendanceState: 'NotJoined', confidence: 'Unmatched' }
    ]
  });

  h.el.participantSearch.value = '010-0000-0123';
  h.el.participantSearch.dispatch('input');
  assert.deepEqual(rowKeys(h), ['roster:p1']);

  h.el.participantSearch.value = '01000000123';
  h.el.participantSearch.dispatch('input');
  assert.deepEqual(rowKeys(h), ['roster:p1']);

  h.el.participantSearch.value = '0000-0123';
  h.el.participantSearch.dispatch('input');
  assert.deepEqual(rowKeys(h), ['roster:p1']);

  h.el.participantSearch.value = '010 1234 5678';
  h.el.participantSearch.dispatch('input');
  assert.deepEqual(rowKeys(h), ['roster:p2']);

  h.el.participantSearch.value = '01012345678';
  h.el.participantSearch.dispatch('input');
  assert.deepEqual(rowKeys(h), ['roster:p2']);

  h.el.participantSearch.value = '010-9999-9999';
  h.el.participantSearch.dispatch('input');
  assert.deepEqual(rowKeys(h), []);

  // Mixed text and digits should not falsely match another person's phone digits.
  h.el.participantSearch.value = '학생0123';
  h.el.participantSearch.dispatch('input');
  assert.deepEqual(rowKeys(h), []);
});

test('phone summary line shows present/total and absent counts across scopes and languages', () => {
  const h = harness();
  h.applyLanguage('ko');
  assert.equal(h.el.phoneSummary.textContent, '출석 0/0 · 미참석 0');

  h.renderBoard(rosterBoard());
  assert.equal(h.el.phoneSummary.textContent, '출석 1/5 · 미참석 4');

  h.applyGroup('A');
  assert.equal(h.el.phoneSummary.textContent, '출석 1/4 · 미참석 3');

  h.applyLanguage('en');
  assert.equal(h.el.phoneSummary.textContent, 'Present 1/4 · Absent 3');

  h.applyLanguage('es');
  assert.equal(h.el.phoneSummary.textContent, 'Presentes 1/4 · Ausentes 3');
});

test('first-run guidance order and empty states match roster -> meeting ID -> connection order', async () => {
  const h = harness();
  assert.match(h.t('tutorial_s0_title'), /명단/);
  assert.match(h.t('tutorial_s1_title'), /회의 ID/);
  assert.match(h.t('summary_empty_prompt'), /명단.*회의 ID/);
  assert.match(markup, /id="btn-export"[^>]*disabled/);
  assert.match(markup, /id="btn-review"[^>]*disabled/);

  h.applyLanguage('ko');
  assert.equal(h.el.btnExport.disabled, true);
  assert.equal(h.el.btnReview.disabled, true);

  // Empty roster guides uploading Excel roster in step 1.
  h.state.rosterLoaded = true;
  h.state.roster = [];
  h.renderParticipants();
  assert.equal(h.el.emptyTitle.textContent, '아직 등록된 명단이 없습니다.');
  assert.match(h.el.emptyNote.textContent, /1단계.*Excel 명단/);

  // Loaded roster without a meeting ID guides entering the meeting ID.
  h.state.roster = [{ id: 'a', name: '학생001' }, { id: 'b', name: '학생002' }];
  select(h, '');
  h.renderParticipants();
  assert.equal(h.el.emptyTitle.textContent, '회의 ID를 입력하면 명단이 표시됩니다.');
  assert.match(h.el.emptyNote.textContent, /2명/);

  // Unconfigured Zoom API uses a neutral dot unless Business API mode is chosen.
  const conn = h.checkZoomConnection(false);
  await respond(h.requests.at(-1), { configured: false, recommendedMode: 'manual' });
  await conn;
  assert.equal(h.el.zoomDot.classList.contains('is-bad'), false);
  assert.equal(h.el.zoomDot.classList.contains('warn'), false);
  h.applyConnectionMode('business');
  assert.equal(h.el.zoomDot.classList.contains('warn'), true);
});

test('screen-reader, keyboard and Korean line-break guards stay intact', () => {
  assert.doesNotMatch(markup, /class="sync-clock"[^>]*aria-live/);
  assert.match(markup, /href="#activity-panel"[^>]*data-i18n="skip_to_activity"/);
  assert.match(markup, /id="tutorial-tab-0"[^>]*aria-controls="tutorial-panel-0"/);
  assert.match(markup, /id="tutorial-panel-0"[^>]*role="tabpanel"[^>]*aria-labelledby="tutorial-tab-0"/);
  assert.match(stylesSource, /:lang\(ko\)\s*\{[^}]*word-break:\s*keep-all/);

  const h = harness();
  h.applyConnectionMode('business');
  h.document.activeElement = { tagName: 'SELECT' };
  h.document.dispatch('keydown', { key: 'r' });
  assert.equal(h.requests.length, 0, 'r inside a select must not trigger sync');

  h.document.activeElement = { tagName: 'BODY' };
  h.el.tutorialDialog.open = true;
  h.document.dispatch('keydown', { key: 'r' });
  assert.equal(h.requests.length, 0, 'r while tutorial is open must not trigger sync');
});

test('Korean 로/으로 particles choose the grammatically right suffix', () => {
  const h = harness();
  assert.equal(h.t('mode_sync_with', { mode: 'Business API' }), 'Business API로 동기화합니다.');
  assert.equal(h.t('mode_sync_with', { mode: 'Zoom 앱 (Pro)' }), 'Zoom 앱 (Pro)로 동기화합니다.');
  assert.equal(h.t('mode_sync_with', { mode: '자동' }), '자동으로 동기화합니다.');
  assert.equal(h.t('mode_sync_with', { mode: '직접 입력' }), '직접 입력으로 동기화합니다.');
  assert.equal(h.t('connection_rename', { name: '김민수' }), 'Zoom 이름을 김민수로 변경');
  assert.equal(h.t('connection_rename', { name: '이서연' }), 'Zoom 이름을 이서연으로 변경');
  assert.equal(h.t('connection_rename', { name: '한길' }), 'Zoom 이름을 한길로 변경');
  assert.equal(h.t('connection_rename', { name: '학생001' }), 'Zoom 이름을 학생001로 변경');
  assert.equal(h.t('connection_rename', { name: '학생003' }), 'Zoom 이름을 학생003으로 변경');

  h.applyConnectionMode('business', { notify: true });
  assert.match(h.el.toastRegion.textContent, /Business API로 동기화합니다\./);
});

test('roster upload validation errors render inline in the roster panel without hidden toasts', async () => {
  const h = harness();
  await h.uploadRoster();
  assert.equal(h.requests.length, 0);
  assert.equal(h.el.rosterFeedback.hidden, false);
  assert.equal(h.el.rosterFeedback.classList.contains('is-bad'), true);
  assert.equal(h.el.rosterFeedback.getAttribute('role'), 'alert');
  assert.match(h.el.rosterFeedbackTitle.textContent, /명단 파일이 필요합니다/);
  assert.equal(h.el.toastRegion.children.length, 0);

  h.el.rosterFile.files = [{ name: 'roster.csv', size: 120 }];
  await h.uploadRoster();
  assert.equal(h.requests.length, 0);
  assert.match(h.el.rosterFeedbackTitle.textContent, /지원하지 않는 파일 형식/);
  assert.match(h.el.rosterFeedbackHint.textContent, /\.xlsx/);
  assert.equal(h.el.toastRegion.children.length, 0);

  h.el.rosterFile.files = [{ name: 'huge.xlsx', size: 25 * 1024 * 1024 }];
  await h.uploadRoster();
  assert.equal(h.requests.length, 0);
  assert.match(h.el.rosterFeedbackTitle.textContent, /너무 큽니다/);
  assert.equal(h.el.toastRegion.children.length, 0);

  h.el.rosterFile.files = [{ name: 'fixed.xlsx', size: 128 }];
  h.el.rosterFile.dispatch('change');
  assert.equal(h.el.rosterFeedback.hidden, true);
});

test('roster upload server errors display inline in Korean and follow UI language switching', async () => {
  const h = harness();
  h.el.rosterFile.files = [{ name: 'bad-headers.xlsx', size: 128 }];
  const missingHeaders = h.uploadRoster();
  await respond(h.requests.at(-1), {
    status: 400,
    errorCode: 'ROSTER_MISSING_HEADERS',
    messageKey: 'roster_err_missing_headers',
    foundHeaders: ['순번코드', '참가자명'],
    missingColumns: ['name', 'sequence']
  }, 400);
  await missingHeaders;
  assert.equal(h.el.rosterFeedback.hidden, false);
  assert.equal(h.el.rosterFeedback.classList.contains('is-bad'), true);
  assert.match(h.el.rosterFeedbackTitle.textContent, /필수 열 머리글/);
  assert.match(h.el.rosterFeedbackList.textContent, /찾은 머리글: 순번코드, 참가자명/);
  assert.match(h.el.rosterFeedbackHint.textContent, /필요한 머리글/);
  assert.equal(h.el.toastRegion.children.length, 0);

  h.applyLanguage('en');
  assert.match(h.el.rosterFeedbackTitle.textContent, /Required column headers not found/);
  assert.match(h.el.rosterFeedbackList.textContent, /Headers found: 순번코드, 참가자명/);

  h.applyLanguage('es');
  assert.match(h.el.rosterFeedbackTitle.textContent, /No se encontraron los encabezados obligatorios/);
  assert.match(h.el.rosterFeedbackList.textContent, /Encabezados encontrados: 순번코드, 참가자명/);

  h.applyLanguage('ko');
  for (const [errorCode, messageKey, expected] of [
    ['ROSTER_NOT_EXCEL', 'roster_err_not_excel', /Excel 파일이 아닙니다/],
    ['ROSTER_EMPTY_SHEET', 'roster_err_empty_sheet', /시트가 비어 있습니다/],
    ['ROSTER_NO_ROWS', 'roster_err_no_rows', /등록할 사람이 없습니다/]
  ]) {
    const attempt = h.uploadRoster();
    await respond(h.requests.at(-1), { status: 400, errorCode, messageKey }, 400);
    await attempt;
    assert.match(h.el.rosterFeedbackTitle.textContent, expected);
    assert.equal(h.el.toastRegion.children.length, 0);
  }
});

test('roster upload reports duplicate numbers inline with row numbers and translates on language switch', async () => {
  const h = harness();
  h.el.rosterFile.files = [{ name: 'roster-duplicates.xlsx', size: 512 }];
  const upload = h.uploadRoster();
  await respond(h.requests.at(-1), {
    displayName: 'roster-duplicates.xlsx',
    count: 32,
    warnings: {
      duplicateNumbers: [
        { sequence: '5', count: 2, names: ['학생005'], rowNumbers: [6, 32] }
      ]
    }
  }, 200);
  const followUps = h.requests.slice(1);
  await respond(followUps.find(r => r.url === '/api/roster'), []);
  await respond(followUps.find(r => r.url.includes('/board')), board('111'));
  await upload;

  assert.equal(h.el.rosterFeedback.hidden, false);
  assert.equal(h.el.rosterFeedback.classList.contains('is-warn'), true);
  assert.equal(h.el.rosterFeedback.getAttribute('role'), 'status');
  assert.match(h.el.rosterFeedbackTitle.textContent, /32명 등록됨 · 같은 번호 1개/);
  assert.match(h.el.rosterFeedbackList.textContent, /같은 번호 2건 \(5번 학생005\) · 6, 32행/);
  assert.equal(h.el.toastRegion.children.length, 0);

  h.applyLanguage('en');
  assert.match(h.el.rosterFeedbackList.textContent, /2 rows with #5 \(학생005\) · rows 6, 32/);

  h.applyLanguage('es');
  assert.match(h.el.rosterFeedbackList.textContent, /2 filas con N\.º 5 \(학생005\) · filas 6, 32/);
});

test('roster upload clean success renders inline confirmation without warnings', async () => {
  const h = harness();
  h.el.rosterFile.files = [{ name: 'roster-valid.xlsx', size: 512 }];
  const upload = h.uploadRoster();
  await respond(h.requests.at(-1), {
    displayName: 'roster-valid.xlsx',
    count: 30,
    warnings: { duplicateNumbers: [] }
  }, 200);
  const followUps = h.requests.slice(1);
  await respond(followUps.find(r => r.url === '/api/roster'), []);
  await respond(followUps.find(r => r.url.includes('/board')), board('111'));
  await upload;

  assert.equal(h.el.rosterFeedback.hidden, false);
  assert.equal(h.el.rosterFeedback.classList.contains('is-ok'), true);
  assert.match(h.el.rosterFeedbackTitle.textContent, /명단을 불러왔습니다/);
  assert.match(h.el.rosterFeedbackMessage.textContent, /roster-valid\.xlsx에서 30명을 등록했습니다/);
  assert.equal(h.el.rosterFeedbackList.hidden, true);
});
