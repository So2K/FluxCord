#include "StreamReceiver.h"
#include "NetTurbo.h"
#include "VideoDepacketizer.h"
#include <initguid.h>
#include <codecapi.h>
#include <sodium.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mftransform.h>
#include <wmcodecdsp.h>
#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#include <cstring>

using Microsoft::WRL::ComPtr;

namespace fc {

namespace {
inline void putBE16(uint8_t* p, uint16_t v) { p[0] = uint8_t(v >> 8); p[1] = uint8_t(v & 0xFF); }
inline void putBE32(uint8_t* p, uint32_t v) {
    p[0] = uint8_t(v >> 24); p[1] = uint8_t((v >> 16) & 0xFF);
    p[2] = uint8_t((v >> 8) & 0xFF); p[3] = uint8_t(v & 0xFF);
}
inline uint16_t getBE16(const uint8_t* p) { return uint16_t((uint16_t(p[0]) << 8) | p[1]); }
inline uint32_t getBE32(const uint8_t* p) {
    return (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) | (uint32_t(p[2]) << 8) | p[3];
}
int64_t steadyMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}
} // namespace

StreamReceiver::StreamReceiver(VoiceEngine* voice) : voice_(voice) {
    WSADATA wsa{};
    WSAStartup(MAKEWORD(2, 2), &wsa);
    sodium_init();
}

StreamReceiver::~StreamReceiver() {
    stop();
}

void StreamReceiver::daveDestroyLocked() {
    if (dave_dec_) { daveDecryptorDestroy(dave_dec_); dave_dec_ = nullptr; }
    if (dave_session_) { daveSessionDestroy(dave_session_); dave_session_ = nullptr; }
    dave_proto_ver_ = 0;
}

bool StreamReceiver::daveInit(uint16_t proto_ver, uint64_t channel_id, const char* self_user_id, uint32_t self_ssrc) {
    local_ssrc_ = self_ssrc;
    std::lock_guard lk(dave_mtx_);
    dave_proto_ver_ = proto_ver;
    if (!dave_session_) dave_session_ = daveSessionCreate(nullptr, nullptr, nullptr, nullptr);
    if (!dave_dec_) dave_dec_ = daveDecryptorCreate();
    if (!dave_session_ || !dave_dec_) return false;
    if (proto_ver > 0) {
        daveSessionInit(dave_session_, proto_ver, channel_id, self_user_id ? self_user_id : "");
        daveDecryptorTransitionToPassthroughMode(dave_dec_, false);
    } else {
        daveSessionReset(dave_session_);
        daveDecryptorTransitionToPassthroughMode(dave_dec_, true);
    }
    return true;
}

void StreamReceiver::daveAddUser(const char* user_id) {
    if (!user_id || !*user_id) return;
    std::lock_guard lk(dave_mtx_);
    if (!dave_session_) dave_session_ = daveSessionCreate(nullptr, nullptr, nullptr, nullptr);
    if (dave_session_) daveSessionAddRecognizedUser(dave_session_, user_id);
}

int32_t StreamReceiver::daveGetKeyPackage(uint8_t* out_buf, int32_t cap) {
    std::lock_guard lk(dave_mtx_);
    if (!dave_session_ || !out_buf || cap <= 0) return 0;
    uint8_t* kp = nullptr;
    size_t len = 0;
    daveSessionGetMarshalledKeyPackage(dave_session_, &kp, &len);
    if (!kp || len == 0) return 0;
    int32_t n = int32_t(std::min<size_t>(len, size_t(cap)));
    std::memcpy(out_buf, kp, size_t(n));
    daveFree(kp);
    return n;
}

void StreamReceiver::daveSetExternalSender(const uint8_t* data, int32_t len) {
    std::lock_guard lk(dave_mtx_);
    if (dave_session_ && data && len > 0) daveSessionSetExternalSender(dave_session_, data, size_t(len));
}

int32_t StreamReceiver::daveProcessProposals(const uint8_t* data, int32_t len, uint8_t* out_cw, int32_t cap) {
    std::lock_guard lk(dave_mtx_);
    if (!dave_session_ || !data || len <= 0) return 0;
    uint8_t* cw = nullptr;
    size_t cwLen = 0;
    daveSessionProcessProposals(dave_session_, data, size_t(len), &cw, &cwLen);
    if (!cw || cwLen == 0) return 0;
    int32_t n = (out_cw && cap > 0) ? int32_t(std::min<size_t>(cwLen, size_t(cap))) : 0;
    if (n > 0) std::memcpy(out_cw, cw, size_t(n));
    daveFree(cw);
    return n;
}

