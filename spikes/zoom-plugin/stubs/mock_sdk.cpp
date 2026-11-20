#include "ToolSuiteProxyInterface.h"

#include <string>
#include <vector>

namespace {

using namespace ZMToolSuiteProxy;

class StubUserVector final : public IToolSuiteVector<ZMToolSuiteUser> {
public:
    explicit StubUserVector(std::vector<ZMToolSuiteUser> items) : items_(std::move(items)) {}
    int GetCount() override { return static_cast<int>(items_.size()); }
    ZMToolSuiteUser GetItem(int index) override { return items_.at(static_cast<size_t>(index)); }

private:
    std::vector<ZMToolSuiteUser> items_;
};

IZMToolSuiteProxyListener* g_listener = nullptr;
bool g_initialized = false;
bool g_auth_submitted = false;

std::wstring g_topic = L"ZoomCheck Plugin SDK Offline Probe";
std::wstring g_meeting_num = L"81234567890";
std::wstring g_name_host = L"호스트 (진행자)";
std::wstring g_name_s1 = L"01 김민수";
std::wstring g_name_s2 = L"02 이서연";

std::vector<ZMToolSuiteUser> MakeFixtureUsers() {
    ZMToolSuiteUser host;
    host.iUserID = 16778240;
    host.strUserName = g_name_host.c_str();
    host.bHost = true;
    host.bMyself = true;

    ZMToolSuiteUser s1;
    s1.iUserID = 16779264;
    s1.strUserName = g_name_s1.c_str();
    s1.bAttendee = true;

    ZMToolSuiteUser s2;
    s2.iUserID = 16780288;
    s2.strUserName = g_name_s2.c_str();
    s2.bAttendee = true;

    return {host, s1, s2};
}

class StubMeetingToolkit final : public meeting::IMeetingToolkit {
public:
    ZMToolSuiteMeetingStatus GetMeetingStatus() override {
        return g_initialized ? MEETING_STATUS_INMEETING : MEETING_STATUS_IDLE;
    }

    ZMToolSuiteGetMeetingProperty GetMeetingProperty() override {
        ZMToolSuiteGetMeetingProperty prop;
        prop.error_code = kZMToolSuiteProxyErrorsSuccess;
        prop.strMeetingTopic = g_topic.c_str();
        prop.strCurrentMeetingNum = g_meeting_num.c_str();
        prop.eMeetingType = MEETING_TYPE_NORMAL;
        return prop;
    }

    IToolSuiteVector<ZMToolSuiteUser>* GetParticipantsList() override {
        last_list_ = StubUserVector(MakeFixtureUsers());
        return &last_list_;
    }

    IToolSuiteVector<ZMToolSuiteUser>* GetSpotlightedUserList() override { return nullptr; }
    IToolSuiteVector<ZMToolSuiteUser>* GetAttendeeList() override { return nullptr; }

    bool ChangeUserName(unsigned int, const zchar_t*, meeting::onChangeUserNameResult pfn) override {
        if (pfn) {
            BaseResult res;
            res.error_code = kZMToolSuiteProxyErrorsSuccess;
            pfn(res);
        }
        return true;
    }

private:
    StubUserVector last_list_{std::vector<ZMToolSuiteUser>{}};
};

class StubParticipantsToolkit final : public meeting::IParticipantsToolkit {
public:
    ZMToolSuiteUser GetUserByUserID(unsigned int iUserID) override {
        for (const auto& u : MakeFixtureUsers()) {
            if (u.iUserID == iUserID) return u;
        }
        return ZMToolSuiteUser{};
    }

    ZMToolSuiteUser GetMySelf() override {
        return MakeFixtureUsers().front();
    }

    bool GetUserRoleByUserID(unsigned int iUserID, onGetUserRoleByUserIDResult pfn) override {
        if (pfn) {
            GetUserRoleByUserIDResult res;
            res.error_code = kZMToolSuiteProxyErrorsSuccess;
            res.role = (iUserID == 16778240) ? ZMToolSuiteUserRole_Host : ZMToolSuiteUserRole_Attendee;
            pfn(res);
        }
        return true;
    }
};

StubMeetingToolkit g_meeting_toolkit;
StubParticipantsToolkit g_participants_toolkit;

void EmitStubLifecycle() {
    if (!g_listener || !g_auth_submitted) return;
    g_listener->OnAuthResult(AUTH_RESULT_SUCCESS);
    g_listener->OnIPCConnectStatusChanged(IPC_STATUS_CONNECTED, "");
    g_listener->OnMeetingStatusChanged(MEETING_STATUS_INMEETING);

    StubUserVector delta(MakeFixtureUsers());
    ZMToolSuiteUserList list;
    list.error_code = kZMToolSuiteProxyErrorsSuccess;
    list.eUserAction = USER_ACTION_SYNC_ALL;
    list.vUserList = &delta;
    g_listener->OnUserStatusChanged(list, ZMToolSuiteMeetingInstance_Default);
}

} // namespace

extern "C" {

void InitZMToolSuite() {
    g_initialized = true;
}

bool StartToolSuiteAuth(ZMToolSuiteProxy::ZMToolSuiteProxyAuthContext context) {
    if (!g_initialized || !context.accessToken || context.accessToken[0] == L'\0') {
        return false;
    }
    g_auth_submitted = true;
    EmitStubLifecycle();
    return true;
}

void UninitZMToolSuite() {
    g_initialized = false;
    g_auth_submitted = false;
}

} // extern "C"

namespace ZMToolSuiteProxy {

void SetToolSuiteProxyListener(IZMToolSuiteProxyListener* pListener) {
    g_listener = pListener;
    EmitStubLifecycle();
}

namespace meeting {

IMeetingToolkit* GetMeetingToolkit(ZMToolSuiteMeetingInstance) {
    return &g_meeting_toolkit;
}

IParticipantsToolkit* GetParticipantsToolkit(ZMToolSuiteMeetingInstance) {
    return &g_participants_toolkit;
}

} // namespace meeting
} // namespace ZMToolSuiteProxy
