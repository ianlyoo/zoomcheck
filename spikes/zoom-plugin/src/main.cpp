#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <bcrypt.h>
#include <shellapi.h>
#include <winhttp.h>

#include "ToolSuiteProxyInterface.h"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <optional>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

namespace {

using namespace ZMToolSuiteProxy;

std::string WideToUtf8(const wchar_t* wstr) {
    if (!wstr || *wstr == L'\0') return {};
    int needed = WideCharToMultiByte(CP_UTF8, 0, wstr, -1, nullptr, 0, nullptr, nullptr);
    if (needed <= 1) return {};
    std::string out(static_cast<size_t>(needed - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wstr, -1, out.data(), needed, nullptr, nullptr);
    return out;
}

std::wstring Utf8ToWide(const std::string& str) {
    if (str.empty()) return {};
    int needed = MultiByteToWideChar(CP_UTF8, 0, str.c_str(), -1, nullptr, 0);
    if (needed <= 1) return {};
    std::wstring out(static_cast<size_t>(needed - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, str.c_str(), -1, out.data(), needed);
    return out;
}

std::string JsonEscape(const std::string& input) {
    std::ostringstream oss;
    for (unsigned char c : input) {
        switch (c) {
            case '"': oss << "\\\""; break;
            case '\\': oss << "\\\\"; break;
            case '\b': oss << "\\b"; break;
            case '\f': oss << "\\f"; break;
            case '\n': oss << "\\n"; break;
            case '\r': oss << "\\r"; break;
            case '\t': oss << "\\t"; break;
            default:
                if (c < 0x20) {
                    oss << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(c);
                } else {
                    oss << static_cast<char>(c);
                }
        }
    }
    return oss.str();
}

std::string Base64UrlEncode(const uint8_t* data, size_t len) {
    static constexpr char kAlphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    std::string out;
    out.reserve(((len + 2) / 3) * 4);
    for (size_t i = 0; i < len; i += 3) {
        uint32_t v = static_cast<uint32_t>(data[i]) << 16;
        if (i + 1 < len) v |= static_cast<uint32_t>(data[i + 1]) << 8;
        if (i + 2 < len) v |= static_cast<uint32_t>(data[i + 2]);
        out.push_back(kAlphabet[(v >> 18) & 0x3F]);
        out.push_back(kAlphabet[(v >> 12) & 0x3F]);
        if (i + 1 < len) out.push_back(kAlphabet[(v >> 6) & 0x3F]);
        if (i + 2 < len) out.push_back(kAlphabet[v & 0x3F]);
    }
    return out;
}

std::string RandomBase64Url(size_t byte_count) {
    std::vector<uint8_t> buf(byte_count);
    NTSTATUS st = BCryptGenRandom(nullptr, buf.data(), static_cast<ULONG>(buf.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    if (st < 0) return {};
    return Base64UrlEncode(buf.data(), buf.size());
}

std::string Sha256Base64Url(const std::string& input) {
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    uint8_t digest[32] = {};
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return {};
    if (BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0) < 0) {
        BCryptCloseAlgorithmProvider(alg, 0);
        return {};
    }
    bool ok = BCryptHashData(hash, reinterpret_cast<PUCHAR>(const_cast<char*>(input.data())), static_cast<ULONG>(input.size()), 0) >= 0 &&
              BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(alg, 0);
    return ok ? Base64UrlEncode(digest, sizeof(digest)) : std::string{};
}

std::string UrlEncode(const std::string& value) {
    std::ostringstream oss;
    oss << std::uppercase << std::hex;
    for (unsigned char c : value) {
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
            c == '-' || c == '_' || c == '.' || c == '~') {
            oss << static_cast<char>(c);
        } else {
            oss << '%' << std::setw(2) << std::setfill('0') << static_cast<int>(c);
        }
    }
    return oss.str();
}

std::string UrlDecode(const std::string& value) {
    std::string out;
    out.reserve(value.size());
    for (size_t i = 0; i < value.size(); ++i) {
        if (value[i] == '%' && i + 2 < value.size()) {
            unsigned int hex_val = 0;
            std::istringstream iss(value.substr(i + 1, 2));
            if (iss >> std::hex >> hex_val) {
                out.push_back(static_cast<char>(hex_val));
                i += 2;
                continue;
            }
        } else if (value[i] == '+') {
            out.push_back(' ');
            continue;
        }
        out.push_back(value[i]);
    }
    return out;
}

std::optional<std::string> ExtractQueryParam(const std::string& request_target, const std::string& key) {
    auto qpos = request_target.find('?');
    if (qpos == std::string::npos) return std::nullopt;
    std::istringstream iss(request_target.substr(qpos + 1));
    std::string pair;
    while (std::getline(iss, pair, '&')) {
        auto eq = pair.find('=');
        if (eq != std::string::npos && pair.substr(0, eq) == key) {
            return UrlDecode(pair.substr(eq + 1));
        }
    }
    return std::nullopt;
}

struct LoopbackServer {
    SOCKET listen_sock = INVALID_SOCKET;
    uint16_t port = 0;

    bool Start() {
        listen_sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (listen_sock == INVALID_SOCKET) return false;

        sockaddr_in addr{};
        addr.sin_family = AF_INET;
        addr.sin_port = htons(0);
        inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);

        if (bind(listen_sock, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR) {
            Close();
            return false;
        }
        if (listen(listen_sock, 4) == SOCKET_ERROR) {
            Close();
            return false;
        }

        int addr_len = sizeof(addr);
        if (getsockname(listen_sock, reinterpret_cast<sockaddr*>(&addr), &addr_len) == SOCKET_ERROR) {
            Close();
            return false;
        }
        port = ntohs(addr.sin_port);
        return true;
    }

    std::optional<std::string> WaitForAuthorizationCode(const std::string& expected_state, int timeout_seconds) {
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(timeout_seconds);
        while (std::chrono::steady_clock::now() < deadline) {
            auto rem = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now());
            if (rem.count() <= 0) break;

            fd_set read_fds;
            FD_ZERO(&read_fds);
            FD_SET(listen_sock, &read_fds);
            timeval tv{};
            tv.tv_sec = static_cast<long>(std::min<long long>(rem.count() / 1000, 1));
            tv.tv_usec = 200000;

            int ready = select(0, &read_fds, nullptr, nullptr, &tv);
            if (ready <= 0) continue;

            SOCKET client = accept(listen_sock, nullptr, nullptr);
            if (client == INVALID_SOCKET) continue;

            char buf[4096] = {};
            int received = recv(client, buf, sizeof(buf) - 1, 0);
            std::string req(received > 0 ? buf : "");

            std::string target;
            auto line_end = req.find("\r\n");
            if (line_end != std::string::npos) {
                std::istringstream line(req.substr(0, line_end));
                std::string method;
                line >> method >> target;
            }

            if (target.rfind("/callback", 0) != 0) {
                const char* nf = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                send(client, nf, static_cast<int>(std::strlen(nf)), 0);
                closesocket(client);
                continue;
            }

            auto err = ExtractQueryParam(target, "error");
            auto state = ExtractQueryParam(target, "state");
            auto code = ExtractQueryParam(target, "code");
            bool valid = !err.has_value() && state.has_value() && *state == expected_state && code.has_value() && !code->empty();

            const char* body = valid
                ? "<html><body><h3>ZoomCheck connected.</h3><p>You can close this window.</p></body></html>"
                : "<html><body><h3>Authorization failed.</h3></body></html>";
            std::ostringstream resp;
            resp << (valid ? "HTTP/1.1 200 OK\r\n" : "HTTP/1.1 400 Bad Request\r\n")
                 << "Content-Type: text/html; charset=utf-8\r\n"
                 << "Connection: close\r\n"
                 << "Content-Length: " << std::strlen(body) << "\r\n\r\n"
                 << body;
            std::string resp_str = resp.str();
            send(client, resp_str.data(), static_cast<int>(resp_str.size()), 0);
            closesocket(client);
            Close();

            if (err.has_value()) {
                std::cerr << "[oauth] Zoom returned error=" << *err << "\n";
            }
            return valid ? code : std::nullopt;
        }
        Close();
        return std::nullopt;
    }

    void Close() {
        if (listen_sock != INVALID_SOCKET) {
            closesocket(listen_sock);
            listen_sock = INVALID_SOCKET;
        }
    }

    ~LoopbackServer() { Close(); }
};

std::optional<std::string> ExtractJsonStringField(const std::string& json, const std::string& key) {
    std::string needle = "\"" + key + "\"";
    auto kpos = json.find(needle);
    if (kpos == std::string::npos) return std::nullopt;
    auto colon = json.find(':', kpos + needle.size());
    if (colon == std::string::npos) return std::nullopt;
    auto q1 = json.find('"', colon + 1);
    if (q1 == std::string::npos) return std::nullopt;
    std::string val;
    for (size_t i = q1 + 1; i < json.size(); ++i) {
        if (json[i] == '\\' && i + 1 < json.size()) {
            val.push_back(json[++i]);
        } else if (json[i] == '"') {
            return val;
        } else {
            val.push_back(json[i]);
        }
    }
    return std::nullopt;
}

std::optional<std::string> ExchangePublicPkceToken(
    const std::string& client_id,
    const std::string& code,
    const std::string& redirect_uri,
    const std::string& code_verifier) {
    std::string form =
        "grant_type=authorization_code"
        "&client_id=" + UrlEncode(client_id) +
        "&code=" + UrlEncode(code) +
        "&redirect_uri=" + UrlEncode(redirect_uri) +
        "&code_verifier=" + UrlEncode(code_verifier);

    HINTERNET session = WinHttpOpen(L"ZoomCheck-Plugin-Probe/0.2", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, nullptr, nullptr, 0);
    if (!session) return std::nullopt;
    HINTERNET connect = WinHttpConnect(session, L"zoom.us", INTERNET_DEFAULT_HTTPS_PORT, 0);
    if (!connect) {
        WinHttpCloseHandle(session);
        return std::nullopt;
    }
    HINTERNET request = WinHttpOpenRequest(connect, L"POST", L"/oauth/token", nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE);
    if (!request) {
        WinHttpCloseHandle(connect);
        WinHttpCloseHandle(session);
        return std::nullopt;
    }

    const wchar_t* headers = L"Content-Type: application/x-www-form-urlencoded\r\n";
    BOOL sent = WinHttpSendRequest(
        request,
        headers,
        static_cast<DWORD>(-1L),
        const_cast<char*>(form.data()),
        static_cast<DWORD>(form.size()),
        static_cast<DWORD>(form.size()),
        0);

    std::string response_body;
    DWORD status_code = 0;
    if (sent && WinHttpReceiveResponse(request, nullptr)) {
        DWORD status_size = sizeof(status_code);
        WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status_code, &status_size, WINHTTP_NO_HEADER_INDEX);
        DWORD avail = 0;
        while (WinHttpQueryDataAvailable(request, &avail) && avail > 0) {
            std::string chunk(avail, '\0');
            DWORD read = 0;
            if (WinHttpReadData(request, chunk.data(), avail, &read) && read > 0) {
                response_body.append(chunk.data(), read);
            }
        }
    }

    WinHttpCloseHandle(request);
    WinHttpCloseHandle(connect);
    WinHttpCloseHandle(session);

    if (status_code != 200) {
        auto err = ExtractJsonStringField(response_body, "reason");
        std::cerr << "[oauth] token exchange failed with HTTP " << status_code
                  << (err ? (" (" + *err + ")") : "") << "\n";
        return std::nullopt;
    }
    auto scope = ExtractJsonStringField(response_body, "scope");
    if (scope) {
        std::cout << "[oauth] token exchange succeeded, granted scope=\"" << *scope << "\"\n" << std::flush;
    } else {
        std::cout << "[oauth] token exchange succeeded\n" << std::flush;
    }
    return ExtractJsonStringField(response_body, "access_token");
}

struct CapturedParticipant {
    unsigned int user_id = 0;
    std::string display_name;
    bool is_host = false;
    bool is_myself = false;
    bool is_attendee = false;
    bool in_waiting_room = false;
    std::string role = "Unknown";
};

std::string FormatSnapshotJson(
    const std::string& meeting_num,
    const std::string& meeting_topic,
    int meeting_status,
    const std::vector<CapturedParticipant>& participants) {
    std::ostringstream oss;
    oss << "{\n"
        << "  \"meetingId\": \"" << JsonEscape(meeting_num) << "\",\n"
        << "  \"meetingTopic\": \"" << JsonEscape(meeting_topic) << "\",\n"
        << "  \"meetingStatus\": " << meeting_status << ",\n"
        << "  \"source\": \"zoom-plugin-sdk\",\n"
        << "  \"participantNames\": [";
    for (size_t i = 0; i < participants.size(); ++i) {
        if (i > 0) oss << ", ";
        oss << "\"" << JsonEscape(participants[i].display_name) << "\"";
    }
    oss << "],\n"
        << "  \"participants\": [\n";
    for (size_t i = 0; i < participants.size(); ++i) {
        const auto& p = participants[i];
        oss << "    {\"presenceKey\": \"plugin:" << p.user_id << "\", "
            << "\"userId\": " << p.user_id << ", "
            << "\"displayName\": \"" << JsonEscape(p.display_name) << "\", "
            << "\"email\": null, "
            << "\"isHost\": " << (p.is_host ? "true" : "false") << ", "
            << "\"isMyself\": " << (p.is_myself ? "true" : "false") << ", "
            << "\"isAttendee\": " << (p.is_attendee ? "true" : "false") << ", "
            << "\"inWaitingRoom\": " << (p.in_waiting_room ? "true" : "false") << ", "
            << "\"role\": \"" << JsonEscape(p.role) << "\"}";
        if (i + 1 < participants.size()) oss << ",";
        oss << "\n";
    }
    oss << "  ]\n"
        << "}";
    return oss.str();
}

const char* AuthResultName(ZMToolSuiteProxyAuthResult r) {
    switch (r) {
        case AUTH_RESULT_SUCCESS: return "AUTH_RESULT_SUCCESS";
        case AUTH_RESULT_REQUEST_ERROR: return "AUTH_RESULT_REQUEST_ERROR";
        case AUTH_RESULT_VERIFY_USER_FAILED: return "AUTH_RESULT_VERIFY_USER_FAILED";
        case AUTH_RESULT_FAILED: return "AUTH_RESULT_FAILED";
        default: return "UNKNOWN";
    }
}

const char* IpcStatusName(ZMToolSuiteIPCConnectStatus s) {
    switch (s) {
        case IPC_STATUS_IDLE: return "IPC_STATUS_IDLE";
        case IPC_STATUS_CONNECT_TIMEOUT: return "IPC_STATUS_CONNECT_TIMEOUT";
        case IPC_STATUS_CONNECTED: return "IPC_STATUS_CONNECTED";
        case IPC_STATUS_DISCONNECTED: return "IPC_STATUS_DISCONNECTED";
        default: return "UNKNOWN";
    }
}

const char* UserActionName(ZMToolSuiteUserAction a) {
    switch (a) {
        case USER_ACTION_NO_ACTION: return "NO_ACTION";
        case USER_ACTION_SYNC_ALL: return "SYNC_ALL";
        case USER_ACTION_ADD_USER: return "ADD_USER";
        case USER_ACTION_LEFT_USER: return "LEFT_USER";
        case USER_ACTION_UPDATE_USER: return "UPDATE_USER";
        default: return "UNKNOWN";
    }
}

const char* UserRoleName(ZMToolSuiteUserRole role) {
    switch (role) {
        case ZMToolSuiteUserRole_Host: return "Host";
        case ZMToolSuiteUserRole_CoHost: return "CoHost";
        case ZMToolSuiteUserRole_Attendee: return "Attendee";
        case ZMToolSuiteUserRole_Panelist: return "Panelist";
        case ZMToolSuiteUserRole_BreakoutRoomModerator: return "BreakoutRoomModerator";
        default: return "None";
    }
}

template <typename Pred>
bool PumpWaitUntil(int timeout_ms, Pred pred) {
    auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
    while (std::chrono::steady_clock::now() < deadline) {
        if (pred()) return true;
        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        if (pred()) return true;
        MsgWaitForMultipleObjects(0, nullptr, FALSE, 25, QS_ALLINPUT);
    }
    return pred();
}

class ProbeListener final : public IZMToolSuiteProxyListener {
public:
    std::mutex mu;
    bool auth_done = false;
    ZMToolSuiteProxyAuthResult auth_result = AUTH_RESULT_FAILED;
    bool ipc_connected = false;
    int user_event_count = 0;
    ZMToolSuiteMeetingStatus last_meeting_status = MEETING_STATUS_IDLE;

