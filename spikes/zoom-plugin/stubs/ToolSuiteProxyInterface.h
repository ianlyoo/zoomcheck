#pragma once

// Offline contract stub matching Zoom Plugin SDK for Windows 7.1.0.2020 public API reference.
// Replace by passing -DZOOM_PLUGIN_SDK_DIR=<extracted_x64_dir> to CMake.

#include <functional>
#include <string>
#include <vector>

#if defined(ZOOM_PLUGIN_SDK_STUB)
#define ZOOM_PROXY_SDK_EXPORT
#elif defined(WIN32)
#if defined(ZOOM_PROXY_SDK_IMPLEMENTATION)
#define ZOOM_PROXY_SDK_EXPORT __declspec(dllexport)
#else
#define ZOOM_PROXY_SDK_EXPORT __declspec(dllimport)
#endif
#else
#define ZOOM_PROXY_SDK_EXPORT __attribute__((visibility("default")))
#endif

#if defined(WIN32)
typedef wchar_t zchar_t;
#else
#include <stdint.h>
typedef char zchar_t;
#endif

namespace ZMToolSuiteProxy {

template <class T>
class IToolSuiteVector {
public:
    virtual ~IToolSuiteVector() {}
    virtual int GetCount() = 0;
    virtual T GetItem(int index) = 0;
};

struct ZMToolSuiteProxyAuthContext {
    const zchar_t* accessToken = nullptr;
    const zchar_t* domain = nullptr;
};

enum ZMToolSuiteProxyAuthResult {
    AUTH_RESULT_SUCCESS,
    AUTH_RESULT_REQUEST_ERROR,
    AUTH_RESULT_VERIFY_USER_FAILED,
    AUTH_RESULT_FAILED
};

enum ZMToolSuiteProxyErrors {
    kZMToolSuiteProxyErrorsUnknown,
    kZMToolSuiteProxyErrorsSuccess,
    kZMToolSuiteProxyErrorsNotImplemented,
    kZMToolSuiteProxyErrorsWrongUsage,
    kZMToolSuiteProxyErrorsInvalidArgument,
    kZMToolSuiteProxyErrorsInternalError,
    kZMToolSuiteProxyErrorsNoPermission,
    kZMToolSuiteProxyErrorsRateLimitReached,
    kZMToolSuiteProxyErrorsOperationTimeout,
    kZMToolSuiteProxyErrorsVersionIncompatible
};

struct BaseResult {
    ZMToolSuiteProxyErrors error_code = kZMToolSuiteProxyErrorsUnknown;
};

enum ZMToolSuiteIPCConnectStatus {
    IPC_STATUS_IDLE,
    IPC_STATUS_CONNECT_TIMEOUT,
    IPC_STATUS_CONNECTED,
    IPC_STATUS_DISCONNECTED
};

enum ZMToolSuiteAudioType {
    ZMToolSuiteAudioType_None,
    ZMToolSuiteAudioType_Voip,
    ZMToolSuiteAudioType_Telephony
};

enum ZMToolSuiteUserRole {
    ZMToolSuiteUserRole_None,
    ZMToolSuiteUserRole_Host,
    ZMToolSuiteUserRole_CoHost,
    ZMToolSuiteUserRole_Attendee,
    ZMToolSuiteUserRole_Panelist,
    ZMToolSuiteUserRole_BreakoutRoomModerator
};

struct ZMToolSuiteUser {
    bool bHost = false;
    bool bVideoOn = false;
    unsigned int iUserID = 0;
    const zchar_t* strUserName = nullptr;
    bool bMyself = false;
    bool bSpotlighted = false;
    bool bAttendee = false;
    bool bRaisedHand = false;
    bool bAudioMuted = false;
    bool bTalking = false;
    bool bInWaitingRoom = false;
    ZMToolSuiteAudioType audioType = ZMToolSuiteAudioType_None;
};

enum ZMToolSuiteUserAction {
    USER_ACTION_NO_ACTION,
    USER_ACTION_SYNC_ALL,
    USER_ACTION_ADD_USER,
    USER_ACTION_LEFT_USER,
    USER_ACTION_UPDATE_USER
};

struct ZMToolSuiteUserList : public BaseResult {
    ZMToolSuiteUserAction eUserAction = USER_ACTION_NO_ACTION;
    IToolSuiteVector<ZMToolSuiteUser>* vUserList = {};
};

enum ZMToolSuiteProxySharePlayError {
    ZMToolSuiteProxySharePlayError_No_Error,
    ZMToolSuiteProxySharePlayError_Not_Support,
    ZMToolSuiteProxySharePlayError_Resolution_High,
    ZMToolSuiteProxySharePlayError_Open_Fail,
    ZMToolSuiteProxySharePlayError_Play_Fail,
    ZMToolSuiteProxySharePlayError_Seek_Fail
};

enum ZMToolSuiteProxyShareContentType {
    ZMToolSuiteProxyShareContentType_None,
    ZMToolSuiteProxyShareContentType_AS,
    ZMToolSuiteProxyShareContentType_DS,
    ZMToolSuiteProxyShareContentType_AUDIO,
    ZMToolSuiteProxyShareContentType_FRAME,
    ZMToolSuiteProxyShareContentType_VIDEO_FILE,
    ZMToolSuiteProxyShareContentType_CAMERA
};

enum ZMToolSuiteMeetingStatus {
    MEETING_STATUS_IDLE,
    MEETING_STATUS_CONNECTING,
    MEETING_STATUS_WAITING_FOR_HOST,
    MEETING_STATUS_INMEETING,
    MEETING_STATUS_DISCONNECTING,
    MEETING_STATUS_RECONNECTING,
    MEETING_STATUS_FAILED,
    MEETING_STATUS_ENDED,
    MEETING_STATUS_LOCKED,
    MEETING_STATUS_UNLOCKED,
    MEETING_STATUS_IN_WAITING_ROOM,
    MEETING_STATUS_WEBINAR_PROMOTE,
    MEETING_STATUS_WEBINAR_DEPROMOTE,
    MEETING_STATUS_BEFORE_SUBCONF_CREATE,
    MEETING_STATUS_BEFORE_SUBCONF_DESTROY,
    MEETING_STATUS_SUBCONF_CREATED,
    MEETING_STATUS_SUBCONF_DESTROYED
};

enum ZMToolSuiteMeetingType {
    MEETING_TYPE_NONE,
    MEETING_TYPE_NORMAL,
    MEETING_TYPE_WEBINAR,
    MEETING_TYPE_WEBINARBO,
    MEETING_TYPE_BO,
    MEETING_TYPE_GREENROOM
};

enum ZMToolSuiteMeetingInstance {
    ZMToolSuiteMeetingInstance_None,
    ZMToolSuiteMeetingInstance_Default,
    ZMToolSuiteMeetingInstance_GreenRoom,
    ZMToolSuiteMeetingInstance_NewBO
};

struct ZMToolSuiteGetMeetingProperty : public BaseResult {
    const zchar_t* strMeetingTopic = nullptr;
    const zchar_t* strCurrentMeetingNum = nullptr;
    const zchar_t* strMeetingPassword = nullptr;
    const zchar_t* strMeetingJoinURL = nullptr;
    ZMToolSuiteMeetingType eMeetingType = MEETING_TYPE_NONE;
};

struct GetUserRoleByUserIDResult : public BaseResult {
    ZMToolSuiteUserRole role = ZMToolSuiteUserRole_None;
};

enum IPCEmojiReactionType {
    IPCEmojiReactionType_Unknow
};

enum IPCEmojiReactionSkinTone {
    IPCEmojiReactionSkinTone_Unknow
};

enum IPCFeedbackReactionType {
    IPCFeedbackReactionType_None
};

struct SendEmojiReactionParam {
    IPCEmojiReactionType type = IPCEmojiReactionType_Unknow;
    IPCEmojiReactionSkinTone skinTone = IPCEmojiReactionSkinTone_Unknow;
};

enum IPCReactionCategory {
    IPCReactionCategory_Unknown = 0,
    IPCReactionCategory_Normal = 1,
    IPCReactionCategory_Custom = 2
};

struct WebinarEmojiContent {
    std::string emojiKey;
    unsigned int count = 0;
    IPCReactionCategory reactionCategory = IPCReactionCategory_Unknown;
};

enum IPCMeetingViewType {
    IPCMeetingViewType_ActiveSpeaker,
    IPCMeetingViewType_Wall,
    IPCMeetingViewType_MultiSpeaker,
    IPCMeetingViewType_Other
};

enum ZMToolSuiteShareStatus {
    ZMToolSuiteShareStatus_Started,
    ZMToolSuiteShareStatus_Ended
};

enum ZMToolSuitMonitorType {
    ZMToolSuitMonitorType_First,
    ZMToolSuitMonitorType_Dual
};

struct ZMToolSuiteShareOptions {
    bool bShareSound = false;
    bool bOptimizeVideo = false;
};

typedef std::function<void(const GetUserRoleByUserIDResult&)> onGetUserRoleByUserIDResult;

class IZMToolSuiteProxyListener {
public:
    virtual ~IZMToolSuiteProxyListener() = default;
    virtual void OnAuthResult(ZMToolSuiteProxyAuthResult eResult) = 0;
    virtual void OnIPCConnectStatusChanged(ZMToolSuiteIPCConnectStatus eStatus, std::string errorMessage) = 0;
    virtual void OnUserStatusChanged(ZMToolSuiteUserList vUserList, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnMeetingStatusChanged(ZMToolSuiteProxy::ZMToolSuiteMeetingStatus status) = 0;
    virtual void OnMeetingShareOptionsChanged(ZMToolSuiteShareOptions& options) = 0;
    virtual void OnUserRaisedHand(ZMToolSuiteMeetingInstance eInstance, unsigned int iUserID) = 0;
    virtual void OnUserLowerHand(ZMToolSuiteMeetingInstance eInstance, unsigned int iUserID) = 0;
    virtual void OnRecordingStatusChanged(ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnEmojiReactionReceived(unsigned int sender_id, SendEmojiReactionParam type, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnEmojiReactionReceivedInWebinar(IToolSuiteVector<WebinarEmojiContent>* emojiContents, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnEmojiFeedbackReceived(unsigned int sender_id, IPCFeedbackReactionType type, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnAllHandsLowered(ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnGroupLayoutUpdate(ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnMyVideoStatusChange(bool bVideoOn, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnStartShareAudio(ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnStopShareAudio(ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnStartViewAudio(unsigned int share_user_id, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnStopViewAudio(unsigned int share_user_id, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnShareSendStatusChanged(unsigned int nShareSourceUserID, bool bPaused, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnStartSendShare(ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnStopSendShare(ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnShareContentTypeChanged(ZMToolSuiteProxyShareContentType status, unsigned int userId, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnShareStatusChanged(unsigned int share_user_id, ZMToolSuiteShareStatus status, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnSharedVideoEnded(ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnVideoFileSharePlayError(ZMToolSuiteProxySharePlayError error, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnMyAudioStatusChanged(bool bMuted, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnOriginalSoundStatusChange(bool bOn, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnUnreadChatCountChange(unsigned int count, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnChatWindowStatusChange(bool bOn, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnMeetingViewTypeChange(IPCMeetingViewType type, ZMToolSuiteMeetingInstance eInstance) = 0;
    virtual void OnFullScreenStatusChange(bool bFullScreen, ZMToolSuitMonitorType monitorType, ZMToolSuiteMeetingInstance eInstance) = 0;
};

namespace meeting {

typedef std::function<void(const BaseResult&)> onChangeUserNameResult;

class IMeetingToolkit {
public:
    virtual ~IMeetingToolkit() = default;
    virtual ZMToolSuiteMeetingStatus GetMeetingStatus() = 0;
    virtual ZMToolSuiteGetMeetingProperty GetMeetingProperty() = 0;
    virtual IToolSuiteVector<ZMToolSuiteUser>* GetParticipantsList() = 0;
    virtual IToolSuiteVector<ZMToolSuiteUser>* GetSpotlightedUserList() = 0;
    virtual IToolSuiteVector<ZMToolSuiteUser>* GetAttendeeList() = 0;
    virtual bool ChangeUserName(unsigned int iUserID, const zchar_t* strUserName, onChangeUserNameResult pfn) = 0;
};

class IParticipantsToolkit {
public:
    virtual ~IParticipantsToolkit() = default;
    virtual ZMToolSuiteUser GetUserByUserID(unsigned int iUserID) = 0;
    virtual ZMToolSuiteUser GetMySelf() = 0;
    virtual bool GetUserRoleByUserID(unsigned int iUserID, onGetUserRoleByUserIDResult pfn) = 0;
};

ZOOM_PROXY_SDK_EXPORT IMeetingToolkit* GetMeetingToolkit(ZMToolSuiteMeetingInstance type = ZMToolSuiteMeetingInstance_Default);
ZOOM_PROXY_SDK_EXPORT IParticipantsToolkit* GetParticipantsToolkit(ZMToolSuiteMeetingInstance type = ZMToolSuiteMeetingInstance_Default);

} // namespace meeting

ZOOM_PROXY_SDK_EXPORT void SetToolSuiteProxyListener(IZMToolSuiteProxyListener* pListener);

} // namespace ZMToolSuiteProxy

extern "C" {
ZOOM_PROXY_SDK_EXPORT void InitZMToolSuite();
ZOOM_PROXY_SDK_EXPORT bool StartToolSuiteAuth(ZMToolSuiteProxy::ZMToolSuiteProxyAuthContext context);
ZOOM_PROXY_SDK_EXPORT void UninitZMToolSuite();
}
