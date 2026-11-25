#include "VoiceTransport.h"
#include "NetTurbo.h"
#include <sodium.h>
#include <algorithm>
#include <chrono>
#include <cstring>

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
} // namespace

VoiceTransport::VoiceTransport() {
    WSADATA wsa{};
    WSAStartup(MAKEWORD(2, 2), &wsa);
    sodium_init();
}

VoiceTransport::~VoiceTransport() {
    disconnect();
}

void VoiceTransport::daveDestroyLocked() {
    for (auto& [uid, dec] : user_decryptors_) {
        if (dec) daveDecryptorDestroy(dec);
    }
    user_decryptors_.clear();
    ssrc_to_user_.clear();
    if (dave_enc_) { daveEncryptorDestroy(dave_enc_); dave_enc_ = nullptr; }
    if (dave_session_) { daveSessionDestroy(dave_session_); dave_session_ = nullptr; }
    dave_proto_ver_ = 0;
}

bool VoiceTransport::daveInit(uint16_t protocol_version, uint64_t channel_id, const char* self_user_id, uint32_t self_ssrc) {
    std::lock_guard lk(dave_mtx_);
    self_user_id_ = self_user_id ? self_user_id : "";
    dave_proto_ver_ = protocol_version;
    if (!dave_session_) {
        dave_session_ = daveSessionCreate(nullptr, nullptr, nullptr, nullptr);
    }
    if (!dave_enc_) {
        dave_enc_ = daveEncryptorCreate();
    }
    if (!dave_session_ || !dave_enc_) return false;

    daveEncryptorAssignSsrcToCodec(dave_enc_, self_ssrc, DAVE_CODEC_OPUS);
    if (protocol_version > 0) {
        daveSessionInit(dave_session_, protocol_version, channel_id, self_user_id_.c_str());
        daveEncryptorSetPassthroughMode(dave_enc_, false);
    } else {
        daveSessionReset(dave_session_);
        daveEncryptorSetPassthroughMode(dave_enc_, true);
    }
    return true;
}

void VoiceTransport::daveSetVideoSsrc(uint32_t video_ssrc) {
    if (video_ssrc == 0) return;
    if (sock_ != INVALID_SOCKET) optimizeUdpSocket(sock_, true, &remote_);
    std::lock_guard lk(dave_mtx_);
    if (!dave_enc_) dave_enc_ = daveEncryptorCreate();
    if (dave_enc_) daveEncryptorAssignSsrcToCodec(dave_enc_, video_ssrc, DAVE_CODEC_H264);
}

void VoiceTransport::daveAddUser(const char* user_id) {
    if (!user_id || !*user_id) return;
    std::lock_guard lk(dave_mtx_);
    if (!dave_session_) dave_session_ = daveSessionCreate(nullptr, nullptr, nullptr, nullptr);
    if (dave_session_) daveSessionAddRecognizedUser(dave_session_, user_id);
    if (user_decryptors_.find(user_id) == user_decryptors_.end()) {
        DAVEDecryptorHandle dec = daveDecryptorCreate();
        if (dec) {
            daveDecryptorTransitionToPassthroughMode(dec, dave_proto_ver_ == 0);
            user_decryptors_[user_id] = dec;
        }
    }
}

void VoiceTransport::daveRemoveUser(const char* user_id) {
    if (!user_id || !*user_id) return;
    std::lock_guard lk(dave_mtx_);
    if (dave_session_) daveSessionRemoveRecognizedUser(dave_session_, user_id);
    auto it = user_decryptors_.find(user_id);
    if (it != user_decryptors_.end()) {
        if (it->second) daveDecryptorDestroy(it->second);
        user_decryptors_.erase(it);
    }
}

