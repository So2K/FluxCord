#include "ScreenEngine.h"
#include "NetTurbo.h"
#include <initguid.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <ksmedia.h>
#include <mmdeviceapi.h>
#include <opus.h>
#include <strmif.h>
#include <codecapi.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi1_5.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mftransform.h>
#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#include <cstring>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace fc {

namespace {
std::string wideToUtf8(const wchar_t* w) {
    if (!w || !*w) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1) return {};
    std::string s(size_t(n - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w, -1, s.data(), n, nullptr, nullptr);
    return s;
}

std::wstring utf8ToWide(const char* s) {
    if (!s || !*s) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (n <= 1) return {};
    std::wstring w(size_t(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
}

// Lightweight COM completion handler for Windows 10/11 Process Loopback (captures game/system audio WITHOUT FluxCord voice echo!)
class ProcessLoopbackHandler final : public IActivateAudioInterfaceCompletionHandler, public IAgileObject {
public:
    HANDLE done_ev = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HRESULT result_hr = E_FAIL;
    ComPtr<IAudioClient> client;

    ~ProcessLoopbackHandler() { if (done_ev) CloseHandle(done_ev); }

    ULONG STDMETHODCALLTYPE AddRef() override { return ++ref_; }
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG r = --ref_;
        if (r == 0) delete this;
        return r;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IActivateAudioInterfaceCompletionHandler) || riid == __uuidof(IAgileObject)) {
            *ppv = static_cast<IActivateAudioInterfaceCompletionHandler*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    HRESULT STDMETHODCALLTYPE ActivateCompleted(IActivateAudioInterfaceAsyncOperation* op) override {
        HRESULT hrAct = E_FAIL;
        ComPtr<IUnknown> unk;
        if (op && SUCCEEDED(op->GetActivateResult(&hrAct, &unk)) && SUCCEEDED(hrAct) && unk) {
            result_hr = unk.As(&client);
        } else {
            result_hr = hrAct;
        }
        SetEvent(done_ev);
        return S_OK;
    }
private:
    std::atomic<ULONG> ref_{1};
};

bool tryOpenProcessLoopback(DWORD pid, bool includeTree, ComPtr<IAudioClient>& outClient) {
    if (pid == 0) return false;
    AUDIOCLIENT_ACTIVATION_PARAMS params{};
    params.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
    params.ProcessLoopbackParams.TargetProcessId = pid;
    params.ProcessLoopbackParams.ProcessLoopbackMode = includeTree
        ? PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        : PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE;

    PROPVARIANT pv{};
    pv.vt = VT_BLOB;
    pv.blob.cbSize = sizeof(params);
    pv.blob.pBlobData = reinterpret_cast<BYTE*>(&params);

    auto* handler = new ProcessLoopbackHandler();
    ComPtr<IActivateAudioInterfaceAsyncOperation> op;
    HRESULT hr = ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient), &pv, handler, &op);
    bool ok = false;
    if (SUCCEEDED(hr) && WaitForSingleObject(handler->done_ev, 1500) == WAIT_OBJECT_0 && SUCCEEDED(handler->result_hr) && handler->client) {
        outClient = handler->client;
        ok = true;
    }
    handler->Release();
    return ok;
}