    void OnAuthResult(ZMToolSuiteProxyAuthResult eResult) override {
        std::cout << "[sdk] OnAuthResult: " << AuthResultName(eResult) << "\n" << std::flush;
        std::lock_guard<std::mutex> lock(mu);
        auth_done = true;
        auth_result = eResult;
    }

    void OnIPCConnectStatusChanged(ZMToolSuiteIPCConnectStatus eStatus, std::string errorMessage) override {
        std::cout << "[sdk] OnIPCConnectStatusChanged: " << IpcStatusName(eStatus);
        if (!errorMessage.empty()) std::cout << " (" << errorMessage << ")";
        std::cout << "\n" << std::flush;
        std::lock_guard<std::mutex> lock(mu);
        ipc_connected = (eStatus == IPC_STATUS_CONNECTED);
    }

    void OnMeetingStatusChanged(ZMToolSuiteProxy::ZMToolSuiteMeetingStatus status) override {
        std::cout << "[sdk] OnMeetingStatusChanged: " << static_cast<int>(status) << "\n" << std::flush;
        std::lock_guard<std::mutex> lock(mu);
        last_meeting_status = status;
    }

    void OnUserStatusChanged(ZMToolSuiteUserList vUserList, ZMToolSuiteMeetingInstance eInstance) override {
        int count = vUserList.vUserList ? vUserList.vUserList->GetCount() : 0;
        std::cout << "[sdk] OnUserStatusChanged: action=" << UserActionName(vUserList.eUserAction)
                  << " err=" << static_cast<int>(vUserList.error_code)
                  << " instance=" << static_cast<int>(eInstance)
                  << " count=" << count << "\n";
        if (vUserList.vUserList) {
            for (int i = 0; i < count; ++i) {
                ZMToolSuiteUser u = vUserList.vUserList->GetItem(i);
                std::cout << "  -> userID=" << u.iUserID
                          << " name=\"" << WideToUtf8(u.strUserName) << "\""
                          << " bHost=" << (u.bHost ? "true" : "false")
                          << " bMyself=" << (u.bMyself ? "true" : "false")
                          << " bInWaitingRoom=" << (u.bInWaitingRoom ? "true" : "false") << "\n";
            }
        }
        std::cout << std::flush;
        std::lock_guard<std::mutex> lock(mu);
        user_event_count++;
    }