void VoiceTransport::daveMapSsrc(uint32_t ssrc, const char* user_id) {
    if (!user_id || !*user_id) return;
    std::lock_guard lk(dave_mtx_);
    ssrc_to_user_[ssrc] = user_id;
    auto& dec = user_decryptors_[user_id];
    if (!dec) dec = daveDecryptorCreate();
    if (dec) {
        if (dave_session_ && dave_proto_ver_ > 0) {
            DAVEKeyRatchetHandle kr = daveSessionGetKeyRatchet(dave_session_, user_id);
            if (kr) {
                daveDecryptorTransitionToPassthroughMode(dec, false);
                daveDecryptorTransitionToKeyRatchet(dec, kr);
                daveKeyRatchetDestroy(kr);
            }
        } else {
            daveDecryptorTransitionToPassthroughMode(dec, true);
        }
    }
}

int32_t VoiceTransport::daveGetKeyPackage(uint8_t* out_buf, int32_t cap) {
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

void VoiceTransport::daveSetExternalSender(const uint8_t* data, int32_t len) {
    std::lock_guard lk(dave_mtx_);
    if (dave_session_ && data && len > 0) {
        daveSessionSetExternalSender(dave_session_, data, size_t(len));
    }
}

int32_t VoiceTransport::daveProcessProposals(const uint8_t* data, int32_t len, uint8_t* out_commit_welcome, int32_t cap) {
    std::lock_guard lk(dave_mtx_);
    if (!dave_session_ || !data || len <= 0) return 0;
    uint8_t* cw = nullptr;
    size_t cwLen = 0;
    daveSessionProcessProposals(dave_session_, data, size_t(len), &cw, &cwLen);
    if (!cw || cwLen == 0) return 0;
    int32_t n = (out_commit_welcome && cap > 0) ? int32_t(std::min<size_t>(cwLen, size_t(cap))) : 0;
    if (n > 0) std::memcpy(out_commit_welcome, cw, size_t(n));
    daveFree(cw);
    return n;
}

bool VoiceTransport::daveProcessCommit(const uint8_t* data, int32_t len) {
    std::lock_guard lk(dave_mtx_);
    if (!dave_session_ || !data || len <= 0) return false;
    DAVECommitResultHandle res = daveSessionProcessCommit(dave_session_, data, size_t(len));
    if (!res) return false;
    bool ok = !daveCommitResultIsFailed(res);
    daveCommitResultDestroy(res);
    return ok;
}

bool VoiceTransport::daveProcessWelcome(const uint8_t* data, int32_t len) {
    std::lock_guard lk(dave_mtx_);
    if (!dave_session_ || !data || len <= 0) return false;
    DAVEWelcomeResultHandle res = daveSessionProcessWelcome(dave_session_, data, size_t(len));
    if (!res) return false;
    daveWelcomeResultDestroy(res);
    return true;
}

void VoiceTransport::daveRefreshRatchetsLocked(uint16_t protocol_version) {
    dave_proto_ver_ = protocol_version;
    if (!dave_session_) return;
    if (protocol_version == 0) {
        if (dave_enc_) daveEncryptorSetPassthroughMode(dave_enc_, true);
        for (auto& [uid, dec] : user_decryptors_) {
            if (dec) daveDecryptorTransitionToPassthroughMode(dec, true);
        }
        return;
    }

    if (dave_enc_ && !self_user_id_.empty()) {
        DAVEKeyRatchetHandle selfKr = daveSessionGetKeyRatchet(dave_session_, self_user_id_.c_str());
        if (selfKr) {
            daveEncryptorSetPassthroughMode(dave_enc_, false);
            daveEncryptorSetKeyRatchet(dave_enc_, selfKr);
            daveKeyRatchetDestroy(selfKr);
            dave_enc_active_.store(true);
        }
    }
    for (auto& [uid, dec] : user_decryptors_) {
        if (!dec) continue;
        DAVEKeyRatchetHandle kr = daveSessionGetKeyRatchet(dave_session_, uid.c_str());
        if (kr) {
            daveDecryptorTransitionToPassthroughMode(dec, false);
            daveDecryptorTransitionToKeyRatchet(dec, kr);
            daveKeyRatchetDestroy(kr);
        }
    }
}

void VoiceTransport::daveExecuteTransition(uint16_t protocol_version) {
    std::lock_guard lk(dave_mtx_);
    daveRefreshRatchetsLocked(protocol_version);
}

bool VoiceTransport::connectAndDiscover(const char* ip, uint16_t port, uint32_t ssrc, std::string& out_ext_ip, uint16_t& out_ext_port) {
    disconnect();
    ssrc_ = ssrc;
    sock_ = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock_ == INVALID_SOCKET) return false;

    DWORD timeoutMs = 1500;
    setsockopt(sock_, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeoutMs), sizeof(timeoutMs));

    std::memset(&remote_, 0, sizeof(remote_));
    remote_.sin_family = AF_INET;
    remote_.sin_port = htons(port);
    if (inet_pton(AF_INET, ip, &remote_.sin_addr) != 1) {
        disconnect();
        return false;
    }

    uint8_t req[74]{};
    putBE16(req + 0, 0x0001);
    putBE16(req + 2, 70);
    putBE32(req + 4, ssrc);

    LARGE_INTEGER freq{}, t0{}, t1{};
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&t0);

    if (sendto(sock_, reinterpret_cast<const char*>(req), sizeof(req), 0,
               reinterpret_cast<sockaddr*>(&remote_), sizeof(remote_)) != sizeof(req)) {
        disconnect();
        return false;
    }

    uint8_t resp[128]{};
    sockaddr_in from{};
    int fromLen = sizeof(from);
    int n = recvfrom(sock_, reinterpret_cast<char*>(resp), sizeof(resp), 0,
                     reinterpret_cast<sockaddr*>(&from), &fromLen);
    QueryPerformanceCounter(&t1);
    if (n < 74 || getBE16(resp) != 0x0002) {
        disconnect();
        return false;
    }

    ping_ms_.store(int32_t((t1.QuadPart - t0.QuadPart) * 1000 / std::max<LONGLONG>(1, freq.QuadPart)));
    char ipBuf[65]{};
    std::memcpy(ipBuf, resp + 8, 64);
    out_ext_ip = ipBuf;
    out_ext_port = getBE16(resp + 72);

    DWORD rxTimeout = 15;
    setsockopt(sock_, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&rxTimeout), sizeof(rxTimeout));
    optimizeUdpSocket(sock_, false, &remote_);

    connected_.store(true);
    running_.store(true);
    rx_thread_ = std::thread([this] { rxLoop(); });
    return true;
}