bool createHardwareEncoderMft(int32_t codec, int32_t w, int32_t h, int32_t fps, int32_t bitrateKbps,
                              IMFDXGIDeviceManager* dxgiMgr, ComPtr<IMFTransform>& outMft, std::string& outName) {
    GUID subtype = MFVideoFormat_H264;
    if (codec == FC_CODEC_HEVC) subtype = MFVideoFormat_HEVC;
    else if (codec == FC_CODEC_AV1) subtype = MFVideoFormat_AV1;

    MFT_REGISTER_TYPE_INFO outType{MFMediaType_Video, subtype};
    IMFActivate** activates = nullptr;
    UINT32 count = 0;
    HRESULT hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,
                           MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
                           nullptr, &outType, &activates, &count);
    if (FAILED(hr) || count == 0) {
        if (activates) CoTaskMemFree(activates);
        return false;
    }

    bool ok = false;
    for (UINT32 i = 0; i < count; ++i) {
        LPWSTR friendly = nullptr;
        UINT32 flen = 0;
        if (SUCCEEDED(activates[i]->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, &friendly, &flen)) && friendly) {
            outName = wideToUtf8(friendly);
            CoTaskMemFree(friendly);
        }

        ComPtr<IMFTransform> mft;
        if (SUCCEEDED(activates[i]->ActivateObject(IID_PPV_ARGS(&mft)))) {
            // Unlock async MFT if required
            ComPtr<IMFAttributes> attrs;
            if (SUCCEEDED(mft->GetAttributes(&attrs))) {
                UINT32 isAsync = 0;
                if (SUCCEEDED(attrs->GetUINT32(MF_TRANSFORM_ASYNC, &isAsync)) && isAsync) {
                    attrs->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);
                }
            }

            // Bind D3D11 Device Manager for ZERO-COPY VRAM input!
            hr = mft->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(dxgiMgr));

            // Configure low-latency NVENC parameters via ICodecAPI
            ComPtr<ICodecAPI> capi;
            if (SUCCEEDED(mft.As(&capi))) {
                VARIANT v{};
                v.vt = VT_BOOL; v.boolVal = VARIANT_TRUE;
                capi->SetValue(&CODECAPI_AVLowLatencyMode, &v);

                v.vt = VT_UI4; v.ulVal = eAVEncCommonRateControlMode_CBR;
                capi->SetValue(&CODECAPI_AVEncCommonRateControlMode, &v);

                v.vt = VT_UI4; v.ulVal = UINT32(bitrateKbps) * 1000u;
                capi->SetValue(&CODECAPI_AVEncCommonMeanBitRate, &v);

                v.vt = VT_UI4; v.ulVal = UINT32(bitrateKbps) * 1250u;
                capi->SetValue(&CODECAPI_AVEncCommonMaxBitRate, &v);

                v.vt = VT_UI4; v.ulVal = 15; // Ultra-low-latency NVENC speed tuning
                capi->SetValue(&CODECAPI_AVEncCommonQualityVsSpeed, &v);

                v.vt = VT_UI4; v.ulVal = 0; // Zero B-frames for minimum latency
                capi->SetValue(&CODECAPI_AVEncMPVDefaultBPictureCount, &v);

                v.vt = VT_UI4; v.ulVal = UINT32(fps * 2); // 2-sec GOP
                capi->SetValue(&CODECAPI_AVEncMPVGOPSize, &v);
            }

            // Set output type (H.264 Main Profile / HEVC / AV1)
            ComPtr<IMFMediaType> mtOut;
            MFCreateMediaType(&mtOut);
            mtOut->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            mtOut->SetGUID(MF_MT_SUBTYPE, subtype);
            if (codec == FC_CODEC_H264) {
                mtOut->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Main);
            }
            mtOut->SetUINT32(MF_MT_AVG_BITRATE, UINT32(bitrateKbps) * 1000u);
            mtOut->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            MFSetAttributeSize(mtOut.Get(), MF_MT_FRAME_SIZE, UINT32(w), UINT32(h));
            MFSetAttributeRatio(mtOut.Get(), MF_MT_FRAME_RATE, UINT32(fps), 1);
            MFSetAttributeRatio(mtOut.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

            HRESULT outHr = mft->SetOutputType(0, mtOut.Get(), 0);
            if (FAILED(outHr) && codec == FC_CODEC_H264) {
                mtOut->DeleteItem(MF_MT_MPEG2_PROFILE);
                outHr = mft->SetOutputType(0, mtOut.Get(), 0);
            }
            if (SUCCEEDED(outHr)) {
                ComPtr<IMFMediaType> mtIn;
                MFCreateMediaType(&mtIn);
                mtIn->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                mtIn->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
                mtIn->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
                MFSetAttributeSize(mtIn.Get(), MF_MT_FRAME_SIZE, UINT32(w), UINT32(h));
                MFSetAttributeRatio(mtIn.Get(), MF_MT_FRAME_RATE, UINT32(fps), 1);
                MFSetAttributeRatio(mtIn.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

                if (SUCCEEDED(mft->SetInputType(0, mtIn.Get(), 0))) {
                    mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
                    mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
                    outMft = mft;
                    ok = true;
                    break;
                }
            }
        }
    }

    for (UINT32 i = 0; i < count; ++i) activates[i]->Release();
    CoTaskMemFree(activates);
    return ok;
}
} // namespace

ScreenEngine::ScreenEngine(VoiceTransport* transport) : transport_(transport) {
    if (transport_) {
        transport_->setPliCallback([this] { requestKeyframe(); });
    }
}

ScreenEngine::~ScreenEngine() {
    stop();
}

int32_t ScreenEngine::listMonitors(FcMonitorInfo* out_list, int32_t max_count) {
    if (!out_list || max_count <= 0) return 0;
    ComPtr<IDXGIFactory1> fac;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&fac)))) return 0;

    int32_t count = 0;
    ComPtr<IDXGIAdapter1> ad;
    for (UINT a = 0; fac->EnumAdapters1(a, &ad) != DXGI_ERROR_NOT_FOUND && count < max_count; ++a) {
        ComPtr<IDXGIOutput> out;
        for (UINT o = 0; ad->EnumOutputs(o, &out) != DXGI_ERROR_NOT_FOUND && count < max_count; ++o) {
            DXGI_OUTPUT_DESC od{};
            out->GetDesc(&od);
            if (!od.AttachedToDesktop) continue;
            std::memset(&out_list[count], 0, sizeof(FcMonitorInfo));
            out_list[count].index = count;
            out_list[count].width = od.DesktopCoordinates.right - od.DesktopCoordinates.left;
            out_list[count].height = od.DesktopCoordinates.bottom - od.DesktopCoordinates.top;
            out_list[count].refresh_hz = 60;
            DEVMODEW dm{};
            dm.dmSize = sizeof(dm);
            if (EnumDisplaySettingsW(od.DeviceName, ENUM_CURRENT_SETTINGS, &dm) && dm.dmDisplayFrequency > 0) {
                out_list[count].refresh_hz = int32_t(dm.dmDisplayFrequency);
            }
            std::string name = wideToUtf8(od.DeviceName);
            std::strncpy(out_list[count].name, name.c_str(), sizeof(out_list[count].name) - 1);
            count++;
        }
    }
    return count;
}

bool ScreenEngine::start(int32_t monitor_index, int32_t target_w, int32_t target_h, int32_t fps,
                         int32_t bitrate_kbps, int32_t codec, bool loopback_preview, HWND preview_hwnd,
                         HWND target_hwnd, bool capture_system_audio, const char* render_device_id) {
    stop();
    monitor_idx_ = monitor_index;
    target_w_ = std::clamp(target_w, 640, 3840) & ~7;
    target_h_ = std::clamp(target_h, 360, 2160) & ~7;
    target_fps_ = std::clamp(fps, 15, 120);
    bitrate_kbps_ = std::clamp(bitrate_kbps, 1000, 50000);
    codec_ = codec;
    loopback_preview_ = loopback_preview;
    capture_audio_ = capture_system_audio;
    render_device_id_ = render_device_id ? render_device_id : "";
    preview_hwnd_ = preview_hwnd;
    target_hwnd_ = (target_hwnd && IsWindow(target_hwnd)) ? target_hwnd : nullptr;

    {
        std::lock_guard lk(stats_mtx_);
        std::memset(&stats_, 0, sizeof(stats_));
        stats_.width = target_w_;
        stats_.height = target_h_;
        stats_.running = 1;
    }

    running_.store(true);
    worker_ = std::thread([this] { captureEncodeLoop(); });
    if (capture_audio_ && transport_) {
        audio_worker_ = std::thread([this] { systemAudioLoop(); });
    }
    return true;
}

