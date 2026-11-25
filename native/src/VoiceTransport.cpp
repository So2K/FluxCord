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
inline bool isSilenceFrame(const uint8_t* p, int len) {
    return p && len == 3 && p[0] == 0xF8 && p[1] == 0xFF && p[2] == 0xFE;
}
inline int64_t steadyMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}
} // namespace

VoiceTransport::VoiceTransport() {
    WSADATA wsa{};
    wsa_ok_ = (WSAStartup(MAKEWORD(2, 2), &wsa) == 0);
    sodium_init();
}

VoiceTransport::~VoiceTransport() {
    {
        std::lock_guard lk(cb_mtx_);
        rx_cb_ = nullptr;
        pli_cb_ = nullptr;
    }
    disconnect();
    if (wsa_ok_) WSACleanup();
}

void VoiceTransport::daveDestroyLocked() {
    for (auto& [uid, dec] : user_decryptors_) {
        if (dec) daveDecryptorDestroy(dec);
    }
    user_decryptors_.clear();
    user_has_ratchet_.clear();
    ssrc_to_user_.clear();
    if (dave_enc_) { daveEncryptorDestroy(dave_enc_); dave_enc_ = nullptr; }
    if (dave_session_) { daveSessionDestroy(dave_session_); dave_session_ = nullptr; }
    dave_proto_ver_ = 0;
    dave_enc_active_.store(false);
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
        dave_enc_active_.store(false);
    }
    return true;
}

void VoiceTransport::daveSetVideoSsrc(uint32_t video_ssrc) {
    if (video_ssrc == 0) return;
    {
        std::lock_guard lk(tx_mtx_);
        if (sock_ != INVALID_SOCKET) optimizeUdpSocket(sock_, true, &remote_, &qos_flow_id_);
    }
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
            user_has_ratchet_[user_id] = false;
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
    user_has_ratchet_.erase(user_id);
    // Purge stale SSRC mappings for removed user so reused SSRCs never map to old users!
    for (auto sit = ssrc_to_user_.begin(); sit != ssrc_to_user_.end(); ) {
        if (sit->second == user_id) sit = ssrc_to_user_.erase(sit);
        else ++sit;
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
                user_has_ratchet_[user_id] = true;
            }
        } else {
            daveDecryptorTransitionToPassthroughMode(dec, true);
            user_has_ratchet_[user_id] = false;
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
    int32_t n = 0;
    if (len <= size_t(cap)) {
        n = int32_t(len);
        std::memcpy(out_buf, kp, len);
    }
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
    int32_t n = 0;
    if (out_commit_welcome && cap > 0 && cwLen <= size_t(cap)) {
        n = int32_t(cwLen);
        std::memcpy(out_commit_welcome, cw, cwLen);
    }
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
        dave_enc_active_.store(false);
        for (auto& [uid, dec] : user_decryptors_) {
            if (dec) daveDecryptorTransitionToPassthroughMode(dec, true);
            user_has_ratchet_[uid] = false;
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
            user_has_ratchet_[uid] = true;
        }
    }
}

void VoiceTransport::daveExecuteTransition(uint16_t protocol_version) {
    std::lock_guard lk(dave_mtx_);
    daveRefreshRatchetsLocked(protocol_version);
}

bool VoiceTransport::connectAndDiscover(const char* ip, uint16_t port, uint32_t ssrc, std::string& out_ext_ip, uint16_t& out_ext_port) {
    if (!ip || !*ip) return false;
    std::lock_guard life(lifecycle_mtx_);
    disconnectLocked();
    ssrc_.store(ssrc);

    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s == INVALID_SOCKET) return false;

    DWORD timeoutMs = 1500;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeoutMs), sizeof(timeoutMs));

    sockaddr_in rem{};
    rem.sin_family = AF_INET;
    rem.sin_port = htons(port);
    if (inet_pton(AF_INET, ip, &rem.sin_addr) != 1) {
        closesocket(s);
        return false;
    }

    uint8_t req[74]{};
    putBE16(req + 0, 0x0001);
    putBE16(req + 2, 70);
    putBE32(req + 4, ssrc);

    LARGE_INTEGER freq{}, t0{}, t1{};
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&t0);

    if (sendto(s, reinterpret_cast<const char*>(req), sizeof(req), 0,
               reinterpret_cast<sockaddr*>(&rem), sizeof(rem)) != sizeof(req)) {
        closesocket(s);
        return false;
    }

    uint8_t resp[128]{};
    sockaddr_in from{};
    int fromLen = sizeof(from);
    int n = recvfrom(s, reinterpret_cast<char*>(resp), sizeof(resp), 0,
                     reinterpret_cast<sockaddr*>(&from), &fromLen);
    QueryPerformanceCounter(&t1);
    if (n < 74 || getBE16(resp) != 0x0002) {
        closesocket(s);
        return false;
    }

    ping_ms_.store(int32_t((t1.QuadPart - t0.QuadPart) * 1000 / std::max<LONGLONG>(1, freq.QuadPart)));
    char ipBuf[65]{};
    std::memcpy(ipBuf, resp + 8, 64);
    out_ext_ip = ipBuf;
    out_ext_port = getBE16(resp + 72);

    DWORD rxTimeout = 15;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&rxTimeout), sizeof(rxTimeout));
    ULONG flowId = 0;
    optimizeUdpSocket(s, false, &rem, &flowId);

    {
        std::lock_guard lk(tx_mtx_);
        sock_ = s;
        qos_flow_id_ = flowId;
        remote_ = rem;
        audio_seq_ = 0;
        video_seq_ = 0;
        nonce_counter_ = 1;
        last_audio_ts_ = 0;
    }

    connected_.store(true);
    running_.store(true);
    rx_thread_ = std::thread([this] { rxLoop(); });
    return true;
}