void VoiceTransport::setKey(const uint8_t key[32], bool use_xchacha_rtpsize) {
    std::lock_guard lk(tx_mtx_);
    std::memcpy(key_, key, 32);
    use_xchacha_ = use_xchacha_rtpsize;
    has_key_.store(true);
}

void VoiceTransport::disconnect() {
    running_.store(false);
    connected_.store(false);
    has_key_.store(false);
    if (sock_ != INVALID_SOCKET) {
        closesocket(sock_);
        sock_ = INVALID_SOCKET;
    }
    if (rx_thread_.joinable()) rx_thread_.join();
    {
        std::lock_guard lk(dave_mtx_);
        daveDestroyLocked();
    }
    tx_pkts_.store(0);
    audio_tx_pkts_.store(0);
    video_tx_pkts_.store(0);
    rx_pkts_.store(0);
    pli_rx_.store(0);
    dave_enc_active_.store(false);
    cached_sps_pps_.clear();
}

bool VoiceTransport::sendEncryptedRtp(uint8_t payload_type, uint16_t seq, uint32_t ts, uint32_t ssrc, bool marker, const uint8_t* payload, int len) {
    if (!isReady() || sock_ == INVALID_SOCKET || len <= 0 || len > 1350) return false;
    std::lock_guard lk(tx_mtx_);

    uint8_t pkt[1500]{};
    pkt[0] = 0x80;
    pkt[1] = uint8_t((marker ? 0x80 : 0x00) | (payload_type & 0x7F));
    putBE16(pkt + 2, seq);
    putBE32(pkt + 4, ts);
    putBE32(pkt + 8, ssrc);

    uint32_t ctr = nonce_counter_++;
    int totalLen = 0;

    if (use_xchacha_) {
        uint8_t nonce[24]{};
        std::memcpy(nonce, &ctr, 4);
        unsigned long long clen = 0;
        if (crypto_aead_xchacha20poly1305_ietf_encrypt(
                pkt + 12, &clen,
                payload, size_t(len),
                pkt, 12,
                nullptr, nonce, key_) != 0) {
            return false;
        }
        std::memcpy(pkt + 12 + clen, &ctr, 4);
        totalLen = int(12 + clen + 4);
    } else {
        uint8_t nonce[24]{};
        std::memcpy(nonce, &ctr, 4);
        if (crypto_secretbox_easy(pkt + 12, payload, size_t(len), nonce, key_) != 0) return false;
        std::memcpy(pkt + 12 + len + crypto_secretbox_MACBYTES, &ctr, 4);
        totalLen = 12 + len + crypto_secretbox_MACBYTES + 4;
    }

    int sent = sendto(sock_, reinterpret_cast<const char*>(pkt), totalLen, 0,
                      reinterpret_cast<sockaddr*>(&remote_), sizeof(remote_));
    if (sent == totalLen) {
        tx_pkts_.fetch_add(1);
        if (payload_type == 120) audio_tx_pkts_.fetch_add(1);
        else if (payload_type == 101 || payload_type == 103) video_tx_pkts_.fetch_add(1);
        return true;
    }
    return false;
}

