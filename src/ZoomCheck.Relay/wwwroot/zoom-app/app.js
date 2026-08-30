(function () {
  'use strict';

  // ZoomCheck 릴레이 컴패니언 클라이언트.
  // 릴레이 서버는 암호문만 중계하며 복호화 키를 가지지 않는다.
  // 페어링 코드, 세션 토큰, 파생 키, 참가자 이름은 절대 저장소에 남기지 않는다.

  var sdk = window.zoomSdk;
  var subtle = window.crypto && window.crypto.subtle;

  var CAPABILITIES = ['getSupportedJsApis', 'getMeetingContext', 'getMeetingUUID', 'getUserContext', 'getMeetingParticipants', 'onParticipantChange'];
  var REQUIRED = ['getMeetingParticipants', 'getMeetingContext', 'getUserContext'];

  var PROTOCOL = 'zoomcheck-relay';
  var VERSION = 'v1';
  var ENVELOPE_VERSION = 1;
  var DIRECTION_OUT = 'companion-to-desktop';
  var DIRECTION_IN = 'desktop-to-companion';
  var SNAPSHOT_INTERVAL_MS = 10000;
  var HEARTBEAT_INTERVAL_MS = 2000;
  var CHANGE_DEBOUNCE_MS = 400;

  var MSG_SNAPSHOT = 'participant-snapshot';
  var MSG_HEARTBEAT = 'heartbeat';
  var MSG_DISCONNECT = 'companion-disconnect';
  var MSG_SYNC_REQUEST = 'sync-request';

  var zoom = { meetingId: null, meetingUuid: null, role: null, userId: null, screenName: null, supported: [] };
  var session = null; // { id, token, sendKey, receiveKey, sequence, lastReceived }
  var timers = { heartbeat: null, snapshot: null, poll: null, debounce: null };
  var flags = { sdkReady: false, sending: false, polling: false, connecting: false };
  var el = {};

  function $(id) { return document.getElementById(id); }
  function setText(node, value) { if (node) { node.textContent = value; } }
  function nowLabel() { return new Date().toLocaleTimeString('ko-KR', { hour12: false }); }

  // ---------------------------------------------------------------- 인코딩

  function utf8(text) { return new TextEncoder().encode(text); }

  function toBase64(bytes) {
    var view = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
    var binary = '';
    for (var index = 0; index < view.length; index += 1) { binary += String.fromCharCode(view[index]); }
    return window.btoa(binary);
  }

  function fromBase64(value) {
    var binary = window.atob(String(value || '').replace(/\s+/g, ''));
    var bytes = new Uint8Array(binary.length);
    for (var index = 0; index < binary.length; index += 1) { bytes[index] = binary.charCodeAt(index); }
    return bytes;
  }

  function pick(source, names) {
    for (var index = 0; index < names.length; index += 1) {
      var value = source ? source[names[index]] : null;
      if (value !== undefined && value !== null && value !== '') { return value; }
    }
    return null;
  }

  // ---------------------------------------------------------------- 오류 처리

  // 릴레이/암호 오류 원문에는 코드나 토큰이 섞일 수 있으므로 사용자에게는 축약 메시지만 보인다.
  function RedactedError(message, kind) {
    this.name = 'RedactedError';
    this.message = message;
    this.kind = kind || 'relay';
  }
  RedactedError.prototype = Object.create(Error.prototype);

  function redact(error, fallback) {
    if (error instanceof RedactedError) { return error.message; }
    return fallback;
  }

  function sdkDiagnostic(error) {
    var code = String((error && (error.code || error.errorCode)) || '').replace(/[^a-zA-Z0-9_-]/g, '').slice(0, 24);
    var message = String((error && error.message) || '알 수 없는 오류')
      .replace(/https?:\/\/\S+/gi, '[URL]')
      .replace(/[a-zA-Z0-9_-]{20,}/g, '[값]')
      .replace(/\s+/g, ' ')
      .trim()
      .slice(0, 160);
    return (code ? code + ' · ' : '') + message;
  }

  function sdkStageLabel(stage) {
    return {
      config: 'SDK 연결',
      supported: '허용 API 확인',
      meeting: '회의 컨텍스트',
      user: '사용자 컨텍스트',
      validation: '권한 검증'
    }[stage] || '초기화';
  }

  // ---------------------------------------------------------------- 키 교환

  function deriveDirectionKey(secretBits, saltBytes, sessionId, direction) {
    return subtle.importKey('raw', secretBits, 'HKDF', false, ['deriveKey']).then(function (material) {
      return subtle.deriveKey(
        {
          name: 'HKDF',
          hash: 'SHA-256',
          salt: saltBytes,
          info: utf8(PROTOCOL + '/' + VERSION + '/' + sessionId + '/' + direction)
        },
        material,
        { name: 'AES-GCM', length: 256 },
        false,
        ['encrypt', 'decrypt']
      );
    });
  }

  function redeem(pairingCode) {
    if (!subtle) { return Promise.reject(new RedactedError('이 브라우저에서는 종단간 암호화를 사용할 수 없습니다.', 'crypto')); }

    var keyPair = null;
    return subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits'])
      .then(function (generated) {
        keyPair = generated;
        return subtle.exportKey('spki', generated.publicKey);
      })
      .then(function (spki) {
        return fetch(window.location.origin + '/api/v1/pairings/redeem', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
          body: JSON.stringify({ pairingCode: pairingCode, companionPublicKey: toBase64(spki) }),
          cache: 'no-store',
          credentials: 'omit'
        });
      })
      .then(function (response) {
        if (!response.ok) {
          throw new RedactedError(response.status === 404 || response.status === 400 || response.status === 410
            ? '페어링 코드가 올바르지 않거나 만료되었습니다.'
            : '릴레이 연결에 실패했습니다.', 'redeem');
        }
        return response.json();
      })
      .then(function (payload) {
        // 릴레이 계약(RedeemPairingResponse): sessionId, companionToken, desktopPublicKey, salt.
        var sessionId = pick(payload, ['sessionId']);
        var token = pick(payload, ['companionToken']);
        var desktopKey = pick(payload, ['desktopPublicKey']);
        var salt = pick(payload, ['salt']);
        if (!sessionId || !token || !desktopKey || !salt) {
          throw new RedactedError('릴레이 응답이 올바르지 않습니다.', 'redeem');
        }

        var saltBytes = fromBase64(salt);
        return subtle.importKey('spki', fromBase64(desktopKey), { name: 'ECDH', namedCurve: 'P-256' }, false, [])
          .then(function (desktopPublicKey) {
            return subtle.deriveBits({ name: 'ECDH', public: desktopPublicKey }, keyPair.privateKey, 256);
          })
          .then(function (secretBits) {
            return Promise.all([
              deriveDirectionKey(secretBits, saltBytes, sessionId, DIRECTION_OUT),
              deriveDirectionKey(secretBits, saltBytes, sessionId, DIRECTION_IN)
            ]);
          })
          .then(function (keys) {
            return { id: sessionId, token: token, sendKey: keys[0], receiveKey: keys[1], sequence: 0, lastReceived: 0 };
          })
          .catch(function (error) {
            throw new RedactedError(redact(error, '키 교환에 실패했습니다.'), 'crypto');
          });
      });
  }

  // ---------------------------------------------------------------- 메시지 봉투

  function aad(sessionId, direction, sequence, messageType) {
    return utf8([PROTOCOL, VERSION, sessionId, direction, String(sequence), messageType].join('|'));
  }

  function relayFetch(path, options) {
    if (!session) { return Promise.reject(new RedactedError('연결이 종료되었습니다.', 'session')); }
    var request = {
      method: options.method,
      headers: Object.assign({ Accept: 'application/json', Authorization: 'Bearer ' + session.token }, options.headers || {}),
      cache: 'no-store',
      credentials: 'omit'
    };
    if (options.body) { request.body = options.body; }
    return fetch(window.location.origin + path, request).then(function (response) {
      if (response.status === 401 || response.status === 403 || response.status === 404 || response.status === 410) {
        throw new RedactedError('릴레이 세션이 만료되었습니다.', 'session');
      }
      if (!response.ok) { throw new RedactedError('릴레이 통신에 실패했습니다.', 'relay'); }
      return response.status === 204 ? {} : response.json().catch(function () { return {}; });
    }, function () {
      throw new RedactedError('릴레이에 연결할 수 없습니다.', 'network');
    });
  }

  function sendMessage(messageType, plaintext) {
    if (!session) { return Promise.reject(new RedactedError('연결이 종료되었습니다.', 'session')); }
    var current = session;
    var sequence = current.sequence + 1; // 단조 증가, 1부터 시작
    var nonce = window.crypto.getRandomValues(new Uint8Array(12));

    return subtle.encrypt(
      { name: 'AES-GCM', iv: nonce, additionalData: aad(current.id, DIRECTION_OUT, sequence, messageType), tagLength: 128 },
      current.sendKey,
      utf8(JSON.stringify(plaintext))
    ).then(function (ciphertext) {
      current.sequence = sequence;
      return relayFetch('/api/v1/sessions/' + encodeURIComponent(current.id) + '/companion/messages', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          version: ENVELOPE_VERSION,
          sequence: sequence,
          messageType: messageType,
          nonce: toBase64(nonce),
          ciphertext: toBase64(ciphertext)
        })
      });
    }, function (error) {
      throw new RedactedError(redact(error, '메시지 암호화에 실패했습니다.'), 'crypto');
    });
  }

  function decryptMessage(envelope) {
    var sequence = Number(pick(envelope, ['sequence']));
    var messageType = pick(envelope, ['messageType']);
    var nonce = pick(envelope, ['nonce']);
    var ciphertext = pick(envelope, ['ciphertext']);
    if (!session || !Number.isFinite(sequence) || !messageType || !nonce || !ciphertext) {
      return Promise.resolve(null);
    }

    return subtle.decrypt(
      {
        name: 'AES-GCM',
        iv: fromBase64(nonce),
        additionalData: aad(session.id, DIRECTION_IN, sequence, messageType),
        tagLength: 128
      },
      session.receiveKey,
      fromBase64(ciphertext)
    ).then(function (plaintext) {
      var body = {};
      try { body = JSON.parse(new TextDecoder().decode(plaintext)); } catch (error) { body = {}; }
      return { sequence: sequence, messageType: messageType, body: body };
    }, function () {
      // 위조되거나 순서가 어긋난 메시지는 조용히 버린다.
      return null;
    });
  }

  // ---------------------------------------------------------------- 스냅샷

  function participantPayload(item) {
    return {
      participantUuid: item.participantUUID || item.participantUuid || '',
      screenName: item.screenName || '',
      role: item.role || 'attendee'
    };
  }

  // Windows 쪽에서 호스트/공동호스트와 필수 SDK API를 검증할 수 있도록
  // 회의·사용자 컨텍스트와 supportedApis를 함께 담는다.
  function snapshotPlaintext(participants) {
    return {
      meetingId: zoom.meetingId,
      meetingUuid: zoom.meetingUuid,
      role: zoom.role,
      userId: zoom.userId,
      screenName: zoom.screenName,
      supportedApis: zoom.supported,
      capturedAt: new Date().toISOString(),
      participants: participants
    };
  }

  function sendSnapshot(reason) {
    if (!session || flags.sending) { return Promise.resolve(false); }
    flags.sending = true;
    setText(el.syncState, '전송 중');

    return sdk.getMeetingParticipants().then(function (result) {
      var participants = ((result && result.participants) || []).map(participantPayload).filter(function (item) {
        return item.participantUuid && item.screenName;
      });
      return sendMessage(MSG_SNAPSHOT, snapshotPlaintext(participants)).then(function () {
        setText(el.participantCount, String(participants.length));
        setText(el.lastSent, nowLabel());
        setText(el.syncState, '완료');
        show('참가자 ' + participants.length + '명을 암호화 전송했습니다' + (reason ? ' · ' + reason : '') + '.', 'ok');
        return true;
      });
    }).catch(function (error) {
      setText(el.syncState, '실패');
      show(redact(error, '참가자 정보를 전송하지 못했습니다.'), 'bad');
      if (error instanceof RedactedError && error.kind === 'session') { teardown('릴레이 세션이 만료되었습니다. 다시 연결하세요.'); }
      return false;
    }).then(function (result) {
      flags.sending = false;
      return result;
    });
  }

  // ---------------------------------------------------------------- 루프

  function heartbeat() {
    if (!session) { return; }
    sendMessage(MSG_HEARTBEAT, {
      meetingId: zoom.meetingId,
      meetingUuid: zoom.meetingUuid,
      role: zoom.role,
      supportedApis: zoom.supported,
      sentAt: new Date().toISOString()
    }).catch(function (error) {
      if (error instanceof RedactedError && error.kind === 'session') {
        teardown('릴레이 세션이 만료되었습니다. 다시 연결하세요.');
      }
    });
  }

  function poll() {
    if (!session || flags.polling) { return; }
    flags.polling = true;
    var current = session;
    var path = '/api/v1/sessions/' + encodeURIComponent(current.id) + '/companion/messages?afterSequence=' + current.lastReceived;

    relayFetch(path, { method: 'GET' }).then(function (payload) {
      var items = (payload && payload.messages) || [];
      return items.reduce(function (chain, envelope) {
        return chain.then(function () {
          return decryptMessage(envelope).then(handleCommand);
        });
      }, Promise.resolve());
    }).catch(function (error) {
      if (error instanceof RedactedError && error.kind === 'session') {
        teardown('릴레이 세션이 만료되었습니다. 다시 연결하세요.');
      }
    }).then(function () { flags.polling = false; });
  }

  function handleCommand(message) {
    if (!message || !session || message.sequence <= session.lastReceived) { return null; }
    session.lastReceived = message.sequence;
    if (message.messageType === MSG_SYNC_REQUEST) {
      setText(el.syncState, '요청 수신');
      return sendSnapshot('Windows 요청');
    }
    return null;
  }

  function startLoops() {
    clearTimers();
    timers.heartbeat = setInterval(heartbeat, HEARTBEAT_INTERVAL_MS);
    timers.snapshot = setInterval(function () { sendSnapshot('주기 동기화'); }, SNAPSHOT_INTERVAL_MS);
    timers.poll = setInterval(poll, HEARTBEAT_INTERVAL_MS);
  }

  function clearTimers() {
    ['heartbeat', 'snapshot', 'poll'].forEach(function (name) {
      if (timers[name]) { clearInterval(timers[name]); timers[name] = null; }
    });
    if (timers.debounce) { clearTimeout(timers.debounce); timers.debounce = null; }
  }

  // ---------------------------------------------------------------- 연결 상태

  function show(message, kind) {
    el.message.className = 'message' + (kind ? ' ' + kind : '');
    setText(el.message, message);
  }

  function roleLabel(role) {
    var normalized = String(role || '').toLowerCase();
    if (normalized === 'host') { return '호스트'; }
    if (normalized === 'cohost' || normalized === 'co-host') { return '공동호스트'; }
    return role ? String(role) : '미확인';
  }

  function isHostRole(role) {
    var normalized = String(role || '').toLowerCase();
    return normalized === 'host' || normalized === 'cohost' || normalized === 'co-host';
  }

  function setConnected(connected) {
    el.statusDot.className = 'status-dot ' + (connected ? 'ok' : '');
    setText(el.bridgeState, connected ? '연결됨' : '미연결');
    el.btnSync.disabled = !connected;
    el.btnDisconnect.disabled = !connected;
    el.btnReconnect.disabled = !connected;
    el.btnConnect.disabled = connected || !flags.sdkReady;
    el.pairingCode.disabled = connected;
  }

  // 세션 상태와 파생 키를 메모리에서 지운다.
  function teardown(message) {
    clearTimers();
    session = null;
    flags.sending = false;
    flags.polling = false;
    setConnected(false);
    setText(el.syncState, '대기');
    setText(el.participantCount, '–');
    if (message) { show(message, 'bad'); }
  }

  function disconnect() {
    var closing = session
      ? sendMessage(MSG_DISCONNECT, { closedAt: new Date().toISOString() }).catch(function () { return null; })
      : Promise.resolve(null);
    return closing.then(function () {
      teardown();
      el.pairingCode.value = '';
      show('연결을 해제했습니다.');
    });
  }

  function connect() {
    if (flags.connecting || session) { return; }
    var code = el.pairingCode.value.replace(/\D/g, '');
    if (code.length !== 6) { show('6자리 페어링 코드를 입력하세요.', 'bad'); return; }
    if (!isHostRole(zoom.role)) { show('호스트 또는 공동호스트만 연결할 수 있습니다.', 'bad'); return; }

    flags.connecting = true;
    el.btnConnect.disabled = true;
    show('Windows 앱과 안전하게 페어링하는 중…');

    redeem(code).then(function (established) {
      session = established;
      el.pairingCode.value = '';
      setConnected(true);
      startLoops();
      return sendSnapshot('최초 연결');
    }).catch(function (error) {
      teardown();
      show(redact(error, '연결에 실패했습니다.'), 'bad');
    }).then(function () {
      flags.connecting = false;
      el.btnConnect.disabled = !!session || !flags.sdkReady;
    });
  }

  function reconnect() {
    if (!session) { return; }
    show('릴레이 연결을 다시 확인하는 중…');
    startLoops();
    sendSnapshot('재연결');
  }

  // ---------------------------------------------------------------- Zoom SDK

  function initializeSdk() {
    if (!sdk) {
      show('Zoom Apps SDK를 불러오지 못했습니다. Zoom 클라이언트 안에서 다시 여세요.', 'bad');
      el.roleBadge.className = 'role-badge bad';
      setText(el.roleBadge, '사용 불가');
      return;
    }
    if (!subtle) {
      show('이 브라우저에서는 종단간 암호화를 사용할 수 없습니다.', 'bad');
      el.roleBadge.className = 'role-badge bad';
      setText(el.roleBadge, '사용 불가');
      return;
    }

    var stage = 'config';
    var context = { meeting: {}, uuid: {}, user: {} };

    sdk.config({ version: '0.16', capabilities: CAPABILITIES }).then(function () {
      stage = 'supported';
      return sdk.getSupportedJsApis();
    }).then(function (supported) {
      zoom.supported = (supported && supported.supportedApis) || [];
      stage = 'meeting';
      return sdk.getMeetingContext();
    }).then(function (meeting) {
      context.meeting = meeting || {};
      return sdk.getMeetingUUID().catch(function () { return {}; });
    }).then(function (uuid) {
      context.uuid = uuid || {};
      stage = 'user';
      return sdk.getUserContext();
    }).then(function (user) {
      context.user = user || {};
      stage = 'validation';
      zoom.meetingId = context.meeting.meetingID || context.meeting.meetingId || null;
      zoom.meetingUuid = context.uuid.meetingUUID || context.meeting.meetingUUID || null;
      zoom.role = context.user.role;
      zoom.userId = context.user.participantUUID || context.user.participantId || null;
      zoom.screenName = context.user.screenName || null;

      var missing = REQUIRED.filter(function (name) { return zoom.supported.indexOf(name) < 0; });
      var allowed = isHostRole(zoom.role);

      setText(el.meetingState, zoom.meetingId || '미확인');
      setText(el.roleState, roleLabel(zoom.role));
      setText(el.roleBadge, roleLabel(zoom.role));
      el.roleBadge.className = 'role-badge ' + (allowed && !missing.length ? 'ok' : 'bad');

      if (!allowed) { throw new RedactedError('현재 계정은 이 회의의 호스트 또는 공동호스트가 아닙니다.', 'role'); }
      if (missing.length) { throw new RedactedError('이 Zoom 클라이언트에서 필요한 기능을 사용할 수 없습니다.', 'capability'); }

      if (zoom.supported.indexOf('onParticipantChange') >= 0) {
        sdk.onParticipantChange(function () {
          if (timers.debounce) { clearTimeout(timers.debounce); }
          timers.debounce = setTimeout(function () { sendSnapshot('참가자 변경'); }, CHANGE_DEBOUNCE_MS);
        });
      }

      flags.sdkReady = true;
      el.btnConnect.disabled = false;
      show('권한 확인 완료. Windows 앱의 페어링 코드를 입력하세요.', 'ok');
    }).catch(function (error) {
      flags.sdkReady = false;
      el.btnConnect.disabled = true;
      el.roleBadge.className = 'role-badge bad';
      setText(el.roleBadge, '사용 불가');
      if (error instanceof RedactedError) {
        show(error.message, 'bad');
      } else if (stage === 'meeting') {
        show('회의 컨텍스트 확인 실패. 진행 중인 회의 창의 Apps에서 ZoomCheck를 다시 여세요. · ' + sdkDiagnostic(error), 'bad');
      } else {
        show('Zoom SDK 준비 실패 · ' + sdkStageLabel(stage) + ' · ' + sdkDiagnostic(error), 'bad');
      }
    });
  }

  // ---------------------------------------------------------------- 초기화

  function init() {
    ['role-badge', 'status-dot', 'meeting-state', 'role-state', 'bridge-state', 'participant-count',
      'last-sent', 'sync-state', 'relay-url', 'pairing-code', 'btn-connect', 'btn-sync', 'btn-reconnect',
      'btn-disconnect', 'message'].forEach(function (id) {
      el[id.replace(/-([a-z])/g, function (_, letter) { return letter.toUpperCase(); })] = $(id);
    });

    el.relayUrl.value = window.location.origin;
    flags.sdkReady = false;
    setConnected(false);
    el.btnConnect.disabled = true;

    el.pairingCode.addEventListener('input', function () {
      el.pairingCode.value = el.pairingCode.value.replace(/\D/g, '').slice(0, 6);
    });
    el.btnConnect.addEventListener('click', connect);
    el.btnSync.addEventListener('click', function () { sendSnapshot('수동 요청'); });
    el.btnReconnect.addEventListener('click', reconnect);
    el.btnDisconnect.addEventListener('click', function () { disconnect(); });
    window.addEventListener('pagehide', function () { clearTimers(); session = null; });

    initializeSdk();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