void VoiceTransport::setKey(const uint8_t key[32], bool use_xchacha_rtpsize) {
    if (!key) return;
    std::lock_guard lk(key_mtx_);
    std::memcpy(key_, key, 32);
    use_xchacha_ = use_xchacha_rtpsize;
    has_key_.store(true);
}

void VoiceTransport::disconnect() {
    std::lock_guard life(lifecycle_mtx_);
    disconnectLocked();
}

void VoiceTransport::disconnectLocked() {
    running_.store(false);
    connected_.store(false);
    has_key_.store(false);

    // Wake recvfrom cleanly BEFORE joining rx_thread_, then close socket AFTER join (zero handle-reuse UB!)
    {
        std::lock_guard lk(tx_mtx_);
        if (sock_ != INVALID_SOCKET) shutdown(sock_, SD_BOTH);
    }
    if (rx_thread_.joinable()) rx_thread_.join();

    {
        std::lock_guard lk(tx_mtx_);
        if (sock_ != INVALID_SOCKET) {
            removeUdpQosFlow(sock_, qos_flow_id_);
            closesocket(sock_);
            sock_ = INVALID_SOCKET;
        }
    }
    {
        std::lock_guard lk(dave_mtx_);
        daveDestroyLocked();
    }
    {
        std::lock_guard lk(video_mtx_);
        cached_sps_.clear();
        cached_pps_.clear();
    }
    tx_pkts_.store(0);
    audio_tx_pkts_.store(0);
    video_tx_pkts_.store(0);
    rx_pkts_.store(0);
    pli_rx_.store(0);
}