bool VoiceTransport::sendOpus(const uint8_t* opus, int len, uint32_t timestamp) {
    last_audio_ts_ = timestamp;
    uint8_t daveBuf[1350];
    const uint8_t* payload = opus;
    int payloadLen = len;

    // Apply DAVE E2EE media encryption when active (silence frame 0xF8,0xFF,0xFE stays unencrypted per spec)
    bool isSilence = (len == 3 && opus[0] == 0xF8 && opus[1] == 0xFF && opus[2] == 0xFE);
    if (!isSilence) {
        std::lock_guard lk(dave_mtx_);
        if (dave_enc_ && !daveEncryptorIsPassthroughMode(dave_enc_) && daveEncryptorHasKeyRatchet(dave_enc_)) {
            size_t written = 0;
            if (daveEncryptorEncrypt(dave_enc_, DAVE_MEDIA_TYPE_AUDIO, ssrc_, opus, size_t(len),
                                     daveBuf, sizeof(daveBuf), &written) == DAVE_ENCRYPTOR_RESULT_CODE_SUCCESS && written > 0) {
                payload = daveBuf;
                payloadLen = int(written);
            }
        }
    }
    return sendEncryptedRtp(120, audio_seq_++, timestamp, ssrc_, false, payload, payloadLen);
}

void VoiceTransport::sendSilenceFrames() {
    static const uint8_t kSilence[3] = { 0xF8, 0xFF, 0xFE };
    for (int i = 0; i < 5; ++i) {
        last_audio_ts_ += 960;
        sendOpus(kSilence, 3, last_audio_ts_);
    }
}