bool StreamReceiver::daveProcessCommit(const uint8_t* data, int32_t len) {
    std::lock_guard lk(dave_mtx_);
    if (!dave_session_ || !data || len <= 0) return false;
    DAVECommitResultHandle res = daveSessionProcessCommit(dave_session_, data, size_t(len));
    if (!res) return false;
    bool ok = !daveCommitResultIsFailed(res);
    daveCommitResultDestroy(res);
    return ok;
}

bool StreamReceiver::daveProcessWelcome(const uint8_t* data, int32_t len) {
    std::lock_guard lk(dave_mtx_);
    if (!dave_session_ || !data || len <= 0) return false;
    DAVEWelcomeResultHandle res = daveSessionProcessWelcome(dave_session_, data, size_t(len));
    if (!res) return false;
    daveWelcomeResultDestroy(res);
    return true;
}

void StreamReceiver::daveExecuteTransition(uint16_t proto_ver) {
    std::lock_guard lk(dave_mtx_);
    dave_proto_ver_ = proto_ver;
    if (!dave_dec_) dave_dec_ = daveDecryptorCreate();
    if (!dave_dec_) return;
    if (proto_ver == 0) {
        daveDecryptorTransitionToPassthroughMode(dave_dec_, true);
        return;
    }
    if (dave_session_ && !streamer_uid_.empty()) {
        DAVEKeyRatchetHandle kr = daveSessionGetKeyRatchet(dave_session_, streamer_uid_.c_str());
        if (kr) {
            daveDecryptorTransitionToPassthroughMode(dave_dec_, false);
            daveDecryptorTransitionToKeyRatchet(dave_dec_, kr);
            daveKeyRatchetDestroy(kr);
        }
    }
}

void StreamReceiver::setRemoteStreams(uint32_t video_ssrc, uint32_t audio_ssrc, const char* streamer_user_id) {
    if (video_ssrc != 0) video_ssrc_.store(video_ssrc);
    if (audio_ssrc != 0) {
        audio_ssrc_.store(audio_ssrc);
        if (voice_) voice_->setUserVolume(audio_ssrc ^ 0x80000000u, stream_vol_.load());
    }
    std::lock_guard lk(dave_mtx_);
    if (streamer_user_id && *streamer_user_id) {
        streamer_uid_ = streamer_user_id;
        if (dave_session_) daveSessionAddRecognizedUser(dave_session_, streamer_user_id);
        if (dave_session_ && dave_dec_ && dave_proto_ver_ > 0) {
            DAVEKeyRatchetHandle kr = daveSessionGetKeyRatchet(dave_session_, streamer_user_id);
            if (kr) {
                daveDecryptorTransitionToPassthroughMode(dave_dec_, false);
                daveDecryptorTransitionToKeyRatchet(dave_dec_, kr);
                daveKeyRatchetDestroy(kr);
            }
        }
    }
}

void StreamReceiver::setVolume(float vol) {
    vol = std::clamp(vol, 0.0f, 2.0f);
    stream_vol_.store(vol);
    uint32_t as = audio_ssrc_.load();
    if (voice_ && as != 0) voice_->setUserVolume(as ^ 0x80000000u, vol);
}

bool StreamReceiver::connectAndDiscover(const char* ip, uint16_t port, uint32_t ssrc, HWND hwnd, std::string& out_ext_ip, uint16_t& out_ext_port) {
    stop();
    hwnd_ = hwnd;
    local_ssrc_ = ssrc;
    rtcp_nonce_ = 1;
    sock_ = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock_ == INVALID_SOCKET) return false;

    int rcvBuf = 8 * 1024 * 1024;
    setsockopt(sock_, SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char*>(&rcvBuf), sizeof(rcvBuf));
    DWORD timeoutMs = 1500;
    setsockopt(sock_, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeoutMs), sizeof(timeoutMs));

    std::memset(&remote_, 0, sizeof(remote_));
    remote_.sin_family = AF_INET;
    remote_.sin_port = htons(port);
    if (inet_pton(AF_INET, ip, &remote_.sin_addr) != 1) {
        stop();
        return false;
    }

    uint8_t req[74]{};
    putBE16(req + 0, 0x0001);
    putBE16(req + 2, 70);
    putBE32(req + 4, ssrc);
    if (sendto(sock_, reinterpret_cast<const char*>(req), sizeof(req), 0,
               reinterpret_cast<sockaddr*>(&remote_), sizeof(remote_)) != sizeof(req)) {
        stop();
        return false;
    }

    uint8_t resp[128]{};
    sockaddr_in from{};
    int fromLen = sizeof(from);
    int n = recvfrom(sock_, reinterpret_cast<char*>(resp), sizeof(resp), 0,
                     reinterpret_cast<sockaddr*>(&from), &fromLen);
    if (n < 74 || getBE16(resp) != 0x0002) {
        stop();
        return false;
    }
    char ipBuf[65]{};
    std::memcpy(ipBuf, resp + 8, 64);
    out_ext_ip = ipBuf;
    out_ext_port = getBE16(resp + 72);

    DWORD rxTimeout = 4;
    setsockopt(sock_, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&rxTimeout), sizeof(rxTimeout));
    optimizeUdpSocket(sock_, true, &remote_);

    {
        std::lock_guard lk(stats_mtx_);
        std::memset(&stats_, 0, sizeof(stats_));
        stats_.running = 1;
        stats_.zero_copy = 1;
        std::strncpy(stats_.encoder_name, "DXVA / NVDEC Hardware H.264", sizeof(stats_.encoder_name) - 1);
    }

    running_.store(true);
    worker_ = std::thread([this] { rxDecodeLoop(); });
    return true;
}