bool VoiceTransport::sendEncryptedRtpLocked(uint8_t payload_type, uint16_t seq, uint32_t ts, uint32_t ssrc, bool marker, const uint8_t* payload, int len) {
    if (!isReady() || sock_ == INVALID_SOCKET || !payload || len <= 0 || len > 1350) return false;

    uint8_t localKey[32];
    bool useXchacha = true;
    {
        std::lock_guard klk(key_mtx_);
        std::memcpy(localKey, key_, 32);
        useXchacha = use_xchacha_;
    }

    uint8_t pkt[1500]{};
    pkt[0] = 0x80;
    pkt[1] = uint8_t((marker ? 0x80 : 0x00) | (payload_type & 0x7F));
    putBE16(pkt + 2, seq);
    putBE32(pkt + 4, ts);
    putBE32(pkt + 8, ssrc);

    uint32_t ctr = nonce_counter_++;
    int totalLen = 0;

    if (useXchacha) {
        uint8_t nonce[24]{};
        std::memcpy(nonce, &ctr, 4);
        unsigned long long clen = 0;
        if (crypto_aead_xchacha20poly1305_ietf_encrypt(
                pkt + 12, &clen,
                payload, size_t(len),
                pkt, 12,
                nullptr, nonce, localKey) != 0) {
            return false;
        }
        std::memcpy(pkt + 12 + clen, &ctr, 4);
        totalLen = int(12 + clen + 4);
    } else {
        uint8_t nonce[24]{};
        std::memcpy(nonce, &ctr, 4);
        if (crypto_secretbox_easy(pkt + 12, payload, size_t(len), nonce, localKey) != 0) return false;
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
    if (!opus || len <= 0 || !isReady()) return false;
    uint32_t mySsrc = ssrc_.load();

    std::vector<uint8_t> daveBuf;
    const uint8_t* payload = opus;
    int payloadLen = len;

    // Apply DAVE E2EE media encryption when active (silence frame 0xF8,0xFF,0xFE stays unencrypted per spec)
    if (!isSilenceFrame(opus, len)) {
        std::lock_guard lk(dave_mtx_);
        if (dave_enc_ && !daveEncryptorIsPassthroughMode(dave_enc_)) {
            if (!daveEncryptorHasKeyRatchet(dave_enc_)) return false; // NEVER leak plaintext when DAVE is mandated!
            size_t maxOut = daveEncryptorGetMaxCiphertextByteSize(dave_enc_, DAVE_MEDIA_TYPE_AUDIO, size_t(len));
            daveBuf.resize(std::max(maxOut, size_t(len) + 64));
            size_t written = 0;
            if (daveEncryptorEncrypt(dave_enc_, DAVE_MEDIA_TYPE_AUDIO, mySsrc, opus, size_t(len),
                                     daveBuf.data(), daveBuf.size(), &written) != DAVE_ENCRYPTOR_RESULT_CODE_SUCCESS || written == 0) {
                return false;
            }
            payload = daveBuf.data();
            payloadLen = int(written);
        }
    }

    std::lock_guard lk(tx_mtx_);
    // Keep RTP audio timestamps strictly monotonic even across sendSilenceFrames()
    if (last_audio_ts_ != 0 && int32_t(timestamp - last_audio_ts_) <= 0) {
        timestamp = last_audio_ts_ + 960;
    }
    last_audio_ts_ = timestamp;
    return sendEncryptedRtpLocked(120, audio_seq_++, timestamp, mySsrc, false, payload, payloadLen);
}

void VoiceTransport::sendSilenceFrames() {
    if (!isReady()) return;
    static const uint8_t kSilence[3] = { 0xF8, 0xFF, 0xFE };
    uint32_t mySsrc = ssrc_.load();
    std::lock_guard lk(tx_mtx_);
    for (int i = 0; i < 5; ++i) {
        last_audio_ts_ += 960;
        sendEncryptedRtpLocked(120, audio_seq_++, last_audio_ts_, mySsrc, false, kSilence, 3);
    }
}

bool VoiceTransport::sendH264Nalu(const uint8_t* nalu, int len, uint32_t video_ssrc, uint32_t timestamp, bool marker) {
    if (!isReady() || !nalu || len <= 0) return false;
    constexpr int kMaxPayload = 1100; // MTU-safe even over PPPoE/VPN (zero IP fragmentation)
    if (len <= kMaxPayload) {
        std::lock_guard lk(tx_mtx_);
        return sendEncryptedRtpLocked(101, video_seq_++, timestamp, video_ssrc, marker, nalu, len);
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
        {
            std::lock_guard lk(tx_mtx_);
            if (!sendEncryptedRtpLocked(101, video_seq_++, timestamp, video_ssrc, marker && last, frag, chunk + 2)) {
                return false;
            }
        }
        offset += chunk;
        // Micro-yield on huge 4K IDR bursts without Windows sleep_for(100us) 1-2ms timer stalls!
        if (totalFrags > 24 && ++burst >= 16 && offset < len) {
            burst = 0;
            SwitchToThread();
        }
    }
    return true;
}

bool VoiceTransport::sendH264AccessUnit(const uint8_t* annexb, int len, uint32_t video_ssrc, uint32_t timestamp) {
    if (!isReady() || !annexb || len < 4 || video_ssrc == 0) return false;

    // 1. Scan raw Annex-B NALUs to cache SPS(7) and PPS(8) separately so PPS-only frames never wipe SPS!
    bool hasSps = false, hasPps = false, hasIdr = false;
    std::vector<uint8_t> newSps, newPps;
    {
        int p = 0;
        while (p + 3 < len) {
            int sc = 0;
            if (annexb[p] == 0 && annexb[p + 1] == 0 && annexb[p + 2] == 1) sc = 3;
            else if (p + 4 <= len && annexb[p] == 0 && annexb[p + 1] == 0 && annexb[p + 2] == 0 && annexb[p + 3] == 1) sc = 4;
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
                if (nt == 7) {
                    hasSps = true;
                    newSps.assign({0, 0, 0, 1});
                    newSps.insert(newSps.end(), annexb + s, annexb + nx);
                } else if (nt == 8) {
                    hasPps = true;
                    newPps.assign({0, 0, 0, 1});
                    newPps.insert(newPps.end(), annexb + s, annexb + nx);
                } else if (nt == 5) {
                    hasIdr = true;
                }
            }
            p = nx;
        }
    }

    std::vector<uint8_t> fullAu;
    const uint8_t* rawPtr = annexb;
    int rawLen = len;
    {
        std::lock_guard lk(video_mtx_);
        if (!newSps.empty()) cached_sps_ = std::move(newSps);
        if (!newPps.empty()) cached_pps_ = std::move(newPps);
        if (hasIdr && (!hasSps || !hasPps) && !cached_sps_.empty() && !cached_pps_.empty()) {
            fullAu.reserve(cached_sps_.size() + cached_pps_.size() + size_t(len));
            if (!hasSps) fullAu.insert(fullAu.end(), cached_sps_.begin(), cached_sps_.end());
            if (!hasPps) fullAu.insert(fullAu.end(), cached_pps_.begin(), cached_pps_.end());
            fullAu.insert(fullAu.end(), annexb, annexb + len);
            rawPtr = fullAu.data();
            rawLen = int(fullAu.size());
        }
    }

    // 2. DAVE E2EE Video Frame Encryption (NEVER transmit plaintext or prematurely switch ratchet before OP 22!)
    const uint8_t* data = rawPtr;
    int curLen = rawLen;
    std::vector<uint8_t> daveBuf;
    {
        std::lock_guard lk(dave_mtx_);
        if (dave_enc_ && !daveEncryptorIsPassthroughMode(dave_enc_)) {
            if (!daveEncryptorHasKeyRatchet(dave_enc_)) return false;
            size_t maxOut = daveEncryptorGetMaxCiphertextByteSize(dave_enc_, DAVE_MEDIA_TYPE_VIDEO, size_t(rawLen));
            daveBuf.resize(std::max(maxOut, size_t(rawLen) + 256));
            size_t written = 0;
            if (daveEncryptorEncrypt(dave_enc_, DAVE_MEDIA_TYPE_VIDEO, video_ssrc, rawPtr, size_t(rawLen),
                                     daveBuf.data(), daveBuf.size(), &written) != DAVE_ENCRYPTOR_RESULT_CODE_SUCCESS || written == 0) {
                return false;
            }
            data = daveBuf.data();
            curLen = int(written);
        }
    }

    // 3. Collect all non-empty NALUs first so marker=true ALWAYS lands on the actual final non-empty NALU!
    std::vector<std::pair<int, int>> nalus;
    nalus.reserve(8);
    int pos = 0;
    while (pos + 3 < curLen) {
        int scLen = 0;
        if (data[pos] == 0 && data[pos + 1] == 0 && data[pos + 2] == 1) scLen = 3;
        else if (pos + 4 <= curLen && data[pos] == 0 && data[pos + 1] == 0 && data[pos + 2] == 0 && data[pos + 3] == 1) scLen = 4;
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
        if (next > start) nalus.emplace_back(start, next - start);
        pos = next;
    }

    if (nalus.empty()) return false;
    for (size_t i = 0; i < nalus.size(); ++i) {
        bool isLast = (i + 1 == nalus.size());
        if (!sendH264Nalu(data + nalus[i].first, nalus[i].second, video_ssrc, timestamp, isLast)) {
            return false;
        }
    }
    return true;
}