    void OnMeetingShareOptionsChanged(ZMToolSuiteShareOptions&) override {}
    void OnUserRaisedHand(ZMToolSuiteMeetingInstance, unsigned int) override {}
    void OnUserLowerHand(ZMToolSuiteMeetingInstance, unsigned int) override {}
    void OnRecordingStatusChanged(ZMToolSuiteMeetingInstance) override {}
    void OnEmojiReactionReceived(unsigned int, SendEmojiReactionParam, ZMToolSuiteMeetingInstance) override {}
    void OnEmojiReactionReceivedInWebinar(IToolSuiteVector<WebinarEmojiContent>*, ZMToolSuiteMeetingInstance) override {}
    void OnEmojiFeedbackReceived(unsigned int, IPCFeedbackReactionType, ZMToolSuiteMeetingInstance) override {}
    void OnAllHandsLowered(ZMToolSuiteMeetingInstance) override {}
    void OnGroupLayoutUpdate(ZMToolSuiteMeetingInstance) override {}
    void OnMyVideoStatusChange(bool, ZMToolSuiteMeetingInstance) override {}
    void OnStartShareAudio(ZMToolSuiteMeetingInstance) override {}
    void OnStopShareAudio(ZMToolSuiteMeetingInstance) override {}
    void OnStartViewAudio(unsigned int, ZMToolSuiteMeetingInstance) override {}
    void OnStopViewAudio(unsigned int, ZMToolSuiteMeetingInstance) override {}
    void OnShareSendStatusChanged(unsigned int, bool, ZMToolSuiteMeetingInstance) override {}
    void OnStartSendShare(ZMToolSuiteMeetingInstance) override {}
    void OnStopSendShare(ZMToolSuiteMeetingInstance) override {}
    void OnShareContentTypeChanged(ZMToolSuiteProxyShareContentType, unsigned int, ZMToolSuiteMeetingInstance) override {}
    void OnShareStatusChanged(unsigned int, ZMToolSuiteShareStatus, ZMToolSuiteMeetingInstance) override {}
    void OnSharedVideoEnded(ZMToolSuiteMeetingInstance) override {}
    void OnVideoFileSharePlayError(ZMToolSuiteProxySharePlayError, ZMToolSuiteMeetingInstance) override {}
    void OnMyAudioStatusChanged(bool, ZMToolSuiteMeetingInstance) override {}
    void OnOriginalSoundStatusChange(bool, ZMToolSuiteMeetingInstance) override {}
    void OnUnreadChatCountChange(unsigned int, ZMToolSuiteMeetingInstance) override {}
    void OnChatWindowStatusChange(bool, ZMToolSuiteMeetingInstance) override {}
    void OnMeetingViewTypeChange(IPCMeetingViewType, ZMToolSuiteMeetingInstance) override {}
    void OnFullScreenStatusChange(bool, ZMToolSuitMonitorType, ZMToolSuiteMeetingInstance) override {}
};

struct ProbeOptions {
    int wait_seconds = 20;
    int watch_seconds = 0;
    std::string out_json_path;
    std::string rename_match;
    std::string rename_to;
};

std::vector<CapturedParticipant> CaptureParticipants(
    meeting::IMeetingToolkit* meeting_api,
    meeting::IParticipantsToolkit* participants_api) {
    std::vector<CapturedParticipant> captured;
    IToolSuiteVector<ZMToolSuiteUser>* list = meeting_api->GetParticipantsList();
    if (!list) return captured;
    int count = list->GetCount();
    captured.reserve(static_cast<size_t>(count));
    for (int i = 0; i < count; ++i) {
        ZMToolSuiteUser u = list->GetItem(i);
        CapturedParticipant cp;
        cp.user_id = u.iUserID;
        cp.display_name = WideToUtf8(u.strUserName);
        cp.is_host = u.bHost;
        cp.is_myself = u.bMyself;
        cp.is_attendee = u.bAttendee;
        cp.in_waiting_room = u.bInWaitingRoom;
        if (participants_api) {
            std::atomic<bool> done{false};
            std::string role_name = "Unknown";
            participants_api->GetUserRoleByUserID(u.iUserID, [&](const GetUserRoleByUserIDResult& r) {
                if (r.error_code == kZMToolSuiteProxyErrorsSuccess) {
                    role_name = UserRoleName(r.role);
                } else {
                    role_name = "Err:" + std::to_string(static_cast<int>(r.error_code));
                }
                done.store(true);
            });
            PumpWaitUntil(500, [&] { return done.load(); });
            cp.role = role_name;
        }
        captured.push_back(std::move(cp));
    }
    return captured;
}

void WriteOutJson(const std::string& path, const std::string& json) {
    if (path.empty()) return;
    std::ofstream ofs(path, std::ios::binary | std::ios::trunc);
    if (ofs) {
        ofs << json << "\n";
    }
}

int RunPluginSession(const std::wstring& access_token, const ProbeOptions& opts) {
    ProbeListener listener;
    InitZMToolSuite();

    ZMToolSuiteProxyAuthContext ctx;
    ctx.accessToken = access_token.c_str();
    ctx.domain = L"https://zoom.us";

    if (!StartToolSuiteAuth(ctx)) {
        std::cerr << "[sdk] StartToolSuiteAuth returned false\n";
        UninitZMToolSuite();
        return 2;
    }
    SetToolSuiteProxyListener(&listener);

    PumpWaitUntil(opts.wait_seconds * 1000, [&] {
        std::lock_guard<std::mutex> lock(listener.mu);
        return listener.auth_done && listener.ipc_connected;
    });

    bool auth_ok = false;
    bool ipc_ok = false;
    ZMToolSuiteProxyAuthResult auth_res = AUTH_RESULT_FAILED;
    {
        std::lock_guard<std::mutex> lock(listener.mu);
        auth_ok = listener.auth_done && listener.auth_result == AUTH_RESULT_SUCCESS;
        ipc_ok = listener.ipc_connected;
        auth_res = listener.auth_result;
    }

    if (!auth_ok) {
        std::cerr << "[sdk] Auth did not succeed (result=" << AuthResultName(auth_res) << ")\n";
        SetToolSuiteProxyListener(nullptr);
        UninitZMToolSuite();
        return 3;
    }

    if (!ipc_ok) {
        std::cerr << "[sdk] IPC did not connect within " << opts.wait_seconds << "s\n";
        SetToolSuiteProxyListener(nullptr);
        UninitZMToolSuite();
        return 4;
    }

    auto* meeting_api = meeting::GetMeetingToolkit(ZMToolSuiteMeetingInstance_Default);
    auto* participants_api = meeting::GetParticipantsToolkit(ZMToolSuiteMeetingInstance_Default);
    if (!meeting_api || !participants_api) {
        std::cerr << "[sdk] Meeting or Participants toolkit unavailable\n";
        SetToolSuiteProxyListener(nullptr);
        UninitZMToolSuite();
        return 5;
    }

    ZMToolSuiteMeetingStatus initial_status = meeting_api->GetMeetingStatus();
    std::cout << "[sdk] Immediate GetMeetingStatus() after IPC connect: " << static_cast<int>(initial_status) << "\n" << std::flush;
    if (initial_status != MEETING_STATUS_INMEETING) {
        std::cout << "[sdk] Waiting up to " << opts.wait_seconds << "s for MEETING_STATUS_INMEETING...\n" << std::flush;
        PumpWaitUntil(opts.wait_seconds * 1000, [&] {
            return meeting_api->GetMeetingStatus() == MEETING_STATUS_INMEETING;
        });
    }

    ZMToolSuiteMeetingStatus cur_status = meeting_api->GetMeetingStatus();
    ZMToolSuiteGetMeetingProperty prop = meeting_api->GetMeetingProperty();
    std::string meeting_num = WideToUtf8(prop.strCurrentMeetingNum);
    std::string meeting_topic = WideToUtf8(prop.strMeetingTopic);
    std::cout << "[sdk] Meeting status=" << static_cast<int>(cur_status)
              << " propErr=" << static_cast<int>(prop.error_code)
              << " num=" << meeting_num
              << " topic=\"" << meeting_topic << "\"\n" << std::flush;

    ZMToolSuiteUser me = participants_api->GetMySelf();
    std::cout << "[sdk] Self userID=" << me.iUserID << " name=\"" << WideToUtf8(me.strUserName)
              << "\" bHost=" << (me.bHost ? "true" : "false") << "\n" << std::flush;

    auto captured = CaptureParticipants(meeting_api, participants_api);
    std::string json = FormatSnapshotJson(meeting_num, meeting_topic, static_cast<int>(cur_status), captured);
    std::cout << json << "\n" << std::flush;
    WriteOutJson(opts.out_json_path, json);

    if (opts.watch_seconds > 0) {
        std::cout << "[sdk] Watching live events for " << opts.watch_seconds << "s...\n" << std::flush;
        auto end_time = std::chrono::steady_clock::now() + std::chrono::seconds(opts.watch_seconds);
        int last_events = 0;
        {
            std::lock_guard<std::mutex> lock(listener.mu);
            last_events = listener.user_event_count;
        }
        bool rename_done = false;
        auto next_poll = std::chrono::steady_clock::now() + std::chrono::seconds(3);

        while (std::chrono::steady_clock::now() < end_time) {
            PumpWaitUntil(250, [&] {
                std::lock_guard<std::mutex> lock(listener.mu);
                return listener.user_event_count != last_events;
            });

            bool changed = false;
            {
                std::lock_guard<std::mutex> lock(listener.mu);
                if (listener.user_event_count != last_events) {
                    last_events = listener.user_event_count;
                    changed = true;
                }
            }

            if (changed || std::chrono::steady_clock::now() >= next_poll) {
                next_poll = std::chrono::steady_clock::now() + std::chrono::seconds(3);
                cur_status = meeting_api->GetMeetingStatus();
                prop = meeting_api->GetMeetingProperty();
                meeting_num = WideToUtf8(prop.strCurrentMeetingNum);
                meeting_topic = WideToUtf8(prop.strMeetingTopic);
                auto updated = CaptureParticipants(meeting_api, participants_api);

                if (changed || updated.size() != captured.size()) {
                    captured = std::move(updated);
                    json = FormatSnapshotJson(meeting_num, meeting_topic, static_cast<int>(cur_status), captured);
                    std::cout << "[sdk] Snapshot update (count=" << captured.size() << "):\n" << json << "\n" << std::flush;
                    WriteOutJson(opts.out_json_path, json);
                }

                if (!rename_done && !opts.rename_match.empty() && !opts.rename_to.empty()) {
                    for (const auto& p : captured) {
                        if (p.display_name.find(opts.rename_match) != std::string::npos) {
                            rename_done = true;
                            std::wstring wnew = Utf8ToWide(opts.rename_to);
                            std::cout << "[sdk] Testing ChangeUserName(userID=" << p.user_id
                                      << ", \"" << p.display_name << "\" -> \"" << opts.rename_to << "\")...\n" << std::flush;
                            std::atomic<bool> rdone{false};
                            bool submitted = meeting_api->ChangeUserName(p.user_id, wnew.c_str(), [&](const BaseResult& r) {
                                std::cout << "[sdk] ChangeUserName callback error_code=" << static_cast<int>(r.error_code) << "\n" << std::flush;
                                rdone.store(true);
                            });
                            std::cout << "[sdk] ChangeUserName submitted=" << (submitted ? "true" : "false") << "\n" << std::flush;
                            PumpWaitUntil(5000, [&] { return rdone.load(); });
                            break;
                        }
                    }
                }
            }
        }

        // Final snapshot at end of watch window
        cur_status = meeting_api->GetMeetingStatus();
        prop = meeting_api->GetMeetingProperty();
        meeting_num = WideToUtf8(prop.strCurrentMeetingNum);
        meeting_topic = WideToUtf8(prop.strMeetingTopic);
        captured = CaptureParticipants(meeting_api, participants_api);
        json = FormatSnapshotJson(meeting_num, meeting_topic, static_cast<int>(cur_status), captured);
        std::cout << "[sdk] Final snapshot:\n" << json << "\n" << std::flush;
        WriteOutJson(opts.out_json_path, json);
    }

    SetToolSuiteProxyListener(nullptr);
    UninitZMToolSuite();
    return captured.empty() ? 6 : 0;
}

int RunSelfTest() {
    const std::string rfc_verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    const std::string rfc_expected = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    const std::string actual = Sha256Base64Url(rfc_verifier);
    if (actual != rfc_expected) {
        std::cerr << "[self-test] FAIL PKCE S256: got " << actual << " expected " << rfc_expected << "\n";
        return 10;
    }
    std::cout << "[self-test] PASS PKCE S256 RFC 7636 vector\n";

    LoopbackServer server;
    if (!server.Start() || server.port == 0) {
        std::cerr << "[self-test] FAIL LoopbackServer::Start\n";
        return 11;
    }
    const std::string expected_state = RandomBase64Url(16);
    const uint16_t port = server.port;

    std::thread client_thread([port, expected_state] {
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (s == INVALID_SOCKET) return;
        sockaddr_in addr{};
        addr.sin_family = AF_INET;
        addr.sin_port = htons(port);
        inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);
        if (connect(s, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == 0) {
            std::string req = "GET /callback?code=probe_auth_code_123&state=" + expected_state +
                              " HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
            send(s, req.data(), static_cast<int>(req.size()), 0);
            char buf[512];
            recv(s, buf, sizeof(buf), 0);
        }
        closesocket(s);
    });

    auto code = server.WaitForAuthorizationCode(expected_state, 5);
    client_thread.join();
    if (!code.has_value() || *code != "probe_auth_code_123") {
        std::cerr << "[self-test] FAIL loopback callback capture\n";
        return 12;
    }
    std::cout << "[self-test] PASS RFC 8252 loopback redirect on 127.0.0.1:" << port << "\n";

#if defined(ZOOM_PLUGIN_SDK_STUB)
    std::cout << "[self-test] Running ZMToolSuiteProxy lifecycle against offline 7.1.0.2020 contract stub\n";
    ProbeOptions opts;
    opts.wait_seconds = 2;
    int rc = RunPluginSession(L"stub-access-token", opts);
    if (rc != 0) {
        std::cerr << "[self-test] FAIL stub SDK lifecycle rc=" << rc << "\n";
        return rc;
    }
    std::cout << "[self-test] PASS ZMToolSuiteProxy contract stub + snapshot JSON\n";
#else
    std::cout << "[self-test] Built against real Zoom Plugin SDK; skipping live IPC in --self-test\n";
#endif
    return 0;
}

std::string ReadTrimmedFile(const std::string& path) {
    std::ifstream ifs(path);
    if (!ifs) return {};
    std::ostringstream oss;
    oss << ifs.rdbuf();
    std::string s = oss.str();
    while (!s.empty() && (s.back() == '\r' || s.back() == '\n' || s.back() == ' ' || s.back() == '\t')) {
        s.pop_back();
    }
    return s;
}

} // namespace