void StreamReceiver::setKey(const uint8_t key[32], bool use_xchacha_rtpsize) {
    std::memcpy(key_, key, 32);
    use_xchacha_ = use_xchacha_rtpsize;
    has_key_.store(true);
}

void StreamReceiver::resize(int32_t w, int32_t h) {
    if (w > 16 && h > 16) {
        pending_w_.store(w);
        pending_h_.store(h);
    }
}

void StreamReceiver::stop() {
    running_.store(false);
    has_key_.store(false);
    video_ssrc_.store(0);
    audio_ssrc_.store(0);
    if (sock_ != INVALID_SOCKET) {
        closesocket(sock_);
        sock_ = INVALID_SOCKET;
    }
    if (worker_.joinable()) worker_.join();
    {
        std::lock_guard lk(dave_mtx_);
        daveDestroyLocked();
    }
    std::lock_guard lk(stats_mtx_);
    stats_.running = 0;
}

void StreamReceiver::getStats(FcScreenStats* out) const {
    if (!out) return;
    std::lock_guard lk(stats_mtx_);
    *out = stats_;
}

void StreamReceiver::rxDecodeLoop() {
    HANDLE mm = boostRealtimeThread(L"Games");
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    MFStartup(MF_VERSION);

    { // Inner COM scope so ALL D3D11 / MF objects release cleanly BEFORE MFShutdown!
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    D3D_FEATURE_LEVEL fl{};
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0, D3D11_SDK_VERSION, &dev, &fl, &ctx);
    ComPtr<ID3D11Multithread> mt;
    if (dev && SUCCEEDED(dev.As(&mt))) mt->SetMultithreadProtected(TRUE);

    UINT resetToken = 0;
    ComPtr<IMFDXGIDeviceManager> dxgiMgr;
    if (dev) {
        MFCreateDXGIDeviceManager(&resetToken, &dxgiMgr);
        dxgiMgr->ResetDevice(dev.Get(), resetToken);
    }

    ComPtr<IDXGIFactory2> fac;
    CreateDXGIFactory1(IID_PPV_ARGS(&fac));
    ComPtr<IDXGISwapChain1> swap;
    UINT sw = 1280, sh = 720;
    if (dev && fac && hwnd_ && IsWindow(hwnd_)) {
        RECT rc{};
        GetClientRect(hwnd_, &rc);
        sw = std::max<UINT>(16, UINT(rc.right - rc.left));
        sh = std::max<UINT>(16, UINT(rc.bottom - rc.top));
        DXGI_SWAP_CHAIN_DESC1 scd{};
        scd.Width = sw; scd.Height = sh;
        scd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        scd.SampleDesc.Count = 1;
        scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        scd.BufferCount = 2;
        scd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        scd.Scaling = DXGI_SCALING_STRETCH;
        scd.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        if (SUCCEEDED(fac->CreateSwapChainForHwnd(dev.Get(), hwnd_, &scd, nullptr, nullptr, &swap)) && swap && ctx) {
            // Present an immediate opaque black frame so DWM NEVER shows a transparent hole behind the window!
            ComPtr<ID3D11Texture2D> bb;
            ComPtr<ID3D11RenderTargetView> rtv;
            if (SUCCEEDED(swap->GetBuffer(0, IID_PPV_ARGS(&bb))) &&
                SUCCEEDED(dev->CreateRenderTargetView(bb.Get(), nullptr, &rtv))) {
                const float blk[4] = { 0.0f, 0.0f, 0.0f, 1.0f };
                ctx->ClearRenderTargetView(rtv.Get(), blk);
                swap->Present(0, 0);
            }
        }
    }

    ComPtr<IMFTransform> decMft;
    if (SUCCEEDED(CoCreateInstance(CLSID_CMSH264DecoderMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&decMft)))) {
        if (dxgiMgr) decMft->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(dxgiMgr.Get()));
        ComPtr<IMFAttributes> attrs;
        if (SUCCEEDED(decMft->GetAttributes(&attrs))) attrs->SetUINT32(CODECAPI_AVLowLatencyMode, TRUE);

        ComPtr<IMFMediaType> mtIn;
        MFCreateMediaType(&mtIn);
        mtIn->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        mtIn->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
        decMft->SetInputType(0, mtIn.Get(), 0);

        for (DWORD i = 0; ; ++i) {
            ComPtr<IMFMediaType> mtOut;
            if (FAILED(decMft->GetOutputAvailableType(0, i, &mtOut))) break;
            GUID sub{};
            if (SUCCEEDED(mtOut->GetGUID(MF_MT_SUBTYPE, &sub)) && sub == MFVideoFormat_NV12) {
                decMft->SetOutputType(0, mtOut.Get(), 0);
                break;
            }
        }
        decMft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        decMft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    }

    ComPtr<ID3D11VideoDevice> vdev;
    ComPtr<ID3D11VideoContext> vctx;
    ComPtr<ID3D11VideoProcessorEnumerator> vpenum;
    ComPtr<ID3D11VideoProcessor> vp;
    if (dev) { dev.As(&vdev); ctx.As(&vctx); }
    UINT curDecW = 0, curDecH = 0;

    VideoDepacketizer depack;
    VideoDepacketizer::FrameOut readyFrame;

    uint8_t pkt[2048];
    uint8_t plain[2048];
    std::vector<uint8_t> daveBuf(2 * 1024 * 1024);
    std::vector<uint8_t> cachedSpsPps;
    std::vector<uint8_t> prependBuf;

    int64_t framesDecoded = 0;
    int64_t framesWindow = 0;
    int64_t bytesWindow = 0;
    int64_t rxUdpPkts = 0;
    int64_t depackFrames = 0;
    int64_t daveOk = 0;
    int64_t daveFail = 0;
    int64_t kfSkipped = 0;
    int64_t mftFed = 0;
    uint8_t lastVideoPt = 0;
    uint8_t lastNaluType = 0;
    int64_t lastRatchetRetryMs = 0;
    auto windowStart = std::chrono::steady_clock::now();
    int64_t lastPliMs = 0;
    bool sentKeepalive = false;

    auto updateDiagText = [&]() {
        char diag[64]{};
        std::snprintf(diag, sizeof(diag), "rx=%lld dv=%lld/%lld kf=%lld in=%lld dec=%lld nt=%u",
                      (long long)rxUdpPkts, (long long)daveOk, (long long)daveFail,
                      (long long)kfSkipped, (long long)mftFed, (long long)framesDecoded, unsigned(lastNaluType));
        std::lock_guard lk(stats_mtx_);
        std::strncpy(stats_.encoder_name, diag, sizeof(stats_.encoder_name) - 1);
    };

    auto cacheSpsPpsFromFrame = [&](const uint8_t* data, size_t len) {
        std::vector<uint8_t> found;
        size_t pos = 0;
        while (pos + 4 <= len) {
            size_t sc = 0;
            if (data[pos] == 0 && data[pos + 1] == 0 && data[pos + 2] == 1) sc = 3;
            else if (data[pos] == 0 && data[pos + 1] == 0 && data[pos + 2] == 0 && data[pos + 3] == 1) sc = 4;
            if (sc == 0) { pos++; continue; }
            size_t start = pos + sc;
            size_t next = start;
            while (next + 3 < len) {
                if (data[next] == 0 && data[next + 1] == 0 &&
                    (data[next + 2] == 1 || (next + 3 < len && data[next + 2] == 0 && data[next + 3] == 1))) break;
                next++;
            }
            if (next + 3 >= len) next = len;
            if (start < next) {
                uint8_t nt = data[start] & 0x1F;
                if (nt == 7 || nt == 8) {
                    found.push_back(0); found.push_back(0); found.push_back(0); found.push_back(1);
                    found.insert(found.end(), data + start, data + next);
                }
            }
            pos = next;
        }
        if (!found.empty()) cachedSpsPps = std::move(found);
    };

    auto frameHasSps = [](const uint8_t* data, size_t len) -> bool {
        for (size_t i = 0; i + 4 < len; ++i) {
            if (data[i] == 0 && data[i + 1] == 0) {
                size_t sc = (data[i + 2] == 1) ? 3 : ((data[i + 2] == 0 && data[i + 3] == 1) ? 4 : 0);
                if (sc > 0 && i + sc < len && ((data[i + sc] & 0x1F) == 7)) return true;
            }
        }
        return false;
    };

    // Send ENCRYPTED RTCP PLI per Discord aead_xchacha20_poly1305_rtpsize spec (8B RTCP header AAD + encrypted payload + 16B tag + 4B LE nonce)
    auto sendEncryptedRtcpPli = [&](uint32_t mediaSsrc) {
        if (sock_ == INVALID_SOCKET || !has_key_.load() || mediaSsrc == 0) return;
        uint8_t outPkt[64]{};
        // 8-byte unencrypted RTCP header (AAD)
        outPkt[0] = 0x81; // V=2, P=0, FMT=1 (PLI)
        outPkt[1] = 206;  // PT=PSFB
        putBE16(outPkt + 2, 2);
        putBE32(outPkt + 4, local_ssrc_);

        // 4-byte RTCP PLI body (Media SSRC) -> encrypted!
        uint8_t body[4];
        putBE32(body, mediaSsrc);

        uint32_t ctr = rtcp_nonce_++;
        uint8_t nonce[24]{};
        std::memcpy(nonce, &ctr, 4);
        int totalLen = 0;
        if (use_xchacha_) {
            unsigned long long clen = 0;
            if (crypto_aead_xchacha20poly1305_ietf_encrypt(
                    outPkt + 8, &clen, body, 4, outPkt, 8, nullptr, nonce, key_) != 0) return;
            std::memcpy(outPkt + 8 + clen, &ctr, 4);
            totalLen = int(8 + clen + 4);
        } else {
            if (crypto_secretbox_easy(outPkt + 8, body, 4, nonce, key_) != 0) return;
            std::memcpy(outPkt + 8 + 4 + crypto_secretbox_MACBYTES, &ctr, 4);
            totalLen = 8 + 4 + crypto_secretbox_MACBYTES + 4;
        }
        sendto(sock_, reinterpret_cast<const char*>(outPkt), totalLen, 0,
               reinterpret_cast<sockaddr*>(&remote_), sizeof(remote_));
    };

    auto decodeAndPresent = [&](const VideoDepacketizer::FrameOut& fr, uint32_t mediaSsrc) {
        if (fr.annexb.empty() || !decMft) return;
        depackFrames++;
        const uint8_t* frameData = fr.annexb.data();
        size_t frameLen = fr.annexb.size();

        // DAVE E2EE Video Frame Decryption (only pass clean decrypted Annex-B into NVDEC!)
        {
            std::lock_guard lk(dave_mtx_);
            if (dave_dec_ && dave_proto_ver_ > 0) {
                size_t w = 0;
                if (daveDecryptorDecrypt(dave_dec_, DAVE_MEDIA_TYPE_VIDEO, frameData, frameLen,
                                         daveBuf.data(), daveBuf.size(), &w) == DAVE_DECRYPTOR_RESULT_CODE_SUCCESS && w > 0) {
                    frameData = daveBuf.data();
                    frameLen = w;
                    daveOk++;
                } else {
                    daveFail++;
                    // Lazy ratchet refresh in case MLS Welcome arrived before OP 12 set streamer_uid_
                    int64_t nowMs = steadyMs();
                    if (dave_session_ && !streamer_uid_.empty() && nowMs - lastRatchetRetryMs > 400) {
                        lastRatchetRetryMs = nowMs;
                        DAVEKeyRatchetHandle kr = daveSessionGetKeyRatchet(dave_session_, streamer_uid_.c_str());
                        if (kr) {
                            daveDecryptorTransitionToPassthroughMode(dave_dec_, false);
                            daveDecryptorTransitionToKeyRatchet(dave_dec_, kr);
                            daveKeyRatchetDestroy(kr);
                        }
                    }
                    depack.setNeedKeyframe(true);
                    return;
                }
            }
        }

        // Cache SPS/PPS so NVDEC always has codec config even if IDR arrives without repeated SPS
        cacheSpsPpsFromFrame(frameData, frameLen);
        if (frameLen >= 5 && frameData[0] == 0 && frameData[1] == 0) {
            lastNaluType = (frameData[2] == 1 ? frameData[3] : frameData[4]) & 0x1F;
        }

        // Gate on clean IDR/SPS keyframe after start or packet loss so NVDEC NEVER smears macroblocks!
        if (depack.needKeyframe()) {
            if (!VideoDepacketizer::hasIdrOrSps(frameData, frameLen)) {
                kfSkipped++;
                int64_t nowMs = steadyMs();
                if (nowMs - lastPliMs > 450) {
                    sendEncryptedRtcpPli(mediaSsrc);
                    lastPliMs = nowMs;
                }
                return;
            }
            if (!frameHasSps(frameData, frameLen) && !cachedSpsPps.empty()) {
                prependBuf.clear();
                prependBuf.insert(prependBuf.end(), cachedSpsPps.begin(), cachedSpsPps.end());
                prependBuf.insert(prependBuf.end(), frameData, frameData + frameLen);
                frameData = prependBuf.data();
                frameLen = prependBuf.size();
            }
            depack.setNeedKeyframe(false);
        }

        auto tDec0 = std::chrono::steady_clock::now();
        mftFed++;
        ComPtr<IMFSample> inSamp;
        ComPtr<IMFMediaBuffer> mb;
        if (SUCCEEDED(MFCreateSample(&inSamp)) && SUCCEEDED(MFCreateMemoryBuffer(DWORD(frameLen), &mb))) {
            BYTE* dst = nullptr;
            if (SUCCEEDED(mb->Lock(&dst, nullptr, nullptr))) {
                std::memcpy(dst, frameData, frameLen);
                mb->Unlock();
                mb->SetCurrentLength(DWORD(frameLen));
                inSamp->AddBuffer(mb.Get());
                decMft->ProcessInput(0, inSamp.Get(), 0);
            }
        }

        for (int drain = 0; drain < 4; ++drain) {
            MFT_OUTPUT_DATA_BUFFER outBuf{};
            DWORD status = 0;
            HRESULT ohr = decMft->ProcessOutput(0, 1, &outBuf, &status);
            if (ohr == MF_E_TRANSFORM_STREAM_CHANGE) {
                for (DWORD i = 0; ; ++i) {
                    ComPtr<IMFMediaType> mtOut;
                    if (FAILED(decMft->GetOutputAvailableType(0, i, &mtOut))) break;
                    GUID sub{};
                    if (SUCCEEDED(mtOut->GetGUID(MF_MT_SUBTYPE, &sub)) && sub == MFVideoFormat_NV12) {
                        decMft->SetOutputType(0, mtOut.Get(), 0);
                        UINT32 dw = 0, dh = 0;
                        MFGetAttributeSize(mtOut.Get(), MF_MT_FRAME_SIZE, &dw, &dh);
                        curDecW = dw; curDecH = dh;
                        vp.Reset(); vpenum.Reset();
                        break;
                    }
                }
                continue;
            }
            if (FAILED(ohr) || !outBuf.pSample) break;

            ComPtr<IMFMediaBuffer> ob;
            if (SUCCEEDED(outBuf.pSample->GetBufferByIndex(0, &ob))) {
                ComPtr<IMFDXGIBuffer> dxBuf;
                ComPtr<ID3D11Texture2D> decTex;
                UINT subRes = 0;
                if (SUCCEEDED(ob.As(&dxBuf)) && SUCCEEDED(dxBuf->GetResource(IID_PPV_ARGS(&decTex))) &&
                    SUCCEEDED(dxBuf->GetSubresourceIndex(&subRes)) && swap && vdev && vctx) {
                    D3D11_TEXTURE2D_DESC td{};
                    decTex->GetDesc(&td);
                    if (curDecW != td.Width || curDecH != td.Height || !vp) {
                        curDecW = td.Width; curDecH = td.Height;
                        vp.Reset(); vpenum.Reset();
                        D3D11_VIDEO_PROCESSOR_CONTENT_DESC vcd{};
                        vcd.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
                        vcd.InputWidth = curDecW; vcd.InputHeight = curDecH;
                        vcd.OutputWidth = sw; vcd.OutputHeight = sh;
                        vcd.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
                        if (SUCCEEDED(vdev->CreateVideoProcessorEnumerator(&vcd, &vpenum)))
                            vdev->CreateVideoProcessor(vpenum.Get(), 0, &vp);
                    }
                    ComPtr<ID3D11Texture2D> backBuf;
                    if (vp && vpenum && SUCCEEDED(swap->GetBuffer(0, IID_PPV_ARGS(&backBuf)))) {
                        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd{};
                        ivd.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
                        ivd.Texture2D.ArraySlice = subRes;
                        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd{};
                        ovd.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
                        ComPtr<ID3D11VideoProcessorInputView> pin;
                        ComPtr<ID3D11VideoProcessorOutputView> pout;
                        if (SUCCEEDED(vdev->CreateVideoProcessorInputView(decTex.Get(), vpenum.Get(), &ivd, &pin)) &&
                            SUCCEEDED(vdev->CreateVideoProcessorOutputView(backBuf.Get(), vpenum.Get(), &ovd, &pout))) {
                            float srcAsp = float(curDecW) / float(std::max<UINT>(1, curDecH));
                            float dstAsp = float(sw) / float(std::max<UINT>(1, sh));
                            RECT dstRc{ 0, 0, LONG(sw), LONG(sh) };
                            if (srcAsp > dstAsp) {
                                LONG nh = LONG(float(sw) / srcAsp);
                                dstRc.top = (LONG(sh) - nh) / 2;
                                dstRc.bottom = dstRc.top + nh;
                            } else if (srcAsp < dstAsp) {
                                LONG nw = LONG(float(sh) * srcAsp);
                                dstRc.left = (LONG(sw) - nw) / 2;
                                dstRc.right = dstRc.left + nw;
                            }
                            D3D11_VIDEO_COLOR bg{};
                            bg.RGBA = { 0.0f, 0.0f, 0.0f, 1.0f };
                            vctx->VideoProcessorSetOutputBackgroundColor(vp.Get(), FALSE, &bg);
                            vctx->VideoProcessorSetStreamDestRect(vp.Get(), 0, TRUE, &dstRc);
                            D3D11_VIDEO_PROCESSOR_STREAM pst{};
                            pst.Enable = TRUE;
                            pst.pInputSurface = pin.Get();
                            vctx->VideoProcessorBlt(vp.Get(), pout.Get(), 0, 1, &pst);
                            swap->Present(0, 0);
                        }
                    }
                }
            }
            outBuf.pSample->Release();
            if (outBuf.pEvents) outBuf.pEvents->Release();
            framesDecoded++;
            framesWindow++;
        }

        float decMs = std::chrono::duration<float, std::milli>(std::chrono::steady_clock::now() - tDec0).count();
        auto now = std::chrono::steady_clock::now();
        float sec = std::chrono::duration<float>(now - windowStart).count();
        if (sec >= 0.5f) {
            {
                std::lock_guard lk(stats_mtx_);
                stats_.width = int32_t(curDecW);
                stats_.height = int32_t(curDecH);
                stats_.fps = float(framesWindow) / sec;
                stats_.bitrate_kbps = (float(bytesWindow) * 8.0f / 1000.0f) / sec;
                stats_.decode_ms = decMs;
                stats_.frames_encoded = framesDecoded;
                stats_.frames_skipped_static = depack.framesDropped();
            }
            updateDiagText();
            framesWindow = 0;
            bytesWindow = 0;
            windowStart = now;
        }
    };

    int64_t lastDiagMs = steadyMs();

    while (running_.load()) {
        int32_t rw = pending_w_.exchange(0);
        int32_t rh = pending_h_.exchange(0);
        if (swap && rw > 16 && rh > 16) {
            sw = UINT(rw); sh = UINT(rh);
            swap->ResizeBuffers(2, sw, sh, DXGI_FORMAT_B8G8R8A8_UNORM, 0);
            vp.Reset(); vpenum.Reset();
        }

        int64_t nowMs = steadyMs();
        if (nowMs - lastDiagMs >= 500) {
            lastDiagMs = nowMs;
            updateDiagText();
        }
        if (has_key_.load()) {
            if (!sentKeepalive) {
                uint8_t ka[8] = { 0x13, 0x37, 0xCA, 0xFE, 0, 0, 0, 1 };
                sendto(sock_, reinterpret_cast<const char*>(ka), sizeof(ka), 0,
                       reinterpret_cast<sockaddr*>(&remote_), sizeof(remote_));
                sentKeepalive = true;
            }
            uint32_t vs = video_ssrc_.load();
            if (vs != 0 && depack.needKeyframe() && (nowMs - lastPliMs > 600)) {
                sendEncryptedRtcpPli(vs);
                lastPliMs = nowMs;
            }
        }

        sockaddr_in from{};
        int fromLen = sizeof(from);
        int n = recvfrom(sock_, reinterpret_cast<char*>(pkt), sizeof(pkt), 0,
                         reinterpret_cast<sockaddr*>(&from), &fromLen);
        if (n < 28 || !has_key_.load()) {
            bool wantPli = false;
            while (depack.tryEmit(steadyMs(), readyFrame, wantPli)) {
                decodeAndPresent(readyFrame, video_ssrc_.load());
            }
            if (wantPli && (steadyMs() - lastPliMs > 400)) {
                sendEncryptedRtcpPli(video_ssrc_.load());
                lastPliMs = steadyMs();
            }
            continue;
        }

        uint8_t pt = pkt[1] & 0x7F;
        if (pt >= 72 && pt <= 76) continue; // RTCP
        bool marker = (pkt[1] & 0x80) != 0;
        bool hasPad = (pkt[0] & 0x20) != 0;
        uint8_t cc = pkt[0] & 0x0F;
        bool ext = (pkt[0] & 0x10) != 0;
        int hdrLen = 12 + cc * 4;
        uint16_t extWords = 0;
        if (ext && n > hdrLen + 4) {
            extWords = getBE16(pkt + hdrLen + 2);
            hdrLen += 4;
        }
        if (n <= hdrLen + 20) continue;

        uint16_t seq = getBE16(pkt + 2);
        uint32_t rtpTs = getBE32(pkt + 4);
        uint32_t ssrc = getBE32(pkt + 8);
        uint8_t nonce[24]{};
        std::memcpy(nonce, pkt + n - 4, 4);

        int cipherLen = n - hdrLen - 4;
        unsigned long long plen = 0;
        bool ok = false;
        if (use_xchacha_) {
            ok = crypto_aead_xchacha20poly1305_ietf_decrypt(
                     plain, &plen, nullptr,
                     pkt + hdrLen, size_t(cipherLen),
                     pkt, size_t(hdrLen),
                     nonce, key_) == 0;
        } else {
            ok = crypto_secretbox_open_easy(plain, pkt + hdrLen, size_t(cipherLen), nonce, key_) == 0;
            plen = size_t(cipherLen - crypto_secretbox_MACBYTES);
        }
        if (!ok || plen == 0) continue;
        rxUdpPkts++;

        // CRITICAL: Strip RFC 3550 RTP Padding bytes if P bit (0x20) is set!
        if (hasPad && plen > 0) {
            int padLen = int(plain[plen - 1]);
            if (padLen > 0 && padLen <= int(plen)) plen -= size_t(padLen);
        }

        int payOff = ext ? int(extWords) * 4 : 0;
        if (payOff >= int(plen)) {
            // 0-byte Discord BWE padding probe packet! Record seq so VideoDepacketizer doesn't mistake it for loss!
            uint32_t vsCheck = video_ssrc_.load();
            if ((pt == 101 || pt == 103) && (vsCheck == 0 || ssrc == vsCheck)) depack.noteEmptyProbeSeq(seq);
            continue;
        }
        const uint8_t* pay = plain + payOff;
        int payLen = int(plen) - payOff;
        bytesWindow += payLen;

        // Stream audio (PT 120) -> DAVE decrypt + feed into WASAPI mixer
        if (pt == 120 && voice_) {
            if (audio_ssrc_.load() == 0) {
                audio_ssrc_.store(ssrc);
                voice_->setUserVolume(ssrc ^ 0x80000000u, stream_vol_.load());
            }
            const uint8_t* opPtr = pay;
            int opLen = payLen;
            {
                std::lock_guard lk(dave_mtx_);
                if (dave_dec_ && dave_proto_ver_ > 0) {
                    size_t w = 0;
                    if (daveDecryptorDecrypt(dave_dec_, DAVE_MEDIA_TYPE_AUDIO, pay, size_t(payLen),
                                             daveBuf.data(), daveBuf.size(), &w) == DAVE_DECRYPTOR_RESULT_CODE_SUCCESS && w > 0) {
                        opPtr = daveBuf.data();
                        opLen = int(w);
                    }
                }
            }
            voice_->onRemoteOpus(ssrc ^ 0x80000000u, seq, opPtr, opLen);
            continue;
        }

        // Stream video H.264 (PT 101 / 103; skip RTX 102/104 and secondary simulcast SSRCs!) -> Reorder Buffer -> Clean Annex-B -> DAVE -> NVDEC!
        if (pt != 101 && pt != 103) continue;
        lastVideoPt = pt;
        uint32_t targetVssrc = video_ssrc_.load();
        if (targetVssrc == 0) {
            video_ssrc_.store(ssrc);
            targetVssrc = ssrc;
        } else if (ssrc != targetVssrc) {
            continue;
        }

        bool wantPli = false;
        if (depack.pushPacket(seq, rtpTs, marker, pay, payLen, steadyMs(), readyFrame, wantPli)) {
            decodeAndPresent(readyFrame, ssrc);
        }
        if (wantPli && (steadyMs() - lastPliMs > 400)) {
            sendEncryptedRtcpPli(ssrc);
            lastPliMs = steadyMs();
        }
    }

    decMft.Reset();
    dxgiMgr.Reset();
    swap.Reset();
    vp.Reset();
    vpenum.Reset();
    } // end inner COM scope

    MFShutdown();
    CoUninitialize();
    revertRealtimeThread(mm);
}

} // namespace fc
