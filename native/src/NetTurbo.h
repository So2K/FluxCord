#pragma once
#include <winsock2.h>
#include <ws2tcpip.h>
#include <mswsock.h>
#include <mstcpip.h>
#include <windows.h>
#include <avrt.h>
#include <mmsystem.h>
#include <atomic>
#include <cstdint>
#include <mutex>

#ifndef SIO_UDP_CONNRESET
#define SIO_UDP_CONNRESET _WSAIOW(IOC_VENDOR, 12)
#endif

namespace fc {

inline void enableHighResTimer(bool enable) {
    static std::atomic<bool> g_timerActive{false};
    if (enable) {
        if (!g_timerActive.exchange(true)) timeBeginPeriod(1);
    } else {
        if (g_timerActive.exchange(false)) timeEndPeriod(1);
    }
}

inline HANDLE boostRealtimeThread(const wchar_t* taskName = L"Pro Audio") {
    DWORD idx = 0;
    HANDLE mm = AvSetMmThreadCharacteristicsW(taskName, &idx);
    if (mm) {
        AvSetMmThreadPriority(mm, AVRT_PRIORITY_HIGH);
    } else {
        // Only fall back to manual thread priority when MMCSS is unavailable (avoids MMCSS priority conflict)
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    }
    return mm;
}

inline void revertRealtimeThread(HANDLE mm) {
    if (mm) {
        AvRevertMmThreadCharacteristics(mm);
    } else {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_NORMAL);
    }
}

namespace detail {
struct QosVer { USHORT Major; USHORT Minor; };
using QosCreateFn = BOOL (WINAPI*)(QosVer*, PHANDLE);
using QosAddFn = BOOL (WINAPI*)(HANDLE, SOCKET, PSOCKADDR, int, DWORD, PULONG);
using QosRemoveFn = BOOL (WINAPI*)(HANDLE, SOCKET, ULONG, DWORD);

struct QwaveApi {
    HANDLE hQos = nullptr;
    QosAddFn pAdd = nullptr;
    QosRemoveFn pRemove = nullptr;
};

inline QwaveApi& getQwave() {
    static std::once_flag once;
    static QwaveApi api{};
    std::call_once(once, []() {
        HMODULE hMod = LoadLibraryExW(L"qwave.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!hMod) return;
        auto pCreate = reinterpret_cast<QosCreateFn>(GetProcAddress(hMod, "QOSCreateHandle"));
        api.pAdd = reinterpret_cast<QosAddFn>(GetProcAddress(hMod, "QOSAddSocketToFlow"));
        api.pRemove = reinterpret_cast<QosRemoveFn>(GetProcAddress(hMod, "QOSRemoveSocketFromFlow"));
        if (pCreate) {
            QosVer ver{ 1, 0 };
            pCreate(&ver, &api.hQos);
        }
    });
    return api;
}
} // namespace detail

inline void removeUdpQosFlow(SOCKET s, ULONG& flowId) {
    if (s == INVALID_SOCKET || flowId == 0) return;
    auto& q = detail::getQwave();
    if (q.hQos && q.pRemove) {
        q.pRemove(q.hQos, s, flowId, 0);
    }
    flowId = 0;
}

// Maximize UDP throughput & minimize ping jitter via OS buffer tuning + DSCP QoS (EF46 Voice / AF41 Video) + Windows qWAVE!
inline void optimizeUdpSocket(SOCKET s, bool isVideo, const sockaddr_in* remote = nullptr, ULONG* inOutFlowId = nullptr) {
    if (s == INVALID_SOCKET) return;

    // 1. Disable Windows SIO_UDP_CONNRESET (prevents ICMP Port Unreachable from stalling recvfrom!)
    BOOL newBehavior = FALSE;
    DWORD bytesRet = 0;
    WSAIoctl(s, SIO_UDP_CONNRESET, &newBehavior, sizeof(newBehavior), nullptr, 0, &bytesRet, nullptr, nullptr);

    // 2. Large kernel socket buffers -> ZERO drops on 4K60/120fps IDR bursts!
    int sndBuf = isVideo ? (4 * 1024 * 1024) : (2 * 1024 * 1024);
    int rcvBuf = isVideo ? (8 * 1024 * 1024) : (4 * 1024 * 1024);
    setsockopt(s, SOL_SOCKET, SO_SNDBUF, reinterpret_cast<const char*>(&sndBuf), sizeof(sndBuf));
    setsockopt(s, SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char*>(&rcvBuf), sizeof(rcvBuf));

    // 3. IP DSCP Traffic Class: EF (46 -> 0xB8) for Voice, AF41 (34 -> 0x88) for Interactive Video
    int tos = isVideo ? 0x88 : 0xB8;
    setsockopt(s, IPPROTO_IP, IP_TOS, reinterpret_cast<const char*>(&tos), sizeof(tos));

    // 4. Windows qWAVE QoS Flow (thread-safe init + clean flow replacement so Voice->Video upgrade works without leaking!)
    if (remote) {
        auto& q = detail::getQwave();
        if (q.hQos && q.pAdd) {
            if (inOutFlowId && *inOutFlowId != 0 && q.pRemove) {
                q.pRemove(q.hQos, s, *inOutFlowId, 0);
                *inOutFlowId = 0;
            }
            ULONG flowId = 0;
            // QOSTrafficTypeVoice = 4, QOSTrafficTypeAudioVideo = 3, QOS_NON_ADAPTIVE_FLOW = 0x00000002
            if (q.pAdd(q.hQos, s, const_cast<sockaddr*>(reinterpret_cast<const sockaddr*>(remote)),
                       isVideo ? 3 : 4, 0x00000002, &flowId)) {
                if (inOutFlowId) *inOutFlowId = flowId;
            }
        }
    }
}

} // namespace fc