int main(int argc, char** argv) {
    SetConsoleOutputCP(CP_UTF8);

    WSADATA wsa{};
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        std::cerr << "WSAStartup failed\n";
        return 1;
    }

    int exit_code = 0;
    std::string client_id;
    std::string access_token;
    std::string token_file;
    bool self_test = false;
    bool no_browser = false;
    ProbeOptions opts;

    for (int i = 1; i < argc; ++i) {
        std::string arg = argv[i];
        if (arg == "--self-test") {
            self_test = true;
        } else if (arg == "--no-browser") {
            no_browser = true;
        } else if (arg == "--client-id" && i + 1 < argc) {
            client_id = argv[++i];
        } else if (arg == "--access-token" && i + 1 < argc) {
            access_token = argv[++i];
        } else if (arg == "--token-file" && i + 1 < argc) {
            token_file = argv[++i];
        } else if (arg == "--wait" && i + 1 < argc) {
            opts.wait_seconds = std::stoi(argv[++i]);
        } else if (arg == "--watch" && i + 1 < argc) {
            opts.watch_seconds = std::stoi(argv[++i]);
        } else if (arg == "--out" && i + 1 < argc) {
            opts.out_json_path = argv[++i];
        } else if (arg == "--rename-match" && i + 1 < argc) {
            opts.rename_match = argv[++i];
        } else if (arg == "--rename-to" && i + 1 < argc) {
            opts.rename_to = argv[++i];
        }
    }

    if (self_test) {
        exit_code = RunSelfTest();
    } else {
        if (access_token.empty() && !token_file.empty()) {
            access_token = ReadTrimmedFile(token_file);
        }

        if (access_token.empty() && !client_id.empty()) {
            LoopbackServer server;
            if (!server.Start()) {
                std::cerr << "[oauth] Could not start 127.0.0.1 loopback listener\n";
                WSACleanup();
                return 1;
            }
            std::string verifier = RandomBase64Url(32);
            std::string challenge = Sha256Base64Url(verifier);
            std::string state = RandomBase64Url(16);
            std::string redirect_uri = "http://127.0.0.1:" + std::to_string(server.port) + "/callback";
            std::string auth_url =
                "https://zoom.us/oauth/authorize?response_type=code"
                "&client_id=" + UrlEncode(client_id) +
                "&redirect_uri=" + UrlEncode(redirect_uri) +
                "&code_challenge=" + UrlEncode(challenge) +
                "&code_challenge_method=S256"
                "&state=" + UrlEncode(state);

            std::cout << "AUTH_URL=" << auth_url << "\n" << std::flush;
            if (!no_browser) {
                std::cout << "[oauth] Opening default browser for Zoom PKCE authorization (" << redirect_uri << ")...\n" << std::flush;
                std::wstring wurl = Utf8ToWide(auth_url);
                ShellExecuteW(nullptr, L"open", wurl.c_str(), nullptr, nullptr, SW_SHOW);
            } else {
                std::cout << "[oauth] Waiting for loopback callback on " << redirect_uri << " (--no-browser)...\n" << std::flush;
            }

            auto code = server.WaitForAuthorizationCode(state, 240);
            if (!code.has_value()) {
                std::cerr << "[oauth] Timed out or invalid state waiting for loopback callback\n";
                exit_code = 1;
            } else {
                auto token = ExchangePublicPkceToken(client_id, *code, redirect_uri, verifier);
                if (!token.has_value()) {
                    exit_code = 1;
                } else {
                    access_token = *token;
                    if (!token_file.empty()) {
                        std::ofstream ofs(token_file, std::ios::binary | std::ios::trunc);
                        if (ofs) ofs << access_token;
                    }
                }
            }
        }

        if (!access_token.empty()) {
            exit_code = RunPluginSession(Utf8ToWide(access_token), opts);
        } else if (exit_code == 0) {
            std::cout << "Usage:\n"
                      << "  zoomcheck-plugin-probe --self-test\n"
                      << "  zoomcheck-plugin-probe --client-id <ZOOM_PUBLIC_CLIENT_ID> [--no-browser] [--watch 60] [--out snapshot.json]\n"
                      << "  zoomcheck-plugin-probe --token-file <path> [--watch 60] [--out snapshot.json] [--rename-match <substr> --rename-to <name>]\n";
        }
    }

    WSACleanup();
    return exit_code;
}