void VoiceTransport::rxLoop() {
    HANDLE mm = boostRealtimeThread(L"Pro Audio");
    uint8_t buf[2048];
    uint8_t plain[2048];
    uint8_t daveOut[2048];
    int64_t lastKeepaliveMs = steadyMs();

    while (running_.load()) {
        SOCKET s = INVALID_SOCKET;
        sockaddr_in rem{};
        {
            std::lock_guard lk(tx_mtx_);
            s = sock_;
            rem = remote_;
        }
        if (s == INVALID_SOCKET) break;

        // Periodic 12s UDP NAT keepalive so strict NATs never expire mapping while muted/idle!
        int64_t nowMs = steadyMs();
        if (has_key_.load() && nowMs - lastKeepaliveMs >= 12000) {
            lastKeepaliveMs = nowMs;
            uint8_t ka[8] = { 0x13, 0x37, 0xCA, 0xFE, 0, 0, 0, 1 };
            sendto(s, reinterpret_cast<const char*>(ka), sizeof(ka), 0,
                   reinterpret_cast<sockaddr*>(&rem), sizeof(rem));
        }

        sockaddr_in from{};
        int fromLen = sizeof(from);
        int n = recvfrom(s, reinterpret_cast<char*>(buf), sizeof(buf), 0,
                         reinterpret_cast<sockaddr*>(&from), &fromLen);
        if (n < 12 || !has_key_.load()) continue;
        if (from.sin_addr.s_addr != rem.sin_addr.s_addr) continue; // drop spoofed foreign packets

        uint8_t ptFull = buf[1];
        uint8_t pt = ptFull & 0x7F;
        // Detect incoming RTCP PSFB (PT=206: PLI FMT=1 / FIR FMT=4) -> immediately force an NVENC IDR keyframe for viewers!
        if (ptFull == 206 || ptFull == 205) {
            uint8_t fmt = buf[0] & 0x1F;
            if (ptFull == 206 && (fmt == 1 || fmt == 4)) {
                pli_rx_.fetch_add(1);
                std::function<void()> cb;
                { std::lock_guard clk(cb_mtx_); cb = pli_cb_; }
                if (cb) cb();
            }
            continue;
        }
        if (n < 28 || (pt >= 72 && pt <= 76)) continue; // Other RTCP (SR/RR)

        uint8_t localKey[32];
        bool useXchacha = true;
        {
            std::lock_guard klk(key_mtx_);
            std::memcpy(localKey, key_, 32);
            useXchacha = use_xchacha_;
        }

        uint8_t cc = buf[0] & 0x0F;
        bool ext = (buf[0] & 0x10) != 0;
        bool hasPad = (buf[0] & 0x20) != 0;
        uint16_t seq = getBE16(buf + 2);
        uint32_t ssrc = getBE32(buf + 8);
        uint8_t nonce[24]{};
        std::memcpy(nonce, buf + n - 4, 4);

        unsigned long long plen = 0;
        bool ok = false;
        int audioOff = 0;

        if (useXchacha) {
            int hdrLen = 12 + cc * 4;
            uint16_t extWords = 0;
            if (ext && n > hdrLen + 4) {
                extWords = getBE16(buf + hdrLen + 2);
                hdrLen += 4;
            }
            if (n <= hdrLen + 20) continue;
            int cipherLen = n - hdrLen - 4;
            ok = crypto_aead_xchacha20poly1305_ietf_decrypt(
                     plain, &plen, nullptr,
                     buf + hdrLen, size_t(cipherLen),
                     buf, size_t(hdrLen),
                     nonce, localKey) == 0;
            audioOff = ext ? int(extWords) * 4 : 0;
        } else {
            // Legacy xsalsa20_poly1305_lite: ONLY fixed 12B RTP header is unencrypted! CSRCs & ext header live inside ciphertext!
            int hdrLen = 12;
            if (n <= hdrLen + int(crypto_secretbox_MACBYTES) + 4) continue;
            int cipherLen = n - hdrLen - 4;
            ok = crypto_secretbox_open_easy(plain, buf + hdrLen, size_t(cipherLen), nonce, localKey) == 0;
            plen = size_t(cipherLen - crypto_secretbox_MACBYTES);
            if (ok) {
                audioOff = cc * 4;
                if (ext && audioOff + 4 <= int(plen)) {
                    uint16_t extWords = getBE16(plain + audioOff + 2);
                    audioOff += 4 + int(extWords) * 4;
                }
            }
        }
        if (!ok || plen == 0) continue;

        // Strip RFC 3550 RTP Padding bytes if P bit (0x20) is set!
        if (hasPad && plen > 0) {
            int padLen = int(plain[plen - 1]);
            if (padLen > 0 && padLen <= int(plen)) plen -= size_t(padLen);
        }
        if (audioOff >= int(plen)) continue;

        const uint8_t* opusPtr = plain + audioOff;
        int opusLen = int(plen) - audioOff;

        // Decrypt DAVE E2EE audio frame if active — NEVER pass failed ciphertext to Opus!
        if (pt == 120) {
            bool silence = isSilenceFrame(opusPtr, opusLen);
            if (!silence) {
                std::lock_guard lk(dave_mtx_);
                if (dave_proto_ver_ > 0) {
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
                    // Fallback: if SSRC arrived before OP 5 SPEAKING, probe only ratcheted non-passthrough decryptors!
                    if (!decrypted && itU == ssrc_to_user_.end()) {
                        for (auto& [uid, dec] : user_decryptors_) {
                            if (!dec || !user_has_ratchet_[uid]) continue;
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
                    if (!decrypted) continue; // Drop undecryptable E2EE packet instead of screeching Opus!
                }
            }
        }

        rx_pkts_.fetch_add(1);
        if (pt == 120) {
            AudioRxCallback cb;
            { std::lock_guard clk(cb_mtx_); cb = rx_cb_; }
            if (cb) cb(ssrc, seq, opusPtr, opusLen);
        }
    }
    revertRealtimeThread(mm);
}

} // namespace fc