void ScreenEngine::stop() {
    if (!running_.exchange(false)) return;
    if (worker_.joinable()) worker_.join();
    if (audio_worker_.joinable()) audio_worker_.join();
    std::lock_guard lk(stats_mtx_);
    stats_.running = 0;
}

void ScreenEngine::resizePreview(int32_t width, int32_t height) {
    if (width > 16 && height > 16) {
        pending_w_.store(width);
        pending_h_.store(height);
    }
}

void ScreenEngine::getStats(FcScreenStats* out) const {
    if (!out) return;
    std::lock_guard lk(stats_mtx_);
    *out = stats_;
}

void ScreenEngine::captureEncodeLoop() {
    HANDLE mm = boostRealtimeThread(L"Games");
    SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    if (HDESK desk = OpenInputDesktop(0, FALSE, GENERIC_ALL)) {
        SetThreadDesktop(desk);
        CloseDesktop(desk);
    }
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    MFStartup(MF_VERSION);

    { // Inner scope: ALL COM / DXGI / D3D11 / Media Foundation objects MUST be released BEFORE MFShutdown & CoUninitialize!
    auto setErr = [&](const char* msg) {
        std::lock_guard lk(stats_mtx_);
        std::strncpy(stats_.encoder_name, msg, sizeof(stats_.encoder_name) - 1);
    };

    ComPtr<IDXGIFactory2> fac;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&fac)))) {
        setErr("ERR: CreateDXGIFactory1");
        goto cleanup;
    }

    // Find requested monitor and its owning adapter (RTX 4070 Ti)
    ComPtr<IDXGIAdapter1> chosenAdapter;
    ComPtr<IDXGIOutput1> chosenOutput;
    int32_t idx = 0;
    ComPtr<IDXGIAdapter1> ad;
    for (UINT a = 0; fac->EnumAdapters1(a, &ad) != DXGI_ERROR_NOT_FOUND && !chosenOutput; ++a) {
        ComPtr<IDXGIOutput> out;
        for (UINT o = 0; ad->EnumOutputs(o, &out) != DXGI_ERROR_NOT_FOUND; ++o) {
            DXGI_OUTPUT_DESC od{};
            out->GetDesc(&od);
            if (!od.AttachedToDesktop) continue;
            if (idx == monitor_idx_) {
                chosenAdapter = ad;
                out.As(&chosenOutput);
                break;
            }
            idx++;
        }
    }
    if (!chosenOutput) {
        setErr("ERR: NoMonitor");
        goto cleanup;
    }

    // Create D3D11 device with Video Support + BGRA Support for Zero-Copy GPU pipeline
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    D3D_FEATURE_LEVEL fl{};
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    if (FAILED(D3D11CreateDevice(chosenAdapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags,
                                 nullptr, 0, D3D11_SDK_VERSION, &dev, &fl, &ctx))) {
        setErr("ERR: D3D11CreateDevice");
        goto cleanup;
    }

    // Enable D3D11 multithread protection (required for Media Foundation DXGI Device Manager)
    ComPtr<ID3D11Multithread> mt;
    if (SUCCEEDED(dev.As(&mt))) mt->SetMultithreadProtected(TRUE);

    ComPtr<IDXGIOutputDuplication> dup;
    UINT srcW = UINT(target_w_), srcH = UINT(target_h_);
    const bool windowMode = (target_hwnd_ != nullptr && IsWindow(target_hwnd_));

    // Optional GDI/PrintWindow DIB + dynamic D3D11 BGRA upload texture for per-window capture
    HDC winSrcDc = nullptr, winMemDc = nullptr;
    HBITMAP winDib = nullptr;
    void* winBits = nullptr;
    ComPtr<ID3D11Texture2D> winUploadTex;
    UINT winTexW = 0, winTexH = 0;

    if (!windowMode) {
        HRESULT dupHr = E_FAIL;
        ComPtr<IDXGIOutput5> out5;
        if (SUCCEEDED(chosenOutput.As(&out5))) {
            const DXGI_FORMAT fmts[] = { DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_R10G10B10A2_UNORM };
            dupHr = out5->DuplicateOutput1(dev.Get(), 0, 3, fmts, &dup);
        }
        if (FAILED(dupHr)) {
            dupHr = chosenOutput->DuplicateOutput(dev.Get(), &dup);
        }
        if (FAILED(dupHr)) {
            char ebuf[64];
            std::snprintf(ebuf, sizeof(ebuf), "ERR: DuplicateOutput 0x%08X", unsigned(dupHr));
            setErr(ebuf);
            goto cleanup;
        }
        DXGI_OUTDUPL_DESC dd{};
        dup->GetDesc(&dd);
        srcW = dd.ModeDesc.Width;
        srcH = dd.ModeDesc.Height;
    } else {
        RECT wrc{};
        GetWindowRect(target_hwnd_, &wrc);
        srcW = UINT(std::max<LONG>(64, wrc.right - wrc.left)) & ~1u;
        srcH = UINT(std::max<LONG>(64, wrc.bottom - wrc.top)) & ~1u;
        winSrcDc = GetDC(nullptr);
        winMemDc = CreateCompatibleDC(winSrcDc);
        BITMAPINFO bi{};
        bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = LONG(srcW);
        bi.bmiHeader.biHeight = -LONG(srcH);
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        winDib = CreateDIBSection(winMemDc, &bi, DIB_RGB_COLORS, &winBits, nullptr, 0);
        if (winDib) SelectObject(winMemDc, winDib);
        D3D11_TEXTURE2D_DESC wd{};
        wd.Width = srcW; wd.Height = srcH;
        wd.MipLevels = 1; wd.ArraySize = 1;
        wd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        wd.SampleDesc.Count = 1;
        wd.Usage = D3D11_USAGE_DEFAULT;
        wd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        dev->CreateTexture2D(&wd, nullptr, &winUploadTex);
        winTexW = srcW; winTexH = srcH;
    }

    const UINT dstW = UINT(std::min<int32_t>(target_w_, int32_t(srcW))) & ~1u;
    const UINT dstH = UINT(std::min<int32_t>(target_h_, int32_t(srcH))) & ~1u;

    // Create D3D11 Video Processor for ZERO-COPY BGRA -> NV12 scaling/conversion in VRAM
    ComPtr<ID3D11VideoDevice> vdev;
    ComPtr<ID3D11VideoContext> vctx;
    ComPtr<ID3D11VideoProcessorEnumerator> vpenum;
    ComPtr<ID3D11VideoProcessor> vp;
    bool hasVp = false;
    if (SUCCEEDED(dev.As(&vdev)) && SUCCEEDED(ctx.As(&vctx))) {
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC vdesc{};
        vdesc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        vdesc.InputWidth = srcW; vdesc.InputHeight = srcH;
        vdesc.OutputWidth = dstW; vdesc.OutputHeight = dstH;
        vdesc.Usage = D3D11_VIDEO_USAGE_OPTIMAL_SPEED;
        if (SUCCEEDED(vdev->CreateVideoProcessorEnumerator(&vdesc, &vpenum)) &&
            SUCCEEDED(vdev->CreateVideoProcessor(vpenum.Get(), 0, &vp))) {
            hasVp = true;
        }
    }

    // VRAM-only NV12 surface fed directly to NVENC (0 CPU readback!)
    D3D11_TEXTURE2D_DESC nv12Desc{};
    nv12Desc.Width = dstW;
    nv12Desc.Height = dstH;
    nv12Desc.MipLevels = 1;
    nv12Desc.ArraySize = 1;
    nv12Desc.Format = DXGI_FORMAT_NV12;
    nv12Desc.SampleDesc.Count = 1;
    nv12Desc.Usage = D3D11_USAGE_DEFAULT;
    nv12Desc.BindFlags = D3D11_BIND_RENDER_TARGET;
    ComPtr<ID3D11Texture2D> nv12Tex;
    dev->CreateTexture2D(&nv12Desc, nullptr, &nv12Tex);

    ComPtr<ID3D11VideoProcessorOutputView> vpOutNv12;
    if (hasVp && nv12Tex) {
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd{};
        ovd.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        vdev->CreateVideoProcessorOutputView(nv12Tex.Get(), vpenum.Get(), &ovd, &vpOutNv12);
    }

    // Bind D3D11 device to Media Foundation DXGI Device Manager -> Hardware NVENC MFT
    UINT resetToken = 0;
    ComPtr<IMFDXGIDeviceManager> dxgiMgr;
    MFCreateDXGIDeviceManager(&resetToken, &dxgiMgr);
    dxgiMgr->ResetDevice(dev.Get(), resetToken);

    ComPtr<IMFTransform> encMft;
    ComPtr<ICodecAPI> encCodecApi;
    std::string encName = "DirectX Zero-Copy GPU";
    bool hasHwEnc = createHardwareEncoderMft(codec_, int32_t(dstW), int32_t(dstH), target_fps_, bitrate_kbps_,
                                             dxgiMgr.Get(), encMft, encName);
    if (hasHwEnc && encMft) encMft.As(&encCodecApi);
    ComPtr<IMFMediaEventGenerator> encEvGen;
    bool isAsyncMft = false;
    if (hasHwEnc && encMft) {
        ComPtr<IMFAttributes> ea;
        UINT32 asyncVal = 0;
        if (SUCCEEDED(encMft->GetAttributes(&ea)) && SUCCEEDED(ea->GetUINT32(MF_TRANSFORM_ASYNC, &asyncVal)) && asyncVal) {
            isAsyncMft = SUCCEEDED(encMft.As(&encEvGen));
        }
    }
    auto lastKfTime = std::chrono::steady_clock::now() - std::chrono::seconds(5);
    bool hasLastNv12Frame = false;

    // Optional Zero-Copy DXGI FlipDiscard Preview SwapChain (displays VRAM frame directly without CPU copy!)
    ComPtr<IDXGISwapChain1> previewSwap;
    ComPtr<ID3D11VideoProcessorEnumerator> prevEnum;
    ComPtr<ID3D11VideoProcessor> prevVp;
    if (loopback_preview_ && preview_hwnd_ && IsWindow(preview_hwnd_)) {
        RECT rc{};
        GetClientRect(preview_hwnd_, &rc);
        UINT pw = std::max<UINT>(16, UINT(rc.right - rc.left));
        UINT ph = std::max<UINT>(16, UINT(rc.bottom - rc.top));
        DXGI_SWAP_CHAIN_DESC1 scd{};
        scd.Width = pw; scd.Height = ph;
        scd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        scd.SampleDesc.Count = 1;
        scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        scd.BufferCount = 2;
        scd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        scd.Scaling = DXGI_SCALING_STRETCH;
        fac->CreateSwapChainForHwnd(dev.Get(), preview_hwnd_, &scd, nullptr, nullptr, &previewSwap);

        if (vdev) {
            D3D11_VIDEO_PROCESSOR_CONTENT_DESC pcd{};
            pcd.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
            pcd.InputWidth = dstW; pcd.InputHeight = dstH;
            pcd.OutputWidth = pw; pcd.OutputHeight = ph;
            pcd.Usage = D3D11_VIDEO_USAGE_OPTIMAL_SPEED;
            if (SUCCEEDED(vdev->CreateVideoProcessorEnumerator(&pcd, &prevEnum))) {
                vdev->CreateVideoProcessor(prevEnum.Get(), 0, &prevVp);
            }
        }
    }

    {
        std::lock_guard lk(stats_mtx_);
        stats_.width = int32_t(dstW);
        stats_.height = int32_t(dstH);
        stats_.zero_copy = (hasVp && nv12Tex) ? 1 : 0;
        std::strncpy(stats_.encoder_name, encName.c_str(), sizeof(stats_.encoder_name) - 1);
    }

    const int64_t framePeriodUs = 1000000 / target_fps_;
    int64_t frameCount = 0;
    int64_t skippedStatic = 0;
    int64_t bytesWindow = 0;
    int64_t framesWindow = 0;
    auto windowStart = std::chrono::steady_clock::now();
    auto streamEpoch = windowStart;
    const int64_t sampleDurHns = 10000000 / target_fps_;
    int asyncNeedInput = 0;

    while (running_.load()) {
        auto tStart = std::chrono::steady_clock::now();

        // Handle preview resize without recreating pipeline
        int32_t rw = pending_w_.exchange(0);
        int32_t rh = pending_h_.exchange(0);
        if (previewSwap && rw > 16 && rh > 16) {
            previewSwap->ResizeBuffers(2, UINT(rw), UINT(rh), DXGI_FORMAT_B8G8R8A8_UNORM, 0);
        }

        ComPtr<ID3D11Texture2D> deskTex;
        bool acquiredDup = false;
        bool reencodeStaticKf = false;
        if (!windowMode) {
            DXGI_OUTDUPL_FRAME_INFO fi{};
            ComPtr<IDXGIResource> res;
            HRESULT hr = dup->AcquireNextFrame(uint32_t(std::max<int64_t>(4, framePeriodUs / 1000)), &fi, &res);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) {
                skippedStatic++;
                std::lock_guard lk(stats_mtx_);
                stats_.frames_skipped_static = skippedStatic;
                // If a viewer just requested a keyframe (PLI / join) or 2s elapsed while desktop is static,
                // re-encode the cached VRAM NV12 surface as an IDR keyframe so viewers NEVER spin on a static screen!
                auto nowKf = std::chrono::steady_clock::now();
                bool periodicKf = hasLastNv12Frame && (nowKf - lastKfTime >= std::chrono::seconds(2));
                if (hasLastNv12Frame && (force_keyframe_.load() || periodicKf)) {
                    reencodeStaticKf = true;
                } else {
                    continue;
                }
            } else if (FAILED(hr)) {
                std::this_thread::sleep_for(std::chrono::milliseconds(16));
                continue;
            } else {
                res.As(&deskTex);
                acquiredDup = true;
            }
        } else {
            if (!IsWindow(target_hwnd_) || !winMemDc || !winBits || !winUploadTex) {
                std::this_thread::sleep_for(std::chrono::milliseconds(30));
                continue;
            }
            if (PrintWindow(target_hwnd_, winMemDc, 2)) {
                ctx->UpdateSubresource(winUploadTex.Get(), 0, nullptr, winBits, winTexW * 4, 0);
                deskTex = winUploadTex;
            } else {
                std::this_thread::sleep_for(std::chrono::milliseconds(16));
                continue;
            }
        }

        float encMs = 0.0f;
        float decMs = 0.0f;

        if ((deskTex || reencodeStaticKf) && hasVp && nv12Tex && vpOutNv12) {
            auto tEnc0 = std::chrono::steady_clock::now();

            // 1. ZERO-COPY GPU Color Space Conversion & Scaling: Desktop BGRA -> NV12 strictly in VRAM
            if (deskTex) {
                D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd{};
                ivd.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
                ComPtr<ID3D11VideoProcessorInputView> vpIn;
                if (SUCCEEDED(vdev->CreateVideoProcessorInputView(deskTex.Get(), vpenum.Get(), &ivd, &vpIn))) {
                    D3D11_VIDEO_PROCESSOR_STREAM st{};
                    st.Enable = TRUE;
                    st.pInputSurface = vpIn.Get();
                    vctx->VideoProcessorBlt(vp.Get(), vpOutNv12.Get(), 0, 1, &st);
                    hasLastNv12Frame = true;
                }
            }

            // 2. Force IDR Keyframe on viewer join / RTCP PLI (throttled >=500ms so PLI floods never starve P-frames!) or every 2.0s
            auto nowKf2 = std::chrono::steady_clock::now();
            bool wantKf = force_keyframe_.load() && (nowKf2 - lastKfTime >= std::chrono::milliseconds(500));
            bool periodicKf2 = (nowKf2 - lastKfTime >= std::chrono::seconds(2));
            if (encCodecApi && (wantKf || periodicKf2)) {
                force_keyframe_.store(false);
                VARIANT kv{};
                kv.vt = VT_UI4;
                kv.ulVal = 1;
                encCodecApi->SetValue(&CODECAPI_AVEncVideoForceKeyFrame, &kv);
                lastKfTime = nowKf2;
            }

            // 3. Feed VRAM NV12 surface directly to Hardware NVENC MFT (ZERO CPU COPY!)
            if (hasHwEnc && encMft) {
                ComPtr<IMFSample> inSample;
                ComPtr<IMFMediaBuffer> dxBuf;
                if (SUCCEEDED(MFCreateSample(&inSample)) &&
                    SUCCEEDED(MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), nv12Tex.Get(), 0, FALSE, &dxBuf))) {
                    int64_t elapsedUs = std::chrono::duration_cast<std::chrono::microseconds>(
                        std::chrono::steady_clock::now() - streamEpoch).count();
                    int64_t sampleTimeHns = elapsedUs * 10; // exact monotonic 100ns units
                    inSample->AddBuffer(dxBuf.Get());
                    inSample->SetSampleTime(sampleTimeHns);
                    inSample->SetSampleDuration(sampleDurHns);

                    if (isAsyncMft && encEvGen) {
                        // Drain all pending Async NVENC events without blocking
                        for (int spin = 0; spin < 16; ++spin) {
                            ComPtr<IMFMediaEvent> ev;
                            HRESULT ehr = encEvGen->GetEvent(MF_EVENT_FLAG_NO_WAIT, &ev);
                            if (ehr == MF_E_NO_EVENTS_AVAILABLE || FAILED(ehr)) break;
                            MediaEventType met = MEUnknown;
                            ev->GetType(&met);
                            if (met == METransformNeedInput) {
                                asyncNeedInput++;
                            } else if (met == METransformHaveOutput) {
                                MFT_OUTPUT_DATA_BUFFER outBuf{};
                                MFT_OUTPUT_STREAM_INFO osi{};
                                encMft->GetOutputStreamInfo(0, &osi);
                                ComPtr<IMFSample> outSample;
                                if (!(osi.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)) {
                                    MFCreateSample(&outSample);
                                    ComPtr<IMFMediaBuffer> mb;
                                    MFCreateMemoryBuffer(std::max<DWORD>(osi.cbSize, 1024 * 512), &mb);
                                    outSample->AddBuffer(mb.Get());
                                    outBuf.pSample = outSample.Get();
                                }
                                DWORD status = 0;
                                if (SUCCEEDED(encMft->ProcessOutput(0, 1, &outBuf, &status)) && outBuf.pSample) {
                                    ComPtr<IMFMediaBuffer> cb;
                                    if (SUCCEEDED(outBuf.pSample->ConvertToContiguousBuffer(&cb))) {
                                        BYTE* data = nullptr;
                                        DWORD maxLen = 0, curLen = 0;
                                        if (SUCCEEDED(cb->Lock(&data, &maxLen, &curLen)) && curLen > 0) {
                                            bytesWindow += curLen;
                                            uint32_t vssrc = video_ssrc_.load();
                                            if (transport_ && transport_->isReady() && vssrc != 0) {
                                                LONGLONG outHns = sampleTimeHns;
                                                outBuf.pSample->GetSampleTime(&outHns);
                                                uint32_t rtpTs = uint32_t((outHns * 9) / 1000); // exact 90 kHz wall-clock RTP timestamp
                                                transport_->sendH264AccessUnit(data, int(curLen), vssrc, rtpTs);
                                            }
                                            cb->Unlock();
                                        }
                                    }
                                    if (osi.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) outBuf.pSample->Release();
                                }
                                if (outBuf.pEvents) outBuf.pEvents->Release();
                            }
                        }
                        if (asyncNeedInput > 0) {
                            if (SUCCEEDED(encMft->ProcessInput(0, inSample.Get(), 0))) asyncNeedInput--;
                        }
                    } else if (SUCCEEDED(encMft->ProcessInput(0, inSample.Get(), 0))) {
                        MFT_OUTPUT_DATA_BUFFER outBuf{};
                        MFT_OUTPUT_STREAM_INFO osi{};
                        encMft->GetOutputStreamInfo(0, &osi);
                        ComPtr<IMFSample> outSample;
                        if (!(osi.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)) {
                            MFCreateSample(&outSample);
                            ComPtr<IMFMediaBuffer> mb;
                            MFCreateMemoryBuffer(std::max<DWORD>(osi.cbSize, 1024 * 512), &mb);
                            outSample->AddBuffer(mb.Get());
                            outBuf.pSample = outSample.Get();
                        }
                        DWORD status = 0;
                        HRESULT ohr = encMft->ProcessOutput(0, 1, &outBuf, &status);
                        if (SUCCEEDED(ohr) && outBuf.pSample) {
                            ComPtr<IMFMediaBuffer> cb;
                            if (SUCCEEDED(outBuf.pSample->ConvertToContiguousBuffer(&cb))) {
                                BYTE* data = nullptr;
                                DWORD maxLen = 0, curLen = 0;
                                if (SUCCEEDED(cb->Lock(&data, &maxLen, &curLen)) && curLen > 0) {
                                    bytesWindow += curLen;
                                    uint32_t vssrc = video_ssrc_.load();
                                    if (transport_ && transport_->isReady() && vssrc != 0) {
                                        transport_->sendH264AccessUnit(data, int(curLen), vssrc, uint32_t((sampleTimeHns * 9) / 1000));
                                    }
                                    cb->Unlock();
                                }
                            }
                            if (osi.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) {
                                outBuf.pSample->Release();
                            }
                        }
                    }
                }
            }
            auto tEnc1 = std::chrono::steady_clock::now();
            encMs = std::chrono::duration<float, std::milli>(tEnc1 - tEnc0).count();

            // 3. ZERO-COPY Preview Present directly from VRAM NV12 -> DXGI FlipDiscard SwapChain
            if (previewSwap && prevVp && prevEnum) {
                auto tDec0 = std::chrono::steady_clock::now();
                ComPtr<ID3D11Texture2D> backBuf;
                if (SUCCEEDED(previewSwap->GetBuffer(0, IID_PPV_ARGS(&backBuf)))) {
                    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC piv{};
                    piv.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
                    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC pov{};
                    pov.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
                    ComPtr<ID3D11VideoProcessorInputView> pin;
                    ComPtr<ID3D11VideoProcessorOutputView> pout;
                    if (SUCCEEDED(vdev->CreateVideoProcessorInputView(nv12Tex.Get(), prevEnum.Get(), &piv, &pin)) &&
                        SUCCEEDED(vdev->CreateVideoProcessorOutputView(backBuf.Get(), prevEnum.Get(), &pov, &pout))) {
                        D3D11_VIDEO_PROCESSOR_STREAM pst{};
                        pst.Enable = TRUE;
                        pst.pInputSurface = pin.Get();
                        vctx->VideoProcessorBlt(prevVp.Get(), pout.Get(), 0, 1, &pst);
                        previewSwap->Present(0, 0);
                    }
                }
                auto tDec1 = std::chrono::steady_clock::now();
                decMs = std::chrono::duration<float, std::milli>(tDec1 - tDec0).count();
            }
        }

        if (acquiredDup && dup) dup->ReleaseFrame();
        frameCount++;
        framesWindow++;

        auto now = std::chrono::steady_clock::now();
        float elapsedSec = std::chrono::duration<float>(now - windowStart).count();
        if (elapsedSec >= 0.5f) {
            std::lock_guard lk(stats_mtx_);
            stats_.fps = float(framesWindow) / elapsedSec;
            stats_.bitrate_kbps = (float(bytesWindow) * 8.0f / 1000.0f) / elapsedSec;
            stats_.encode_ms = encMs;
            stats_.decode_ms = decMs;
            stats_.frames_encoded = frameCount;
            stats_.frames_skipped_static = skippedStatic;
            bytesWindow = 0;
            framesWindow = 0;
            windowStart = now;
        }

        auto spentUs = std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - tStart).count();
        if (spentUs < framePeriodUs) {
            std::this_thread::sleep_for(std::chrono::microseconds(framePeriodUs - spentUs));
        }
    }

    if (encMft) {
        encMft->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        encMft->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
    }
    encEvGen.Reset();
    encCodecApi.Reset();
    encMft.Reset();
    dxgiMgr.Reset();
    previewSwap.Reset();
    prevVp.Reset();
    prevEnum.Reset();
    vpOutNv12.Reset();
    nv12Tex.Reset();
    vp.Reset();
    vpenum.Reset();
    dup.Reset();
    winUploadTex.Reset();
    if (winDib) DeleteObject(winDib);
    if (winMemDc) DeleteDC(winMemDc);
    if (winSrcDc) ReleaseDC(nullptr, winSrcDc);
    } // End inner COM scope