bool VoiceTransport::sendH264Nalu(const uint8_t* nalu, int len, uint32_t video_ssrc, uint32_t timestamp, bool marker) {
    if (!isReady() || len <= 0) return false;
    constexpr int kMaxPayload = 1100; // MTU-safe even over PPPoE/VPN (zero IP fragmentation)
    if (len <= kMaxPayload) {
        return sendEncryptedRtp(101, video_seq_++, timestamp, video_ssrc, marker, nalu, len);
    }
    uint8_t naluHdr = nalu[0];
    uint8_t fuInd = (naluHdr & 0xE0) | 28;
    uint8_t naluType = naluHdr & 0x1F;
    int offset = 1;
    uint8_t frag[kMaxPayload + 2];
    int burst = 0;
    int totalFrags = (len - 1 + (kMaxPayload - 3)) / (kMaxPayload - 2);
    while (offset < len) {
        int chunk = std::min(kMaxPayload - 2, len - offset);
        bool first = (offset == 1);
        bool last = (offset + chunk >= len);
        frag[0] = fuInd;
        frag[1] = uint8_t((first ? 0x80 : 0x00) | (last ? 0x40 : 0x00) | naluType);
        std::memcpy(frag + 2, nalu + offset, size_t(chunk));
        if (!sendEncryptedRtp(101, video_seq_++, timestamp, video_ssrc, marker && last, frag, chunk + 2)) {
            return false;
        }
        offset += chunk;
        // Turbo ultra-low-latency pacing: P-frames (<=12 frags) fly out with ZERO sleep; giant IDR keyframes micro-pace 100us per 10 pkts!
        if (totalFrags > 12 && ++burst >= 10 && offset < len) {
            burst = 0;
            std::this_thread::sleep_for(std::chrono::microseconds(100));
        }
    }
    return true;
}

