#pragma once
#include <winsock2.h>
#include <ws2tcpip.h>
#include <mswsock.h>
#include <mstcpip.h>
#include <windows.h>
#include <avrt.h>
#include <mmsystem.h>
#include <cstdint>

#ifndef SIO_UDP_CONNRESET
#define SIO_UDP_CONNRESET _WSAIOW(IOC_VENDOR, 12)
#endif

namespace fc {

inline void enableHighResTimer(bool enable) {
    if (enable) timeBeginPeriod(1);
    else timeEndPeriod(1);
}

inline HANDLE boostRealtimeThread(const wchar_t* taskName = L"Pro Audio") {
    DWORD idx = 0;
    HANDLE mm = AvSetMmThreadCharacteristicsW(taskName, &idx);
    if (mm) AvSetMmThreadPriority(mm, AVRT_PRIORITY_HIGH);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    return mm;
}

inline void revertRealtimeThread(HANDLE mm) {
    if (mm) AvRevertMmThreadCharacteristics(mm);
}

// Maximize UDP throughput & minimize ping jitter via OS buffer tuning + DSCP QoS (EF46 Voice / AF41 Video) + Windows qWAVE!
inline void optimizeUdpSocket(SOCKET s, bool isVideo, const sockaddr_in* remote = nullptr) {
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

    // 4. Windows qWAVE QoS Flow (hardware NIC/WMM priority tagging, dynamically loaded for zero extra DLL dependencies)
    if (remote) {
        struct QosVer { USHORT Major; USHORT Minor; };
        using QosCreateFn = BOOL (WINAPI*)(QosVer*, PHANDLE);
        using QosAddFn = BOOL (WINAPI*)(HANDLE, SOCKET, PSOCKADDR, int, DWORD, PULONG);
        static HMODULE hQwave = LoadLibraryW(L"qwave.dll");
        static HANDLE hQos = nullptr;
        static QosAddFn pQosAdd = nullptr;
        if (hQwave && !hQos) {
            auto pCreate = reinterpret_cast<QosCreateFn>(GetProcAddress(hQwave, "QOSCreateHandle"));
            pQosAdd = reinterpret_cast<QosAddFn>(GetProcAddress(hQwave, "QOSAddSocketToFlow"));
            if (pCreate) {
                QosVer ver{ 1, 0 };
                pCreate(&ver, &hQos);
            }
        }
        if (hQos && pQosAdd) {
            ULONG flowId = 0;
            // QOSTrafficTypeVoice = 4, QOSTrafficTypeAudioVideo = 3, QOS_NON_ADAPTIVE_FLOW = 0x00000002
            pQosAdd(hQos, s, const_cast<sockaddr*>(reinterpret_cast<const sockaddr*>(remote)),
                    isVideo ? 3 : 4, 0x00000002, &flowId);
        }
    }
}

} // namespace fc