cleanup:
    MFShutdown();
    CoUninitialize();
    revertRealtimeThread(mm);
}

// Dedicated WASAPI Process-Loopback (zero voice echo!) + Device-Loopback fallback for Go Live System Audio TX (48kHz Stereo Opus 128kbps + DAVE!)
void ScreenEngine::systemAudioLoop() {
    HANDLE mm = boostRealtimeThread(L"Pro Audio");
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    {
        int opErr = 0;
        OpusEncoder* enc = opus_encoder_create(48000, 2, OPUS_APPLICATION_AUDIO, &opErr);
        if (enc) {
            opus_encoder_ctl(enc, OPUS_SET_BITRATE(128000));
            opus_encoder_ctl(enc, OPUS_SET_SIGNAL(OPUS_SIGNAL_MUSIC));
            opus_encoder_ctl(enc, OPUS_SET_INBAND_FEC(1));
            opus_encoder_ctl(enc, OPUS_SET_PACKET_LOSS_PERC(5));
            opus_encoder_ctl(enc, OPUS_SET_DTX(0));
            opus_encoder_ctl(enc, OPUS_SET_COMPLEXITY(8));
        }

        ComPtr<IAudioClient> ac;
        ComPtr<IAudioCaptureClient> cap;
        WAVEFORMATEX* mixWf = nullptr;
        WAVEFORMATEX procWf{};
        bool started = false;
        uint32_t srcRate = 48000;
        uint32_t srcCh = 2;
        int sampleFmt = 0; // 0=float32, 1=int16, 2=int32

        // Priority 1: Windows 10 20348+ / Win11 Process Loopback (captures target app OR all system audio EXCLUDING FluxCord voice chat -> ZERO ECHO!)
        if (enc) {
            DWORD targetPid = 0;
            bool includeTree = false;
            if (target_hwnd_ && IsWindow(target_hwnd_)) {
                GetWindowThreadProcessId(target_hwnd_, &targetPid);
                if (targetPid != 0 && targetPid != GetCurrentProcessId()) includeTree = true;
            }
            if (!includeTree) targetPid = GetCurrentProcessId();

            ComPtr<IAudioClient> procAc;
            if (tryOpenProcessLoopback(targetPid, includeTree, procAc) && procAc) {
                procWf.wFormatTag = WAVE_FORMAT_IEEE_FLOAT;
                procWf.nChannels = 2;
                procWf.nSamplesPerSec = 48000;
                procWf.wBitsPerSample = 32;
                procWf.nBlockAlign = 8;
                procWf.nAvgBytesPerSec = 48000 * 8;
                DWORD flags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                if (SUCCEEDED(procAc->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 200000, 0, &procWf, nullptr)) &&
                    SUCCEEDED(procAc->GetService(IID_PPV_ARGS(&cap))) &&
                    SUCCEEDED(procAc->Start())) {
                    ac = procAc;
                    srcRate = 48000;
                    srcCh = 2;
                    sampleFmt = 0;
                    started = true;
                } else {
                    // Retry with 16-bit PCM 48kHz stereo (some builds prefer PCM on virtual loopback)
                    procAc.Reset();
                    if (tryOpenProcessLoopback(targetPid, includeTree, procAc) && procAc) {
                        procWf.wFormatTag = WAVE_FORMAT_PCM;
                        procWf.nChannels = 2;
                        procWf.nSamplesPerSec = 48000;
                        procWf.wBitsPerSample = 16;
                        procWf.nBlockAlign = 4;
                        procWf.nAvgBytesPerSec = 48000 * 4;
                        if (SUCCEEDED(procAc->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 200000, 0, &procWf, nullptr)) &&
                            SUCCEEDED(procAc->GetService(IID_PPV_ARGS(&cap))) &&
                            SUCCEEDED(procAc->Start())) {
                            ac = procAc;
                            srcRate = 48000;
                            srcCh = 2;
                            sampleFmt = 1;
                            started = true;
                        }
                    }
                }
            }
        }

        // Priority 2: Bulletproof classic WASAPI Render Endpoint Loopback (uses user's selected DAC e.g. Schiit USB Gen 2, or Windows default!)
        if (enc && !started) {
            ComPtr<IMMDeviceEnumerator> en;
            ComPtr<IMMDevice> dev;
            if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&en)))) {
                if (!render_device_id_.empty()) {
                    std::wstring wid = utf8ToWide(render_device_id_.c_str());
                    if (FAILED(en->GetDevice(wid.c_str(), &dev))) dev.Reset();
                }
                if (!dev) en->GetDefaultAudioEndpoint(eRender, eConsole, &dev);
                if (dev &&
                    SUCCEEDED(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(ac.GetAddressOf()))) &&
                    SUCCEEDED(ac->GetMixFormat(&mixWf)) && mixWf) {
                    if (SUCCEEDED(ac->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 200000, 0, mixWf, nullptr)) &&
                        SUCCEEDED(ac->GetService(IID_PPV_ARGS(&cap))) &&
                        SUCCEEDED(ac->Start())) {
                        srcRate = std::max<uint32_t>(8000, mixWf->nSamplesPerSec);
                        srcCh = std::max<uint32_t>(1, mixWf->nChannels);
                        bool isFloat = (mixWf->wFormatTag == WAVE_FORMAT_IEEE_FLOAT);
                        if (mixWf->wFormatTag == WAVE_FORMAT_EXTENSIBLE && mixWf->cbSize >= 22) {
                            const auto* ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(mixWf);
                            isFloat = (ext->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
                        }
                        if (isFloat && mixWf->wBitsPerSample == 32) sampleFmt = 0;
                        else if (mixWf->wBitsPerSample == 16) sampleFmt = 1;
                        else sampleFmt = 2; // 32-bit int PCM
                        started = true;
                    }
                }
            }
        }

        if (enc && started && ac && cap) {
            std::vector<float> fifo;
            fifo.reserve(4096 * 2);
            uint32_t rtpTs = 0;
            uint8_t opusBuf[1200];
            bool announcedInitial = false;
            int activeHangFrames = 0;

            auto readSample = [&](const BYTE* base, uint32_t frameIdx, uint32_t chIdx) -> float {
                uint32_t idx = frameIdx * srcCh + std::min(chIdx, srcCh - 1);
                if (sampleFmt == 0) return reinterpret_cast<const float*>(base)[idx];
                if (sampleFmt == 1) return float(reinterpret_cast<const int16_t*>(base)[idx]) * (1.0f / 32768.0f);
                return float(reinterpret_cast<const int32_t*>(base)[idx]) * (1.0f / 2147483648.0f);
            };

            while (running_.load()) {
                // As soon as PubGW transport becomes ready, emit initial silence frames so Discord SFU opens our stream audio_ssrc immediately!
                if (!announcedInitial && transport_ && transport_->isReady()) {
                    transport_->sendSilenceFrames();
                    announcedInitial = true;
                }

                UINT32 pktLen = 0;
                if (FAILED(cap->GetNextPacketSize(&pktLen)) || pktLen == 0) {
                    std::this_thread::sleep_for(std::chrono::milliseconds(3));
                    continue;
                }
                BYTE* data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                if (SUCCEEDED(cap->GetBuffer(&data, &frames, &flags, nullptr, nullptr))) {
                    if (frames > 0) {
                        double ratio = 48000.0 / double(srcRate);
                        uint32_t outF = (srcRate == 48000) ? frames : uint32_t(double(frames) * ratio);
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) || !data) {
                            fifo.insert(fifo.end(), size_t(outF) * 2, 0.0f);
                        } else if (srcRate == 48000 && sampleFmt == 0 && srcCh == 2) {
                            // Zero-overhead fast path (Process Loopback 48kHz stereo float32)
                            const float* fp = reinterpret_cast<const float*>(data);
                            fifo.insert(fifo.end(), fp, fp + size_t(frames) * 2);
                        } else {
                            for (uint32_t i = 0; i < outF; ++i) {
                                double srcPos = double(i) / ratio;
                                uint32_t s0 = std::min<uint32_t>(frames - 1, uint32_t(srcPos));
                                uint32_t s1 = std::min<uint32_t>(frames - 1, s0 + 1);
                                float frac = float(srcPos - double(s0));
                                float l = readSample(data, s0, 0) * (1.0f - frac) + readSample(data, s1, 0) * frac;
                                float r = (srcCh >= 2)
                                    ? (readSample(data, s0, 1) * (1.0f - frac) + readSample(data, s1, 1) * frac)
                                    : l;
                                fifo.push_back(l);
                                fifo.push_back(r);
                            }
                        }
                    }
                    cap->ReleaseBuffer(frames);
                }

                while (fifo.size() >= 960 * 2) {
                    float energy = 0.0f;
                    for (int i = 0; i < 960 * 2; ++i) energy += fifo[size_t(i)] * fifo[size_t(i)];
                    bool hasAudio = (energy > 1e-8f);
                    if (hasAudio) activeHangFrames = 25; // 500ms smooth hang so quiet game scenes NEVER clip!
                    else if (activeHangFrames > 0) activeHangFrames--;

                    if (transport_ && transport_->isReady()) {
                        if (activeHangFrames > 0) {
                            int n = opus_encode_float(enc, fifo.data(), 960, opusBuf, sizeof(opusBuf));
                            if (n > 2) transport_->sendOpus(opusBuf, n, rtpTs);
                            if (activeHangFrames == 1 && !hasAudio) transport_->sendSilenceFrames();
                        }
                    }
                    rtpTs += 960;
                    fifo.erase(fifo.begin(), fifo.begin() + 960 * 2);
                }
            }
            ac->Stop();
        }
        if (mixWf) CoTaskMemFree(mixWf);
        if (enc) opus_encoder_destroy(enc);
    }
    CoUninitialize();
    revertRealtimeThread(mm);
}

} // namespace fc