bool VoiceTransport::sendH264AccessUnit(const uint8_t* annexb, int len, uint32_t video_ssrc, uint32_t timestamp) {
    if (!isReady() || !annexb || len < 4 || video_ssrc == 0) return false;

    // 1. Scan raw Annex-B NALUs to cache SPS(7)/PPS(8) and ensure EVERY IDR(5) keyframe starts with SPS+PPS!
    bool hasSps = false, hasIdr = false;
    std::vector<uint8_t> foundSpsPps;
    {
        int p = 0;
        while (p + 4 <= len) {
            int sc = 0;
            if (annexb[p] == 0 && annexb[p + 1] == 0 && annexb[p + 2] == 1) sc = 3;
            else if (annexb[p] == 0 && annexb[p + 1] == 0 && annexb[p + 2] == 0 && annexb[p + 3] == 1) sc = 4;
            if (sc == 0) { p++; continue; }
            int s = p + sc, nx = s;
            while (nx + 3 < len) {
                if (annexb[nx] == 0 && annexb[nx + 1] == 0 &&
                    (annexb[nx + 2] == 1 || (nx + 3 < len && annexb[nx + 2] == 0 && annexb[nx + 3] == 1))) break;
                nx++;
            }
            if (nx + 3 >= len) nx = len;
            if (nx > s) {
                uint8_t nt = annexb[s] & 0x1F;
                if (nt == 7) hasSps = true;
                if (nt == 5) hasIdr = true;
                if (nt == 7 || nt == 8) {
                    foundSpsPps.push_back(0); foundSpsPps.push_back(0); foundSpsPps.push_back(0); foundSpsPps.push_back(1);
                    foundSpsPps.insert(foundSpsPps.end(), annexb + s, annexb + nx);
                }
            }
            p = nx;
        }
    }
    if (!foundSpsPps.empty()) cached_sps_pps_ = std::move(foundSpsPps);

    std::vector<uint8_t> fullAu;
    const uint8_t* rawPtr = annexb;
    int rawLen = len;
    if (hasIdr && !hasSps && !cached_sps_pps_.empty()) {
        fullAu.reserve(cached_sps_pps_.size() + size_t(len));
        fullAu.insert(fullAu.end(), cached_sps_pps_.begin(), cached_sps_pps_.end());
        fullAu.insert(fullAu.end(), annexb, annexb + len);
        rawPtr = fullAu.data();
        rawLen = int(fullAu.size());
    }

    // 2. DAVE E2EE Video Frame Encryption when active (wait for ratchet if DAVE v1 is mandated!)
    const uint8_t* data = rawPtr;
    int curLen = rawLen;
    std::vector<uint8_t> daveBuf;
    {
        std::lock_guard lk(dave_mtx_);
        if (dave_enc_ && !daveEncryptorIsPassthroughMode(dave_enc_)) {
            if (!daveEncryptorHasKeyRatchet(dave_enc_)) {
                // Lazily fetch self key ratchet if MLS group is active
                if (dave_session_ && !self_user_id_.empty()) {
                    DAVEKeyRatchetHandle kr = daveSessionGetKeyRatchet(dave_session_, self_user_id_.c_str());
                    if (kr) {
                        daveEncryptorSetKeyRatchet(dave_enc_, kr);
                        daveKeyRatchetDestroy(kr);
                        dave_enc_active_.store(true);
                    }
                }
            }
            if (daveEncryptorHasKeyRatchet(dave_enc_)) {
                size_t maxOut = daveEncryptorGetMaxCiphertextByteSize(dave_enc_, DAVE_MEDIA_TYPE_VIDEO, size_t(rawLen));
                daveBuf.resize(std::max(maxOut, size_t(rawLen) + 256));
                size_t written = 0;
                if (daveEncryptorEncrypt(dave_enc_, DAVE_MEDIA_TYPE_VIDEO, video_ssrc, rawPtr, size_t(rawLen),
                                         daveBuf.data(), daveBuf.size(), &written) == DAVE_ENCRYPTOR_RESULT_CODE_SUCCESS && written > 0) {
                    data = daveBuf.data();
                    curLen = int(written);
                }
            }
        }
    }

    // 3. Split Annex-B into NALUs and send via RFC 6184 Single-NALU / FU-A with marker=true on final NALU
    int pos = 0;
    bool sentAny = false;
    while (pos + 4 <= curLen) {
        int scLen = 0;
        if (data[pos] == 0 && data[pos + 1] == 0 && data[pos + 2] == 1) scLen = 3;
        else if (data[pos] == 0 && data[pos + 1] == 0 && data[pos + 2] == 0 && data[pos + 3] == 1) scLen = 4;
        if (scLen == 0) { pos++; continue; }
        int start = pos + scLen;
        int next = start;
        while (next + 3 < curLen) {
            if (data[next] == 0 && data[next + 1] == 0 &&
                (data[next + 2] == 1 || (next + 3 < curLen && data[next + 2] == 0 && data[next + 3] == 1))) {
                break;
            }
            next++;
        }
        if (next + 3 >= curLen) next = curLen;
        bool isLast = (next >= curLen);
        if (next > start) {
            sentAny |= sendH264Nalu(data + start, next - start, video_ssrc, timestamp, isLast);
        }
        pos = next;
    }
    return sentAny;
}

void VoiceTransport::rxLoop() {
    HANDLE mm = boostRealtimeThread(L"Pro Audio");
    uint8_t buf[2048];
    uint8_t plain[2048];
    uint8_t daveOut[2048];
    while (running_.load()) {
        sockaddr_in from{};
        int fromLen = sizeof(from);
        int n = recvfrom(sock_, reinterpret_cast<char*>(buf), sizeof(buf), 0,
                         reinterpret_cast<sockaddr*>(&from), &fromLen);
        if (n < 12 || !has_key_.load()) continue;
        uint8_t ptFull = buf[1];
        uint8_t pt = ptFull & 0x7F;
        // Detect incoming RTCP PSFB (PT=206: PLI FMT=1 / FIR FMT=4) -> immediately force an NVENC IDR keyframe for viewers!
        if (ptFull == 206 || ptFull == 205) {
            uint8_t fmt = buf[0] & 0x1F;
            if (ptFull == 206 && (fmt == 1 || fmt == 4)) {
                pli_rx_.fetch_add(1);
                if (pli_cb_) pli_cb_();
            }
            continue;
        }
        if (n < 28 || (pt >= 72 && pt <= 76)) continue; // Other RTCP (SR/RR)

        uint8_t cc = buf[0] & 0x0F;
        bool ext = (buf[0] & 0x10) != 0;
        int hdrLen = 12 + cc * 4;
        uint16_t extWords = 0;
        if (ext && n > hdrLen + 4) {
            extWords = getBE16(buf + hdrLen + 2);
            hdrLen += 4;
        }
        if (n <= hdrLen + 20) continue;

        uint16_t seq = getBE16(buf + 2);
        uint32_t ssrc = getBE32(buf + 8);
        uint8_t nonce[24]{};
        std::memcpy(nonce, buf + n - 4, 4);

        int cipherLen = n - hdrLen - 4;
        unsigned long long plen = 0;
        bool ok = false;
        if (use_xchacha_) {
            ok = crypto_aead_xchacha20poly1305_ietf_decrypt(
                     plain, &plen, nullptr,
                     buf + hdrLen, size_t(cipherLen),
                     buf, size_t(hdrLen),
                     nonce, key_) == 0;
        } else {
            ok = crypto_secretbox_open_easy(plain, buf + hdrLen, size_t(cipherLen), nonce, key_) == 0;
            plen = size_t(cipherLen - crypto_secretbox_MACBYTES);
        }
        if (!ok || plen == 0) continue;

        // Strip RFC 3550 RTP Padding bytes if P bit (0x20) is set!
        bool hasPad = (buf[0] & 0x20) != 0;
        if (hasPad && plen > 0) {
            int padLen = int(plain[plen - 1]);
            if (padLen > 0 && padLen <= int(plen)) plen -= size_t(padLen);
        }

        int audioOff = ext ? int(extWords) * 4 : 0;
        if (audioOff >= int(plen)) continue;

        const uint8_t* opusPtr = plain + audioOff;
        int opusLen = int(plen) - audioOff;

        // Decrypt DAVE E2EE audio frame if active
        if (pt == 120) {
            std::lock_guard lk(dave_mtx_);
            bool decrypted = false;
            auto itU = ssrc_to_user_.find(ssrc);
            if (itU != ssrc_to_user_.end()) {
                auto itD = user_decryptors_.find(itU->second);
                if (itD != user_decryptors_.end() && itD->second) {
                    size_t written = 0;
                    if (daveDecryptorDecrypt(itD->second, DAVE_MEDIA_TYPE_AUDIO, opusPtr, size_t(opusLen),
                                             daveOut, sizeof(daveOut), &written) == DAVE_DECRYPTOR_RESULT_CODE_SUCCESS && written > 0) {
                        opusPtr = daveOut;
                        opusLen = int(written);
                        decrypted = true;
                    }
                }
            }
            // Fallback: if SSRC arrived before OP 5 SPEAKING, probe known decryptors once and bind SSRC!
            if (!decrypted && dave_proto_ver_ > 0 && itU == ssrc_to_user_.end()) {
                for (auto& [uid, dec] : user_decryptors_) {
                    if (!dec) continue;
                    size_t written = 0;
                    if (daveDecryptorDecrypt(dec, DAVE_MEDIA_TYPE_AUDIO, opusPtr, size_t(opusLen),
                                             daveOut, sizeof(daveOut), &written) == DAVE_DECRYPTOR_RESULT_CODE_SUCCESS && written > 0) {
                        ssrc_to_user_[ssrc] = uid;
                        opusPtr = daveOut;
                        opusLen = int(written);
                        decrypted = true;
                        break;
                    }
                }
            }
        }

        rx_pkts_.fetch_add(1);
        if (rx_cb_ && pt == 120) {
            rx_cb_(ssrc, seq, opusPtr, opusLen);
        }
    }
    revertRealtimeThread(mm);
}

} // namespace fc
