using System.Diagnostics;

namespace FluxCord;

public static unsafe partial class Shell
{
    public const float TopH = 38f, RailW = 68f, SideW = 252f, InputH = 54f;

    // Exact verified Lucide font codepoints from Assets/fonts/lucide.ttf
    internal const string IcCheck = "\ue070";
    internal const string IcChevDown = "\ue071";
    internal const string IcChevRight = "\ue073";
    internal const string IcChevUp = "\ue074";
    internal const string IcCopy = "\ue0a2";
    internal const string IcEye = "\ue0be";
    internal const string IcHash = "\ue0f2";
    internal const string IcHeadphoneOff = "\ue62d";
    internal const string IcHeadphones = "\ue0f4";
    internal const string IcImage = "\ue0f9";
    internal const string IcGrid = "\ue104";
    internal const string IcMax = "\ue116";
    internal const string IcChat = "\ue11a";
    internal const string IcMic = "\ue11b";
    internal const string IcMicOff = "\ue11c";
    internal const string IcMin = "\ue11e";
    internal const string IcMonitor = "\ue120";
    internal const string IcMonitorOff = "\ue1db";
    internal const string IcPhoneOff = "\ue13b";
    internal const string IcPlus = "\ue140";
    internal const string IcRefresh = "\ue148";
    internal const string IcShare = "\ue152";
    internal const string IcShareOff = "\ue153";
    internal const string IcSettings = "\ue157";
    internal const string IcSliders = "\ue299";
    internal const string IcVideoOff = "\ue1a5";
    internal const string IcVol = "\ue1aa";
    internal const string IcVolOff = "\ue1ab";
    internal const string IcX = "\ue1b1";
    internal const string IcZap = "\ue1b3";

    private static bool _showSettings;
    private static bool _showQuickSwitch;
    private static bool _showShareModal;
    private static bool _showChannelSearch;
    private static bool _showVoiceInfo;
    private static int _voiceInfoTab; // 0 = Интеграция (Ping Graph + DAVE), 1 = Серверы ⚡ (RTC Turbo Regions)
    private static bool _showStatusMenu;
    private static bool _showEmojiPicker;
    private static bool _showPinsPopup;
    private static bool _showFriendsView;
    private static int _friendsTab; // 0 = В сети, 1 = Все
    private static string? _lightboxUrl;
    private static string? _profileUserId;
    private static string _profileGuildId = "";
    private static float _profileX, _profileY;
    private static readonly HashSet<string> _collapsedCats = new();
    private static int _activeStreamMonitor;
    private static ulong _activeStreamHwnd;
    private static long _lastClockSec;
    private static bool _prevLocalSpkPoll;
    private static int _prevPeerSpkHash;
    private static bool _inVoiceStage;
    public static bool IsInVoiceStage => _inVoiceStage;
    private static bool _muted, _deafened, _loopback;
    private static bool _streaming;
    private static long _voiceJoinedTick;
    private static float _railScroll, _chatScroll, _sideScroll, _memberScroll;
    private static string _hoverGuildTip = "";
    private static float _hoverGuildTipY;
    private static string _replyToId = "", _replyToLabel = "";
    private static string _editingMsgId = "", _editingOrigText = "";
    private static string _searchQuery = "";
    private static bool _pttPrevDown;

    // Sidebar Hover Stream Preview Card (exact GoofCord/Discord c_070.jpg!)
    private static VoiceMember? _hoverStreamVm;
    private static string _hoverStreamGuildId = "";
    private static float _hoverStreamY;
    private static long _hoverStreamLastTick;

    // Context Menus & Quick Audio Popup
    private static VoiceMember? _ctxUser;
    private static string _ctxGuildId = "";
    private static float _ctxX, _ctxY;

    private static string? _ctxStreamUserId; // "self" or remote streamer userId
    private static float _ctxStreamX, _ctxStreamY;

    // 0 = closed, 1 = full audio quick popup (mic chevron), 2 = output devices quick popup (headphones chevron)
    private static int _audioPopupMode;
    private static int _audioPopupSub; // 0 = none, 1 = input list flyout, 2 = output list flyout
    private static float _audioPopupX, _audioPopupY;

    private static string _toast = "";
    private static long _toastUntil;
    private static long _lastStatsTick;

    private static FcAudioStats _audioStats;
    private static float _localVoiceLevel, _localSpeakAnim;
    private static readonly Dictionary<string, float> _peerSpeakAnim = new();
    private static FcScreenStats _selfScreenStats;
    private static List<(string Id, string Name, bool Default)> _capDevices = [];
    private static List<(string Id, string Name, bool Default)> _renDevices = [];
    private static List<(int Index, int W, int H, int Hz, string Name)> _monitors = [];

    private static R _chatInputRect, _tokenInputRect, _searchInputRect;
    private static bool _wantChatInput, _wantTokenInput, _wantSearchInput;
    private static readonly (bool Show, R Rect)[] _watchSlotsLayout = new (bool, R)[App.MaxWatchSlots];
    private static bool _wantSelfHost;
    private static R _selfHostRect;
    private static readonly List<R> _overlayCutouts = new(12);

    public static void ToggleQuickSwitch()
    {
        _showQuickSwitch = !_showQuickSwitch;
        if (_showQuickSwitch)
        {
            _showChannelSearch = false; _showSettings = false; _showShareModal = false;
            _searchQuery = ""; App.SetSearchInputText("");
        }
        App.Invalidate();
    }

    public static void ToggleChannelSearch()
    {
        _showChannelSearch = !_showChannelSearch;
        if (_showChannelSearch)
        {
            _showQuickSwitch = false; _inVoiceStage = false;
            _searchQuery = ""; App.SetSearchInputText("");
        }
        else _searchQuery = "";
        App.Invalidate();
    }

    public static void OnSearchChanged(string text)
    {
        _searchQuery = text.Trim();
        App.Invalidate();
    }

    public static void OnSearchEnter(string text)
    {
        _searchQuery = text.Trim();
        if (_showQuickSwitch)
        {
            var dc = DiscordClient.Instance;
            string q = _searchQuery.ToLowerInvariant();
            foreach (var g in dc.Guilds)
            {
                foreach (var ch in g.Channels)
                {
                    if (ch.IsCategory) continue;
                    if (string.IsNullOrEmpty(q) || ch.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || g.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        dc.ActiveGuildId = g.Id;
                        if (ch.IsVoice) { _ = dc.JoinVoiceAsync(g.Id, ch.Id); _inVoiceStage = true; }
                        else { dc.ActiveChannelId = ch.Id; _inVoiceStage = false; _ = dc.LoadMessagesAsync(ch.Id); }
                        _showQuickSwitch = false;
                        App.Invalidate();
                        return;
                    }
                }
            }
            foreach (var dm in dc.DmChannels)
            {
                if (string.IsNullOrEmpty(q) || dm.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    dc.ActiveGuildId = "@me"; dc.ActiveChannelId = dm.Id; _inVoiceStage = false;
                    _ = dc.LoadMessagesAsync(dm.Id);
                    _showQuickSwitch = false;
                    App.Invalidate();
                    return;
                }
            }
        }
        App.Invalidate();
    }

    public static void OnFilesDropped(List<string> files)
    {
        var dc = DiscordClient.Instance;
        if (files.Count == 0 || string.IsNullOrEmpty(dc.ActiveChannelId)) return;
        foreach (var fp in files)
        {
            if (File.Exists(fp))
            {
                Toast("Загрузка " + Path.GetFileName(fp) + "...");
                _ = dc.UploadFileAsync(dc.ActiveChannelId, fp);
            }
        }
    }

    public static void OnClipboardImagePaste()
    {
        var dc = DiscordClient.Instance;
        if (string.IsNullOrEmpty(dc.ActiveChannelId)) return;
        var tmp = W.SaveClipboardImageToTemp(App.Hwnd);
        if (!string.IsNullOrEmpty(tmp))
        {
            Toast("Отправка скриншота из буфера...");
            _ = ShellAsync.UploadAndDeleteTempAsync(dc.ActiveChannelId, tmp);
        }
    }

    public static void ToggleMute()
    {
        _muted = !_muted;
        ApplyAudioSettings();
        SoundFx.Play(_muted ? UiSound.Mute : UiSound.Unmute);
        App.Invalidate();
    }

    public static void ToggleDeafen()
    {
        _deafened = !_deafened;
        ApplyAudioSettings();
        SoundFx.Play(_deafened ? UiSound.Deafen : UiSound.Undeafen);
        App.Invalidate();
    }

    public static void OnChatTyping()
    {
        var dc = DiscordClient.Instance;
        if (!string.IsNullOrEmpty(dc.ActiveChannelId) && !_inVoiceStage)
            _ = dc.SendTypingAsync(dc.ActiveChannelId);
    }

    public static void StepChannel(int dir)
    {
        var dc = DiscordClient.Instance;
        var g = dc.Guilds.FirstOrDefault(x => x.Id == dc.ActiveGuildId);
        var list = dc.ActiveGuildId == "@me"
            ? dc.DmChannels
            : (g?.Channels.Where(c => !c.IsCategory && !c.IsVoice).ToList() ?? []);
        if (list.Count == 0) return;
        int cur = list.FindIndex(c => c.Id == dc.ActiveChannelId);
        int next = cur < 0 ? 0 : (cur + dir + list.Count) % list.Count;
        dc.ActiveChannelId = list[next].Id;
        _inVoiceStage = false;
        _showFriendsView = false;
        _chatScroll = 0;
        _ = dc.LoadMessagesAsync(dc.ActiveChannelId);
        App.Invalidate();
    }

    // 100ms background poll: updates audio/video telemetry & speaking states while keeping idle D2D CPU near 0%!
    public static bool PollBackgroundTick()
    {
        var dc = DiscordClient.Instance;
        long now = Environment.TickCount64;
        bool needRedraw = false;

        if (SettingsStore.Current.PushToTalk && dc.VoiceConnected)
        {
            bool pttDown = W.AsyncKeyDown(W.VK_RCONTROL);
            if (pttDown != _pttPrevDown)
            {
                _pttPrevDown = pttDown;
                MediaNative.fc_voice_set_muted((_muted || !pttDown) ? 1 : 0);
                needRedraw = true;
            }
        }

        bool wantStats = _streaming || dc.VoiceConnected || dc.IsWatchingAnyStream || _loopback || _showSettings || _audioPopupMode != 0 || _showVoiceInfo;
        if (wantStats)
        {
            _lastStatsTick = now;
            fixed (FcAudioStats* pa = &_audioStats) MediaNative.fc_voice_get_stats(pa);
            fixed (FcScreenStats* ps = &_selfScreenStats) MediaNative.fc_screen_get_stats(ps);
            if (dc.IsWatchingAnyStream || dc.IsPublishingStream) dc.PollWatchStats();

            bool micLive = !_muted && (!SettingsStore.Current.PushToTalk || _pttPrevDown);
            bool selfSpk = micLive && (dc.VoiceConnected || _loopback || _showSettings || _audioPopupMode != 0) && (_audioStats.Speaking != 0 || _audioStats.CleanRms > 0.008f);
            dc.UpdateLocalSpeaking(dc.VoiceConnected && selfSpk, _localVoiceLevel);
            if (selfSpk != _prevLocalSpkPoll || selfSpk) { _prevLocalSpkPoll = selfSpk; needRedraw = true; }

            // Check if any peer speaking state changed
            int peerHash = 0;
            if (dc.VoiceConnected)
            {
                var vg = dc.Guilds.FirstOrDefault(x => x.Id == dc.ConnectedVoiceGuildId);
                if (vg != null)
                {
                    lock (vg.VoiceStates)
                    {
                        foreach (var vm in vg.VoiceStates.Values)
                            if (vm.ChannelId == dc.ConnectedVoiceChannelId && dc.IsUserActuallySpeaking(vm))
                                peerHash = HashCode.Combine(peerHash, vm.UserId);
                    }
                }
            }
            if (peerHash != _prevPeerSpkHash) { _prevPeerSpkHash = peerHash; needRedraw = true; }

            long sec = now / 1000;
            if (sec != _lastClockSec) { _lastClockSec = sec; needRedraw = true; }
            if (_loopback || _showSettings || _audioPopupMode != 0 || _showVoiceInfo) needRedraw = true;
            if (_streaming && _selfScreenStats.FramesEncoded == 0) needRedraw = true;
        }
        if (!string.IsNullOrEmpty(_toast) && now >= _toastUntil) { _toast = ""; needRedraw = true; }
        return needRedraw;
    }

    public static void OnEscape()
    {
        if (_lightboxUrl != null) _lightboxUrl = null;
        else if (_profileUserId != null) _profileUserId = null;
        else if (_ctxStreamUserId != null) _ctxStreamUserId = null;
        else if (_ctxUser != null) _ctxUser = null;
        else if (_audioPopupMode != 0) { _audioPopupMode = 0; _audioPopupSub = 0; }
        else if (_showVoiceInfo) _showVoiceInfo = false;
        else if (_showStatusMenu) _showStatusMenu = false;
        else if (_showEmojiPicker) _showEmojiPicker = false;
        else if (_showPinsPopup) _showPinsPopup = false;
        else if (_showShareModal) _showShareModal = false;
        else if (_showSettings) _showSettings = false;
        else if (_showQuickSwitch) { _showQuickSwitch = false; _searchQuery = ""; }
        else if (_showChannelSearch) { _showChannelSearch = false; _searchQuery = ""; }
        else if (!string.IsNullOrEmpty(_editingMsgId)) { _editingMsgId = ""; _editingOrigText = ""; App.TakeChatInputText(); }
        else if (!string.IsNullOrEmpty(_replyToId)) { _replyToId = ""; _replyToLabel = ""; }
        else if (DiscordClient.Instance.FocusedStreamUserId != null) DiscordClient.Instance.FocusedStreamUserId = null;
        App.Invalidate();
    }

    public static void Init()
    {
        LocalCache.Init();
        MediaNative.fc_init();
        RefreshHardwareListsAsync();
        ApplyAudioSettings();

        DiscordClient.Instance.StateChanged += () =>
        {
            if (DiscordClient.Instance.IsConnected || DiscordClient.Instance.IsDemoMode)
                RemoteAuthQr.Instance.Stop();
            App.WakeUi();
        };
        DiscordClient.Instance.ReadyCompleted += () => _ = ShellAsync.RestoreAfterUpdateAsync();
        RemoteAuthQr.Instance.Changed += () => App.WakeUi();

        var savedToken = SettingsStore.GetToken();
        if (!string.IsNullOrEmpty(savedToken)) _ = DiscordClient.Instance.ConnectAsync(savedToken);
        else if (App.DemoArg) DiscordClient.Instance.EnterDemoMode();
        else RemoteAuthQr.Instance.Start();
    }

    private static int _hwRefreshing;
    public static void RefreshHardwareListsAsync()
    {
        if (Interlocked.Exchange(ref _hwRefreshing, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var caps = MediaNative.ListAudioDevices(true);
                var rens = MediaNative.ListAudioDevices(false);
                var mons = MediaNative.ListMonitors();
                _capDevices = caps;
                _renDevices = rens;
                _monitors = mons;
                App.WakeUi();
            }
            catch { }
            finally { Interlocked.Exchange(ref _hwRefreshing, 0); }
        });
    }

    public static void ApplyAudioSettings()
    {
        var s = SettingsStore.Current;
        MediaNative.fc_voice_set_noise_config(s.NoiseMode, s.NoiseStrength, s.HpfEnabled ? 1 : 0, s.AgcEnabled ? 1 : 0, s.GateDb);
        MediaNative.fc_voice_set_input_gain(s.InputGain);
        MediaNative.fc_voice_set_output_volume(s.OutputVolume);
        MediaNative.fc_voice_set_muted(_muted ? 1 : 0);
        MediaNative.fc_voice_set_deafened(_deafened ? 1 : 0);
        MediaNative.fc_voice_set_loopback(_loopback ? 1 : 0);
    }

    public static void StartStream(int monitorIndex, ulong targetHwnd)
    {
        var s = SettingsStore.Current;
        int w = s.StreamWidth, h = s.StreamHeight;
        if (w <= 0 || h <= 0)
        {
            var mon = _monitors.Count > 0 && monitorIndex < _monitors.Count ? _monitors[monitorIndex] : (0, 1920, 1080, 60, "");
            w = mon.Item2; h = mon.Item3;
        }
        w = Math.Clamp(w, 640, 3840) & ~7;
        h = Math.Clamp(h, 360, 2160) & ~7;
        _activeStreamMonitor = monitorIndex;
        _activeStreamHwnd = targetHwnd;
        int bitrate = s.StreamOptimizeDetail
            ? (h >= 2160 ? 11500 : h >= 1440 ? 9000 : h >= 1080 ? 7000 : h >= 720 ? 4500 : 2500)
            : (h >= 2160 ? 9500 : h >= 1440 ? 7500 : h >= 1080 ? 5500 : h >= 720 ? 3500 : 2000);

        int ok = MediaNative.fc_screen_start_ex(
            monitorIndex, targetHwnd, w, h, s.StreamFps, bitrate, s.StreamCodec,
            1, App.SelfStreamHwnd, s.StreamCaptureAudio ? 1 : 0, s.RenderDeviceId);

        _streaming = ok != 0;
        if (_streaming)
        {
            SoundFx.Play(UiSound.StreamStart);
            _ = DiscordClient.Instance.StartPublishingGoLiveAsync(w, h, s.StreamFps);
            _inVoiceStage = true;
            Toast($"Трансляция запущена ({w}x{h} @ {s.StreamFps}fps{(s.StreamCaptureAudio ? " + Звук" : "")})");
        }
        else
        {
            Toast("Не удалось запустить захват экрана");
        }
        App.Invalidate();
    }

    public static void StopStream()
    {
        if (!_streaming) return;
        MediaNative.fc_screen_stop();
        _streaming = false;
        SoundFx.Play(UiSound.StreamStop);
        DiscordClient.Instance.StopPublishingGoLive();
        Toast("Трансляция экрана остановлена");
        App.Invalidate();
    }

    public static void OpenScreenShareModal()
    {
        _ctxUser = null;
        _ctxStreamUserId = null;
        _audioPopupMode = 0;
        _showShareModal = true;
        RefreshShareSourcesAsync();
        App.Invalidate();
    }

    public static ResumeSession CaptureResumeSession(string targetVer)
    {
        var dc = DiscordClient.Instance;
        var snap = new ResumeSession
        {
            SavedUtcTicks = DateTime.UtcNow.Ticks,
            FromVersion = Updater.CurrentVersion,
            TargetVersion = targetVer,
            ActiveGuildId = dc.ActiveGuildId,
            ActiveChannelId = dc.ActiveChannelId,
            InVoiceStage = _inVoiceStage,
            ShowFriendsView = _showFriendsView,
            VoiceGuildId = dc.VoiceConnected ? dc.ConnectedVoiceGuildId : "",
            VoiceChannelId = dc.VoiceConnected ? dc.ConnectedVoiceChannelId : "",
            Muted = _muted,
            Deafened = _deafened,
            FocusedStreamUserId = dc.FocusedStreamUserId,
            WasStreaming = _streaming,
            StreamMonitor = _activeStreamMonitor,
            StreamHwnd = _activeStreamHwnd
        };
        foreach (var ws in dc.GetWatchSessionsSnapshot())
        {
            snap.WatchedStreams.Add(new ResumeWatchItem
            {
                UserId = ws.StreamerUserId,
                Name = ws.StreamerName,
                Volume = ws.Volume,
                Muted = ws.Muted
            });
        }
        return snap;
    }

    public static void ApplyRestoredUiState(ResumeSession snap)
    {
        var dc = DiscordClient.Instance;
        _muted = snap.Muted;
        _deafened = snap.Deafened;
        ApplyAudioSettings();
        if (!string.IsNullOrEmpty(snap.ActiveGuildId)) dc.ActiveGuildId = snap.ActiveGuildId;
        if (!string.IsNullOrEmpty(snap.ActiveChannelId))
        {
            dc.ActiveChannelId = snap.ActiveChannelId;
            _ = dc.LoadMessagesAsync(snap.ActiveChannelId);
        }
        _inVoiceStage = snap.InVoiceStage;
        _showFriendsView = snap.ShowFriendsView;
        App.WakeUi();
    }

    public static void Toast(string msg, int ms = 2600)
    {
        _toast = msg;
        _toastUntil = Environment.TickCount64 + ms;
        App.Invalidate();
    }

    public static void OnChatEnter(string text)
    {
        var dc = DiscordClient.Instance;
        if (!string.IsNullOrEmpty(dc.ActiveChannelId) && !string.IsNullOrWhiteSpace(text))
        {
            if (!string.IsNullOrEmpty(_editingMsgId))
            {
                _ = dc.EditMessageAsync(dc.ActiveChannelId, _editingMsgId, text);
                _editingMsgId = "";
                _editingOrigText = "";
                Toast("Сообщение изменено");
            }
            else
            {
                _ = dc.SendMessageAsync(dc.ActiveChannelId, text, string.IsNullOrEmpty(_replyToId) ? null : _replyToId);
                _replyToId = "";
                _replyToLabel = "";
                _chatScroll = 0;
            }
        }
    }

    public static void OnTokenSubmit(string token)
    {
        if (!string.IsNullOrWhiteSpace(token)) _ = DiscordClient.Instance.ConnectAsync(token);
    }

    public static void Draw()
    {
        long now = Environment.TickCount64;
        var dc = DiscordClient.Instance;

        if (dc.VoiceConnected && _voiceJoinedTick == 0) _voiceJoinedTick = now;
        else if (!dc.VoiceConnected) _voiceJoinedTick = 0;

        if (SettingsStore.Current.PushToTalk && dc.VoiceConnected)
        {
            bool pttDown = W.AsyncKeyDown(W.VK_RCONTROL);
            if (pttDown != _pttPrevDown)
            {
                _pttPrevDown = pttDown;
                MediaNative.fc_voice_set_muted((_muted || !pttDown) ? 1 : 0);
            }
            UI.Animate();
        }

        bool wantAudioStats = _streaming || dc.VoiceConnected || dc.IsWatchingAnyStream || _loopback || _showSettings || _audioPopupMode != 0;
        if (wantAudioStats && now - _lastStatsTick >= 40)
        {
            _lastStatsTick = now;
            fixed (FcAudioStats* pa = &_audioStats) MediaNative.fc_voice_get_stats(pa);
            fixed (FcScreenStats* ps = &_selfScreenStats) MediaNative.fc_screen_get_stats(ps);
            if (dc.IsWatchingAnyStream || dc.IsPublishingStream) dc.PollWatchStats();
            if (_loopback || _showSettings || _audioPopupMode != 0) UI.Animate();
        }

        // Smooth real-time local speaking state & logarithmic mic level (0..1)
        bool micLive = !_muted && (!SettingsStore.Current.PushToTalk || _pttPrevDown);
        bool selfRawSpk = micLive && (dc.VoiceConnected || _loopback || _showSettings || _audioPopupMode != 0) && (_audioStats.Speaking != 0 || _audioStats.CleanRms > 0.008f);
        float rms = MathF.Max(_audioStats.CleanRms, selfRawSpk ? _audioStats.RawRms * 0.55f : 0f);
        float db = 20f * MathF.Log10(MathF.Max(rms, 1e-5f));
        float targetLvl = selfRawSpk ? Math.Clamp((db + 52f) / 36f, 0.18f, 1f) : 0f;
        _localVoiceLevel += (targetLvl - _localVoiceLevel) * (targetLvl > _localVoiceLevel ? 0.44f : 0.18f);
        float targetSpkA = selfRawSpk ? 1f : 0f;
        _localSpeakAnim += (targetSpkA - _localSpeakAnim) * (targetSpkA > _localSpeakAnim ? 0.42f : 0.16f);
        if (_localVoiceLevel < 0.005f) _localVoiceLevel = 0f;
        if (_localSpeakAnim < 0.005f) _localSpeakAnim = 0f;
        dc.UpdateLocalSpeaking(dc.VoiceConnected && selfRawSpk, _localVoiceLevel);
        if (_localSpeakAnim > 0.005f) UI.Animate();

        _wantChatInput = false;
        _wantTokenInput = false;
        _wantSearchInput = false;
        _wantSelfHost = false;
        for (int i = 0; i < App.MaxWatchSlots; i++) _watchSlotsLayout[i] = (false, default);
        _overlayCutouts.Clear();

        float w = Gfx.W, h = Gfx.H;
        DrawTopBar(new R(0, 0, w, TopH));

        if (!dc.IsConnected && !dc.IsDemoMode)
        {
            DrawLoginScreen(new R(0, TopH, w, h - TopH));
        }
        else
        {
            DrawGuildRail(new R(0, TopH, RailW, h - TopH));
            DrawChannelSidebar(new R(RailW, TopH, SideW, h - TopH));
            if (_inVoiceStage && (dc.VoiceConnected || dc.IsWatchingAnyStream || _streaming))
                DrawVoiceStage(new R(RailW + SideW, TopH, w - RailW - SideW, h - TopH));
            else if (dc.ActiveGuildId == "@me" && _showFriendsView)
                DrawFriendsStage(new R(RailW + SideW, TopH, w - RailW - SideW, h - TopH));
            else
                DrawMainStage(new R(RailW + SideW, TopH, w - RailW - SideW, h - TopH));
        }

        // Overlays (drawn on top; popups punch exact cutouts in live video HWNDs!)
        if (_hoverStreamVm != null && !_showSettings && !_showShareModal && !_showQuickSwitch && _ctxUser == null && _ctxStreamUserId == null && _profileUserId == null)
            DrawHoverStreamPreview();
        if (_showVoiceInfo) DrawVoiceInfoPopup();
        if (_showStatusMenu) DrawStatusPopup();
        if (_showEmojiPicker) DrawEmojiPicker();
        if (_showPinsPopup) DrawPinnedPopup();
        if (_profileUserId != null) DrawUserProfilePopup();
        if (_ctxUser != null) DrawUserContextMenu();
        if (_ctxStreamUserId != null) DrawStreamContextMenu();
        if (_audioPopupMode != 0) DrawAudioQuickPopup();

        bool fullModalOpen = _showSettings || _showShareModal || _showQuickSwitch || _lightboxUrl != null;
        if (_lightboxUrl != null) DrawImageLightbox(new R(0, TopH, w, h - TopH));
        else if (_showShareModal) DrawScreenShareModal(new R(0, TopH, w, h - TopH));
        else if (_showSettings) DrawSettingsOverlay(new R(0, TopH, w, h - TopH));
        else if (_showQuickSwitch) DrawQuickSwitcher(new R(0, TopH, w, h - TopH));

        if (fullModalOpen)
        {
            for (int i = 0; i < App.MaxWatchSlots; i++) _watchSlotsLayout[i].Show = false;
            _wantSelfHost = false;
        }

        if (!string.IsNullOrEmpty(_toast) && now < _toastUntil)
        {
            var tr = new R((w - 460) * 0.5f, h - 56, 460, 36);
            Gfx.FillRect(tr, 0xEE1F2937, 8);
            Gfx.StrokeRect(tr, Th.Brand, 1, 8);
            Gfx.DrawText(_toast, new R(tr.X + 12, tr.Y + 9, tr.W - 24, 20), 12.5f, Th.Text, 600, 1);
            _overlayCutouts.Add(tr);
            UI.Animate();
        }

        ReadOnlySpan<R> cutoutsSpan = _overlayCutouts.Count > 0 ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_overlayCutouts) : [];
        App.SyncNativeChildren(_wantChatInput, _chatInputRect, _wantTokenInput, _tokenInputRect,
            _wantSearchInput, _searchInputRect,
            _watchSlotsLayout, _wantSelfHost, _selfHostRect, cutoutsSpan);
    }

    private static Vortice.Direct2D1.ID2D1Bitmap1? GetBrandIcon(int maxDim = 96, ImgMask mask = ImgMask.None)
        => ImageCache.GetLocal(Path.Combine(AppContext.BaseDirectory, "Assets", "fluxcord.png"), maxDim, mask);

    private static void DrawTopBar(R r)
    {
        Gfx.FillRect(r, Th.Rail);
        Gfx.FillRect(new R(0, r.Bottom - 1, r.W, 1), Th.Border);

        var dc = DiscordClient.Instance;
        var topIcon = GetBrandIcon(48);
        if (topIcon != null) Gfx.DrawBitmap(topIcon, new R(10, r.Y + (r.H - 20f) * 0.5f, 20, 20));
        else Gfx.FillCircle(20, r.Y + r.H * 0.5f, 7, Th.Brand);
        Gfx.DrawText("FluxCord", new R(36, r.Y + 9, 90, 22), 13.5f, Th.Text, 700);

        long ramMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
        int showPing = dc.VoiceConnected && dc.VoicePingMs > 0 ? dc.VoicePingMs : dc.GatewayPingMs;
        string perf = $"RAM {ramMb} МБ • {Gfx.LastFlushMs:F1} мс • Пинг {showPing} мс";
        if (_streaming) perf += $" • LIVE {_selfScreenStats.Fps:F0}fps";
        var pill = new R(112, r.Y + 8, MathF.Min(350, r.W * 0.32f), 22);
        Gfx.FillRect(pill, Th.Panel, 11);
        Gfx.DrawText(perf, new R(pill.X + 10, pill.Y + 3, pill.W - 20, 16), 11f, _streaming ? Th.Green : (showPing > 0 && showPing < 95 ? Th.Green : Th.Dim), 600);

        // Centered server name (exact GoofCord top bar!)
        var g = dc.Guilds.FirstOrDefault(x => x.Id == dc.ActiveGuildId);
        string centerTitle = dc.ActiveGuildId == "@me" ? "Личные сообщения" : (g?.Name ?? "FluxCord");
        Gfx.DrawText(centerTitle, new R(r.X + r.W * 0.35f, r.Y + 9, r.W * 0.3f, 20), 13.5f, Th.Sub, 600, 1);

        float bx = r.Right - 132;

        // Ultra-Updater live top-bar pill (downloads silently in background, 1-click instant apply + auto-rejoin call!)
        if (Updater.HasUpdate || Updater.Downloading || Updater.Applying)
        {
            string uTxt = Updater.Applying ? "⚡ Установка..." :
                          Updater.Downloading ? $"⬇ v{Updater.AvailableVersion} {(int)(Updater.Progress * 100)}%" :
                          Updater.IsReadyToApply ? $"⬆ Обновить до v{Updater.AvailableVersion} ⚡" :
                          $"⬆ Доступно v{Updater.AvailableVersion}";
            float uw = Gfx.MeasureText(uTxt, 11.5f, 700, 240).W + 22;
            var ur = new R(bx - uw - 10, r.Y + 6, uw, 26);
            uint ubg = Updater.IsReadyToApply ? Th.Green : (Updater.Downloading ? Th.Card : Th.Brand);
            Gfx.FillRect(ur, ubg, 13);
            if (Updater.Downloading)
                Gfx.FillRect(new R(ur.X, ur.Y, ur.W * (float)Updater.Progress, ur.H), 0x6623A55A, 13);
            if (UI.Hover(ur)) Gfx.FillRect(ur, Th.Hover, 13);
            Gfx.DrawText(uTxt, new R(ur.X, ur.Y + 5, ur.W, 16), 11.5f, 0xFFFFFFFF, 700, 1);
            if (UI.Click(UI.Id("top_upd"), ur)) Updater.StartUpdateNow();
        }

        if (UI.Button(UI.Id("win_min"), new R(bx, r.Y + 4, 40, 30), "—", 0x00000000, Th.Sub, 4))
            W.ShowWindow(App.Hwnd, W.SW_MINIMIZE);
        if (UI.Button(UI.Id("win_max"), new R(bx + 44, r.Y + 4, 40, 30), "□", 0x00000000, Th.Sub, 4))
            W.ShowWindow(App.Hwnd, W.IsZoomed(App.Hwnd) ? W.SW_SHOWNORMAL : W.SW_MAXIMIZE);
        if (UI.Button(UI.Id("win_cls"), new R(bx + 88, r.Y + 4, 40, 30), "✕", 0x00000000, Th.Red, 4))
            W.PostMessageW(App.Hwnd, W.WM_CLOSE, 0, 0);
    }

    private static void DrawLoginScreen(R r)
    {
        Gfx.FillRect(r, Th.Bg);
        var card = new R(r.X + (r.W - 780) * 0.5f, r.Y + (r.H - 410) * 0.5f, 780, 410);
        Gfx.FillRect(card, Th.Panel, 14);
        Gfx.StrokeRect(card, Th.Border, 1, 14);

        float lx = card.X + 28, lw = 430;
        var loginIcon = GetBrandIcon(96);
        if (loginIcon != null)
        {
            Gfx.DrawBitmap(loginIcon, new R(lx, card.Y + 26, 44, 44));
            Gfx.DrawText("FluxCord — NativeAOT Discord", new R(lx + 54, card.Y + 26, lw - 54, 28), 21, Th.Text, 700);
            Gfx.DrawText("Direct2D FlipDiscard • Zero-Copy 4K60 NVENC • Pro Audio", new R(lx + 54, card.Y + 56, lw - 54, 20), 12f, Th.Sub);
        }
        else
        {
            Gfx.DrawText("FluxCord — NativeAOT Discord", new R(lx, card.Y + 28, lw, 30), 22, Th.Text, 700);
            Gfx.DrawText("Direct2D FlipDiscard • Zero-Copy 4K60 NVENC • Pro Audio", new R(lx, card.Y + 60, lw, 22), 12.5f, Th.Sub);
        }
        Gfx.DrawText(SettingsStore.MigrationSummary, new R(lx, card.Y + 86, lw, 20), 12, Th.Green, 600);

        Gfx.DrawText("Или войти по токену (шифруется Windows DPAPI):", new R(lx, card.Y + 132, lw, 20), 12.5f, Th.Sub);
        _tokenInputRect = new R(lx, card.Y + 158, lw, 38);
        _wantTokenInput = true;
        Gfx.FillRect(_tokenInputRect, Th.Bg, 8);
        Gfx.StrokeRect(_tokenInputRect, Th.Border, 1, 8);

        if (UI.Button(UI.Id("btn_login"), new R(lx, card.Y + 212, lw, 40), "Войти по токену", Th.Brand, Th.Text, 8, 14, 700))
            OnTokenSubmit(App.GetTokenInputText());
        if (UI.Button(UI.Id("btn_demo"), new R(lx, card.Y + 266, lw, 40), "Запустить Demo + Тест 4K Zero-Copy и Шумодава", Th.Card, Th.Cyan, 8, 13, 600))
        {
            RemoteAuthQr.Instance.Stop();
            DiscordClient.Instance.EnterDemoMode();
        }
        Gfx.DrawText(DiscordClient.Instance.StatusText, new R(lx, card.Y + 326, lw, 24), 12, Th.Sub);

        float divX = card.X + 486;
        Gfx.FillRect(new R(divX, card.Y + 28, 1, card.H - 56), Th.Border);

        float rx = divX + 28, rw = card.Right - rx - 28;
        var qr = RemoteAuthQr.Instance;
        if (!qr.IsRunning && !qr.IsExpired && qr.Matrix == null) qr.Start();

        Gfx.DrawText("Вход по QR-коду", new R(rx, card.Y + 28, rw, 26), 17, Th.Text, 700, 1);
        float qrSize = 196;
        var qrBox = new R(rx + (rw - qrSize) * 0.5f, card.Y + 64, qrSize, qrSize);
        Gfx.FillRect(qrBox, 0xFFFFFFFF, 12);

        if (qr.Matrix != null && !qr.IsExpired)
        {
            int n = qr.Matrix.GetLength(0);
            float pad = 12f, cell = (qrSize - pad * 2f) / n;
            float ox = qrBox.X + pad, oy = qrBox.Y + pad;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    if (qr.Matrix[y, x])
                        Gfx.FillRect(new R(ox + x * cell, oy + y * cell, cell + 0.35f, cell + 0.35f), 0xFF0A0B0E);
            float cx = qrBox.X + qrSize * 0.5f, cy = qrBox.Y + qrSize * 0.5f;
            Gfx.FillCircle(cx, cy, 18, 0xFFFFFFFF);
            var qrLogo = GetBrandIcon(64, ImgMask.Circle);
            if (qrLogo != null) Gfx.DrawBitmap(qrLogo, new R(cx - 15, cy - 15, 30, 30));
            else
            {
                Gfx.FillCircle(cx, cy, 14, Th.Brand);
                Gfx.DrawText("F", new R(cx - 10, cy - 10, 20, 20), 14, 0xFFFFFFFF, 700, 1);
            }
        }
        else
        {
            Gfx.FillRect(qrBox.Inset(6), Th.Card, 8);
            Gfx.DrawText(qr.IsExpired ? "QR истёк" : "Загрузка...", new R(qrBox.X, qrBox.Y + qrSize * 0.44f, qrSize, 24), 13, Th.Sub, 600, 1);
        }

        string statusLine = qr.Status;
        if (qr.Matrix != null && !qr.IsExpired && qr.ExpiresAtTick > 0)
        {
            long remSec = Math.Max(0, (qr.ExpiresAtTick - Environment.TickCount64) / 1000);
            if (remSec == 0) qr.Start();
            else if (string.IsNullOrEmpty(qr.ScannedUser)) statusLine = $"Отсканируй в Discord ({remSec / 60}:{remSec % 60:00})";
        }
        Gfx.DrawText(statusLine, new R(rx - 8, card.Y + 272, rw + 16, 40), 12.5f, string.IsNullOrEmpty(qr.ScannedUser) ? Th.Sub : Th.Green, 600, 1, true);
        if (UI.Button(UI.Id("btn_qr_ref"), new R(rx + 24, card.Y + 324, rw - 48, 34), "Обновить QR-код", Th.Card, Th.Text, 7, 12.5f)) qr.Start();
    }

    private static void DrawGuildRail(R r)
    {
        Gfx.FillRect(r, Th.Rail);
        Gfx.FillRect(new R(r.Right - 1, r.Y, 1, r.H), Th.Border);
        var dc = DiscordClient.Instance;
        _hoverGuildTip = "";

        float contentH = 12 + 56 + dc.Guilds.Count * 54 + 20;
        float maxScroll = MathF.Max(0, contentH - r.H);
        if (UI.Hover(r) && UI.Wheel != 0) _railScroll = Math.Clamp(_railScroll - UI.Wheel * 54f, 0f, maxScroll);
        else _railScroll = Math.Clamp(_railScroll, 0f, maxScroll);

        Gfx.PushClip(r);
        float y = r.Y + 12 - _railScroll;

        bool dmAct = dc.ActiveGuildId == "@me";
        var dmRect = new R(r.X + 12, y, 44, 44);
        bool dmOver = UI.Hover(dmRect);
        if (dmAct) Gfx.FillRect(new R(r.X, y + 6, 4, 32), Th.Text, 2);
        else if (dmOver) Gfx.FillRect(new R(r.X, y + 14, 4, 16), Th.Text, 2);
        var dmLogo = GetBrandIcon(96, dmAct || dmOver ? ImgMask.None : ImgMask.Circle);
        if (dmLogo != null)
        {
            Gfx.DrawBitmap(dmLogo, dmRect);
            if (!dmAct && !dmOver) Gfx.FillRect(dmRect, 0x22000000, 22);
        }
        else
        {
            Gfx.FillRect(dmRect, dmAct || dmOver ? Th.Brand : Th.Card, dmAct || dmOver ? 14 : 22);
            Gfx.DrawIcon(IcChat, "ЛС", dmRect, 20, Th.Text);
        }
        if (dmOver) { _hoverGuildTip = "Личные сообщения"; _hoverGuildTipY = y + 8; }
        if (UI.Click(UI.Id("guild_dm"), dmRect))
        {
            dc.ActiveGuildId = "@me"; _inVoiceStage = false; _sideScroll = 0; _showFriendsView = true;
        }
        y += 56;
        Gfx.FillRect(new R(r.X + 18, y - 6, 32, 1), Th.Border);

        for (int i = 0; i < dc.Guilds.Count; i++)
        {
            if (y + 44 < r.Y - 10) { y += 54; continue; }
            if (y > r.Bottom + 10) break;

            var g = dc.Guilds[i];
            bool act = dc.ActiveGuildId == g.Id;
            bool hasUnread = !act && g.Channels.Any(c => dc.UnreadChannels.ContainsKey(c.Id));
            var gr = new R(r.X + 12, y, 44, 44);
            bool over = UI.Hover(gr);

            if (act) Gfx.FillRect(new R(r.X, y + 6, 4, 32), Th.Text, 2);
            else if (over) Gfx.FillRect(new R(r.X, y + 14, 4, 16), Th.Text, 2);
            else if (hasUnread) Gfx.FillRect(new R(r.X, y + 18, 4, 8), Th.Text, 2);

            var bmp = ImageCache.Get(g.IconUrl, 96, act || over ? ImgMask.Squircle : ImgMask.Circle);
            if (bmp != null) Gfx.DrawBitmap(bmp, gr);
            else
            {
                Gfx.FillRect(gr, act || over ? Th.Brand : Th.Card, act || over ? 14 : 22);
                string abbr = g.Name.Length >= 2 ? g.Name[..2].ToUpperInvariant() : g.Name.ToUpperInvariant();
                Gfx.DrawText(abbr, new R(gr.X, gr.Y + 13, gr.W, 20), 13, Th.Text, 700, 1);
            }

            if (over) { _hoverGuildTip = g.Name; _hoverGuildTipY = y + 8; }
            if (UI.Click(UI.Id("guild", i), gr))
            {
                dc.ActiveGuildId = g.Id; _sideScroll = 0; _showFriendsView = false;
                var first = g.Channels.FirstOrDefault(c => c.Type == 0 || c.Type == 5);
                if (first != null) { dc.ActiveChannelId = first.Id; _chatScroll = 0; _ = dc.LoadMessagesAsync(first.Id); }
            }
            y += 54;
        }
        Gfx.PopClip();

        if (maxScroll > 0)
        {
            float barH = MathF.Max(28, r.H * (r.H / contentH));
            float barY = r.Y + (_railScroll / maxScroll) * (r.H - barH);
            Gfx.FillRect(new R(r.Right - 4, barY, 3, barH), 0x33FFFFFF, 1.5f);
        }
    }

    private static void DrawChannelSidebar(R r)
    {
        Gfx.FillRect(r, Th.Sidebar);
        Gfx.FillRect(new R(r.Right - 1, r.Y, 1, r.H), Th.Border);

        var dc = DiscordClient.Instance;
        var guild = dc.Guilds.FirstOrDefault(g => g.Id == dc.ActiveGuildId);
        string title = dc.ActiveGuildId == "@me" ? "Личные сообщения" : (guild?.Name ?? "Сервер");

        Gfx.DrawText(title, new R(r.X + 14, r.Y + 12, r.W - 28, 24), 15, Th.Text, 700);
        Gfx.FillRect(new R(r.X, r.Y + 44, r.W, 1), Th.Border);

        float dockH = 60f + (dc.VoiceConnected ? 92f : 0f) + (_streaming ? 42f : 0f);
        var listRect = new R(r.X, r.Y + 46, r.W, r.H - 46 - dockH);

        var channels = dc.ActiveGuildId == "@me" ? dc.DmChannels : (guild?.Channels ?? []);
        float totalH = 16 + (dc.ActiveGuildId == "@me" ? 40 : 0);
        foreach (var ch in channels)
        {
            bool catCollapsed = !ch.IsCategory && !string.IsNullOrEmpty(ch.ParentId) && _collapsedCats.Contains(ch.ParentId)
                                && ch.Id != dc.ActiveChannelId && ch.Id != dc.ConnectedVoiceChannelId;
            if (catCollapsed) continue;
            totalH += ch.IsCategory ? 28 : 34;
            if (ch.IsVoice && guild != null)
            {
                lock (guild.VoiceStates)
                    totalH += guild.VoiceStates.Values.Count(v => v.ChannelId == ch.Id) * 28;
            }
        }
        float maxSideScroll = MathF.Max(0, totalH - listRect.H);
        if (UI.Hover(listRect) && UI.Wheel != 0) _sideScroll = Math.Clamp(_sideScroll - UI.Wheel * 44f, 0f, maxSideScroll);
        else _sideScroll = Math.Clamp(_sideScroll, 0f, maxSideScroll);

        Gfx.PushClip(listRect);
        float y = listRect.Y + 8 - _sideScroll;
        if (dc.ActiveGuildId == "@me")
        {
            var frBtn = new R(r.X + 8, y, r.W - 16, 34);
            if (_showFriendsView) Gfx.FillRect(frBtn, Th.Active, 6);
            else if (UI.Hover(frBtn)) Gfx.FillRect(frBtn, Th.Hover, 6);
            int onCnt; lock (dc.Friends) onCnt = dc.Friends.Count(f => f.Status != "offline");
            Gfx.DrawText($"👥  Друзья ({onCnt} в сети)", new R(frBtn.X + 10, frBtn.Y + 8, frBtn.W - 20, 18), 13.5f, _showFriendsView ? Th.Text : Th.Sub, 600);
            if (UI.Click(UI.Id("dm_friends_btn"), frBtn)) { _showFriendsView = true; _inVoiceStage = false; }
            y += 40;
        }
        for (int i = 0; i < channels.Count; i++)
        {
            var ch = channels[i];
            if (ch.IsCategory)
            {
                bool col = _collapsedCats.Contains(ch.Id);
                if (y + 28 >= listRect.Y && y <= listRect.Bottom)
                {
                    var catR = new R(r.X + 6, y + 2, r.W - 12, 24);
                    if (UI.Hover(catR)) Gfx.FillRect(catR, Th.Hover, 4);
                    Gfx.DrawText((col ? "▸ " : "▾ ") + ch.Name.ToUpperInvariant(), new R(r.X + 10, y + 7, r.W - 20, 18), 11, Th.Dim, 700);
                    if (UI.Click(UI.Id("cat", i), catR))
                    {
                        if (col) _collapsedCats.Remove(ch.Id);
                        else _collapsedCats.Add(ch.Id);
                    }
                }
                y += 28;
                continue;
            }
            if (!string.IsNullOrEmpty(ch.ParentId) && _collapsedCats.Contains(ch.ParentId) && ch.Id != dc.ActiveChannelId && ch.Id != dc.ConnectedVoiceChannelId)
                continue;

            bool isActive = !_inVoiceStage && ch.Id == dc.ActiveChannelId;
            bool isVoiceJoined = ch.IsVoice && ch.Id == dc.ConnectedVoiceChannelId;
            var cr = new R(r.X + 8, y, r.W - 16, 32);
            if (y + 32 >= listRect.Y && y <= listRect.Bottom)
            {
                if (isVoiceJoined) Gfx.FillRect(cr, 0x2223A55A, 6);
                else if (isActive) Gfx.FillRect(cr, Th.Active, 6);
                else if (UI.Hover(cr)) Gfx.FillRect(cr, Th.Hover, 6);

                if (ch.Type == 1 || ch.Type == 3)
                {
                    var av = ImageCache.Get(ch.DmAvatarUrl, 48, ImgMask.Circle);
                    var avr = new R(cr.X + 6, cr.Y + 4, 24, 24);
                    if (av != null) Gfx.DrawBitmap(av, avr);
                    else { Gfx.FillCircle(avr.X + 12, avr.Y + 12, 12, Th.Card); Gfx.DrawText(ch.Name.Length > 0 ? ch.Name[..1].ToUpperInvariant() : "?", new R(avr.X, avr.Y + 4, 24, 18), 11, Th.Text, 700, 1); }
                    Gfx.DrawText(ch.Name, new R(cr.X + 36, cr.Y + 7, cr.W - 42, 20), 13.5f, isActive ? Th.Text : Th.Sub, isActive ? 600 : 400);
                }
                else
                {
                    var icR = new R(cr.X + 6, cr.Y + 5, 22, 22);
                    if (ch.IsVoice) Gfx.DrawIcon(IcVol, "🔊", icR, 15, isVoiceJoined ? Th.Green : Th.Dim);
                    else Gfx.DrawIcon(IcHash, "#", icR, 15, isActive ? Th.Text : Th.Dim);

                    bool unread = dc.UnreadChannels.ContainsKey(ch.Id);
                    if (unread && !isActive) Gfx.FillCircle(r.X + 4, cr.Y + 16, 3, Th.Text);

                    float nameRightPad = isVoiceJoined && _voiceJoinedTick > 0 ? 58f : 8f;
                    Gfx.DrawText(ch.Name, new R(cr.X + 30, cr.Y + 7, cr.W - 30 - nameRightPad, 20), 13.5f,
                        isVoiceJoined ? Th.Green : (isActive || unread ? Th.Text : Th.Sub), isActive || isVoiceJoined || unread ? 600 : 400);

                    if (isVoiceJoined && _voiceJoinedTick > 0)
                    {
                        var ts = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - _voiceJoinedTick));
                        string dur = ts.Hours > 0 ? $"{ts.Hours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes:00}:{ts.Seconds:00}";
                        Gfx.DrawText(dur, new R(cr.Right - 56, cr.Y + 8, 50, 16), 11f, Th.Green, 600, 2);
                    }
                }

                if (UI.Click(UI.Id("ch", i), cr))
                {
                    if (ch.IsVoice)
                    {
                        if (!isVoiceJoined) _ = dc.JoinVoiceAsync(ch.GuildId, ch.Id);
                        _inVoiceStage = true;
                        dc.FocusedStreamUserId = null;
                    }
                    else
                    {
                        dc.ActiveChannelId = ch.Id;
                        _inVoiceStage = false;
                        _showFriendsView = false;
                        _chatScroll = 0;
                        _ = dc.LoadMessagesAsync(ch.Id);
                    }
                }
            }
            y += 34;

            if (ch.IsVoice && guild != null)
            {
                List<VoiceMember> vms;
                lock (guild.VoiceStates) vms = guild.VoiceStates.Values.Where(v => v.ChannelId == ch.Id).ToList();
                foreach (var vm in vms)
                {
                    if (y + 28 >= listRect.Y && y <= listRect.Bottom)
                    {
                        var vr = new R(r.X + 20, y, r.W - 28, 26);
                        bool rowOver = UI.Hover(vr);
                        if (rowOver) Gfx.FillRect(vr, Th.Hover, 5);

                        var vav = ImageCache.Get(vm.AvatarUrl, 48, ImgMask.Circle);
                        var vavr = new R(vr.X + 4, vr.Y + 3, 20, 20);
                        bool isMe = vm.UserId == dc.Me.Id;
                        bool spk = dc.IsUserActuallySpeaking(vm);
                        float sa = isMe ? _localSpeakAnim : StepSpeakAnim(vm.UserId, spk);
                        bool isMuted = vm.SelfMute || (isMe && _muted);
                        // Exact original-Discord crisp green speaking ring tightly around avatar (matches c_070.jpg!)
                        if (sa > 0.04f)
                        {
                            float cx = vavr.X + 10f, cy = vavr.Y + 10f;
                            Gfx.FillCircle(cx, cy, 12.1f, Gfx.Fade(0xFF23A55A, sa));
                            Gfx.FillCircle(cx, cy, 10.3f, Th.Sidebar);
                        }
                        if (vav != null) Gfx.DrawBitmap(vav, vavr);
                        else Gfx.FillCircle(vavr.X + 10, vavr.Y + 10, 10, Th.Brand);

                        float rightUsed = (vm.SelfStream ? 58f : 4f) + (isMuted ? 18f : 0f);
                        Gfx.DrawText(vm.Username, new R(vr.X + 30, vr.Y + 5, MathF.Max(20f, vr.W - 34 - rightUsed), 18), 12.5f, sa > 0.15f ? Th.Text : Th.Sub, sa > 0.15f ? 600 : 500);

                        if (isMuted)
                            Gfx.DrawIcon(IcMicOff, "M", new R(vr.Right - (vm.SelfStream ? 74 : 20), vr.Y + 5, 16, 16), 12, isMe ? Th.Red : Th.Dim);

                        if (vm.SelfStream)
                        {
                            if (rowOver && !isMe)
                            {
                                _hoverStreamVm = vm;
                                _hoverStreamGuildId = ch.GuildId;
                                _hoverStreamY = vr.Y;
                                _hoverStreamLastTick = Environment.TickCount64;
                            }
                            bool watching = dc.IsWatchingStream(vm.UserId);
                            var br = new R(vr.Right - 54, vr.Y + 5, 50, 16);
                            Gfx.FillRect(br, Th.Red, 8);
                            Gfx.DrawText("В ЭФИРЕ", new R(br.X, br.Y + 1, br.W, 14), 9f, 0xFFFFFFFF, 700, 1);
                            if (watching) Gfx.FillCircle(br.X - 6, br.Y + 8, 3f, Th.Green);
                            if (UI.Click(UI.Id("watch_" + vm.UserId), vr))
                            {
                                _inVoiceStage = true;
                                _hoverStreamVm = null;
                                _ = dc.WatchStreamAsync(ch.GuildId, ch.Id, vm.UserId, vm.Username, focusImmediately: true);
                            }
                        }

                        if (UI.RightClick(vr))
                        {
                            _ctxStreamUserId = null; _audioPopupMode = 0;
                            _ctxUser = vm;
                            _ctxGuildId = ch.GuildId;
                            _ctxX = Math.Clamp(UI.MX, 8, Gfx.W - 252);
                            _ctxY = Math.Clamp(UI.MY, TopH + 8, Gfx.H - 220);
                        }
                    }
                    y += 28;
                }
            }
        }
        Gfx.PopClip();

        if (maxSideScroll > 0)
        {
            float barH = MathF.Max(28, listRect.H * (listRect.H / totalH));
            float barY = listRect.Y + (_sideScroll / maxSideScroll) * (listRect.H - barH);
            Gfx.FillRect(new R(r.Right - 4, barY, 3, barH), 0x33FFFFFF, 1.5f);
        }

        // Exact GoofCord Bottom-Left Voice Dock + User Bar (matches f_004.jpg & f_037.jpg!)
        float dy = r.Bottom - dockH;
        Gfx.FillRect(new R(r.X, dy, r.W, dockH), Th.Rail);
        Gfx.FillRect(new R(r.X, dy, r.W, 1), Th.Border);

        // 1. Go Live mini-bar when streaming
        if (_streaming)
        {
            var glr = new R(r.X + 8, dy + 6, r.W - 16, 30);
            Gfx.FillRect(glr, Th.Panel, 6);
            Gfx.FillRect(new R(glr.X + 4, glr.Y + 4, 22, 22), Th.Amber, 5);
            Gfx.DrawIcon(IcMonitor, "G", new R(glr.X + 4, glr.Y + 4, 22, 22), 13, 0xFF0A0B0E);
            Gfx.DrawText(string.IsNullOrEmpty(dc.PublishStatus) ? "Go Live" : dc.PublishStatus, new R(glr.X + 32, glr.Y + 7, glr.W - 64, 16), 12, Th.Text, 700);
            var stp = new R(glr.Right - 28, glr.Y + 3, 24, 24);
            if (UI.Hover(stp)) Gfx.FillRect(stp, 0x33ED4245, 5);
            Gfx.DrawIcon(IcMonitorOff, "X", stp, 14, Th.Sub);
            if (UI.Click(UI.Id("dock_stop_gl"), stp)) StopStream();
            dy += 42;
            Gfx.FillRect(new R(r.X, dy, r.W, 1), Th.Border);
        }

        // 2. Voice Connected Block (exact c_026.jpg — clickable to open Ping Graph + RTC Region Turbo Switcher!)
        if (dc.VoiceConnected)
        {
            var vg = dc.Guilds.FirstOrDefault(x => x.Id == dc.ConnectedVoiceGuildId);
            var vc = vg?.Channels.FirstOrDefault(x => x.Id == dc.ConnectedVoiceChannelId);
            var connHit = new R(r.X + 4, dy + 4, r.W - 46, 40);
            if (UI.Hover(connHit) || _showVoiceInfo) Gfx.FillRect(connHit, Th.Hover, 6);

            // Crisp green 3-bar signal strength icon (exact c_026.jpg!)
            uint sigCol = dc.VoicePingMs > 180 ? Th.Red : (dc.VoicePingMs > 110 ? Th.Amber : Th.Green);
            Gfx.FillRect(new R(r.X + 10, dy + 24, 3, 6), sigCol, 1);
            Gfx.FillRect(new R(r.X + 15, dy + 19, 3, 11), sigCol, 1);
            Gfx.FillRect(new R(r.X + 20, dy + 14, 3, 16), sigCol, 1);

            string vHdr = dc.VoicePingMs > 0 ? $"Голосовая связь • {dc.VoicePingMs} мс" : dc.VoiceStatus;
            Gfx.DrawText(vHdr, new R(r.X + 29, dy + 8, r.W - 76, 18), 12f, sigCol, 700);
            Gfx.DrawText($"{vc?.Name ?? "Voice"} / {vg?.Name ?? ""}", new R(r.X + 29, dy + 26, r.W - 76, 16), 11f, Th.Sub);
            if (UI.Click(UI.Id("btn_vinfo"), connHit))
            {
                _showVoiceInfo = !_showVoiceInfo;
                _showStatusMenu = false; _audioPopupMode = 0; _ctxUser = null; _ctxStreamUserId = null;
                if (_showVoiceInfo && dc.Regions.Count == 0) _ = dc.BenchmarkRegionsAsync();
            }

            var discBtn = new R(r.Right - 38, dy + 10, 28, 28);
            if (UI.Hover(discBtn)) Gfx.FillRect(discBtn, 0x33ED4245, 6);
            Gfx.DrawIcon(IcPhoneOff, "✕", discBtn, 16, Th.Sub);
            if (UI.Click(UI.Id("btn_vdisc"), discBtn))
            {
                dc.LeaveVoice();
                StopStream();
                _showVoiceInfo = false;
                _inVoiceStage = false;
            }

            // 4 crisp action buttons row (Camera, Share Screen, Grid Stage, Audio Settings)
            float bw = (r.W - 28) / 4f, by = dy + 50;
            var b1 = new R(r.X + 8, by, bw, 32);
            Gfx.FillRect(b1, UI.Hover(b1) ? Th.Hover : Th.Card, 6);
            Gfx.DrawIcon(IcVideoOff, "C", b1, 16, Th.Dim);

            var b2 = new R(b1.Right + 4, by, bw, 32);
            Gfx.FillRect(b2, _streaming ? 0x3323A55A : (UI.Hover(b2) ? Th.Hover : Th.Card), 6);
            Gfx.DrawIcon(_streaming ? IcShareOff : IcShare, "S", b2, 16, _streaming ? Th.Green : Th.Text);
            if (UI.Click(UI.Id("dock_share"), b2))
            {
                if (_streaming) StopStream();
                else OpenScreenShareModal();
            }

            var b3 = new R(b2.Right + 4, by, bw, 32);
            Gfx.FillRect(b3, _inVoiceStage ? Th.Active : (UI.Hover(b3) ? Th.Hover : Th.Card), 6);
            Gfx.DrawIcon(IcGrid, "G", b3, 16, _inVoiceStage ? Th.Text : Th.Sub);
            if (UI.Click(UI.Id("dock_grid"), b3)) { _inVoiceStage = !_inVoiceStage; dc.FocusedStreamUserId = null; }

            var b4 = new R(b3.Right + 4, by, bw, 32);
            Gfx.FillRect(b4, UI.Hover(b4) ? Th.Hover : Th.Card, 6);
            Gfx.DrawIcon(IcSliders, "A", b4, 16, Th.Sub);
            if (UI.Click(UI.Id("dock_aud"), b4)) { _showSettings = true; _settingsTab = 0; RefreshHardwareListsAsync(); }

            dy += 92;
            Gfx.FillRect(new R(r.X, dy, r.W, 1), Th.Border);
        }

        // 3. User Bar with clickable Profile/Status Switcher + Split [Mic | ⌄], [Headphones | ⌄], [⚙]
        var profHit = new R(r.X + 4, dy + 7, r.W - 134, 46);
        if (UI.Hover(profHit) || _showStatusMenu) Gfx.FillRect(profHit, Th.Hover, 8);
        if (UI.Click(UI.Id("u_prof"), profHit))
        {
            _showStatusMenu = !_showStatusMenu;
            _showVoiceInfo = false; _audioPopupMode = 0; _ctxUser = null; _ctxStreamUserId = null;
        }

        string? myAvUrl = string.IsNullOrEmpty(dc.Me.Avatar) ? null : $"https://cdn.discordapp.com/avatars/{dc.Me.Id}/{dc.Me.Avatar}.png?size=64";
        var myAv = ImageCache.Get(myAvUrl, 64, ImgMask.Circle);
        var myAvR = new R(r.X + 8, dy + 14, 32, 32);
        if (_localSpeakAnim > 0.04f)
        {
            float cx = myAvR.X + 16f, cy = myAvR.Y + 16f;
            Gfx.FillCircle(cx, cy, 18.4f, Gfx.Fade(0xFF23A55A, _localSpeakAnim));
            Gfx.FillCircle(cx, cy, 16.4f, Th.Rail);
        }
        if (myAv != null) Gfx.DrawBitmap(myAv, myAvR);
        else Gfx.FillCircle(myAvR.X + 16, myAvR.Y + 16, 16, Th.Brand);
        uint stDotCol = dc.Me.Status switch { "idle" => Th.Amber, "dnd" => Th.Red, "invisible" => Th.Dim, _ => Th.Green };
        Gfx.FillCircle(myAvR.Right - 4, myAvR.Bottom - 4, 5, Th.Rail);
        Gfx.FillCircle(myAvR.Right - 4, myAvR.Bottom - 4, 3.5f, stDotCol);

        Gfx.DrawText(dc.Me.DisplayName, new R(r.X + 46, dy + 13, r.W - 178, 18), 12f, Th.Text, 700);
        string stLabel = _muted ? "Мик выкл" : (dc.Me.Status switch { "idle" => "Неактивен", "dnd" => "Не беспокоить", "invisible" => "Невидимый", _ => (dc.VoiceConnected ? "В голосовом" : "В сети") });
        Gfx.DrawText(stLabel, new R(r.X + 46, dy + 31, r.W - 178, 16), 10.5f, _muted ? Th.Red : stDotCol);

        // Split Mic button [ 🎤 | ⌄ ]
        var rMicGrp = new R(r.Right - 126, dy + 16, 44, 28);
        Gfx.FillRect(rMicGrp, _muted ? 0x33ED4245 : Th.Card, 6);
        var rMicBtn = new R(rMicGrp.X, rMicGrp.Y, 28, 28);
        if (UI.Hover(rMicBtn)) Gfx.FillRect(rMicBtn, Th.Hover, 6);
        Gfx.DrawIcon(_muted ? IcMicOff : IcMic, "M", rMicBtn, 14, _muted ? Th.Red : (_localSpeakAnim > 0.15f ? Th.Green : Th.Text));
        if (UI.Click(UI.Id("u_mute"), rMicBtn)) { _muted = !_muted; ApplyAudioSettings(); SoundFx.Play(_muted ? UiSound.Mute : UiSound.Unmute); }

        var rMicChev = new R(rMicGrp.X + 28, rMicGrp.Y, 16, 28);
        if (UI.Hover(rMicChev)) Gfx.FillRect(rMicChev, Th.Hover, 4);
        Gfx.DrawIcon(IcChevDown, "v", rMicChev, 11, Th.Sub);
        if (UI.Click(UI.Id("u_mic_pop"), rMicChev))
        {
            RefreshHardwareListsAsync();
            _showStatusMenu = false; _showVoiceInfo = false;
            _audioPopupMode = _audioPopupMode == 1 ? 0 : 1;
            _audioPopupSub = 0;
            _audioPopupX = rMicGrp.X;
            _audioPopupY = rMicGrp.Y - 8;
        }

        // Split Headphones button [ 🎧 | ⌄ ]
        var rDeafGrp = new R(r.Right - 78, dy + 16, 44, 28);
        Gfx.FillRect(rDeafGrp, _deafened ? 0x33ED4245 : Th.Card, 6);
        var rDeafBtn = new R(rDeafGrp.X, rDeafGrp.Y, 28, 28);
        if (UI.Hover(rDeafBtn)) Gfx.FillRect(rDeafBtn, Th.Hover, 6);
        Gfx.DrawIcon(_deafened ? IcHeadphoneOff : IcHeadphones, "H", rDeafBtn, 14, _deafened ? Th.Red : Th.Text);
        if (UI.Click(UI.Id("u_deaf"), rDeafBtn)) { _deafened = !_deafened; ApplyAudioSettings(); SoundFx.Play(_deafened ? UiSound.Deafen : UiSound.Undeafen); }

        var rDeafChev = new R(rDeafGrp.X + 28, rDeafGrp.Y, 16, 28);
        if (UI.Hover(rDeafChev)) Gfx.FillRect(rDeafChev, Th.Hover, 4);
        Gfx.DrawIcon(IcChevDown, "v", rDeafChev, 11, Th.Sub);
        if (UI.Click(UI.Id("u_deaf_pop"), rDeafChev))
        {
            RefreshHardwareListsAsync();
            _audioPopupMode = _audioPopupMode == 2 ? 0 : 2;
            _audioPopupSub = 2;
            _audioPopupX = rDeafGrp.X;
            _audioPopupY = rDeafGrp.Y - 8;
        }

        var rSet = new R(r.Right - 30, dy + 16, 24, 28);
        Gfx.FillRect(rSet, _showSettings ? Th.Brand : (UI.Hover(rSet) ? Th.Hover : Th.Card), 6);
        Gfx.DrawIcon(IcSettings, "⚙", rSet, 14, Th.Text);
        if (UI.Click(UI.Id("u_set"), rSet))
        {
            _showSettings = !_showSettings;
            if (_showSettings) RefreshHardwareListsAsync();
        }
    }

    // ---------------- 1-to-1 GoofCord Voice Stage (Multi-Stream Grid + Instant Focus/Unfocus!) ----------------

    private static void DrawStreamConnectingPlaceholder(R vidR, string streamerName, string diag)
    {
        Gfx.FillRect(vidR, 0xFF0E1015, 8);
        // Animated 3-dot Discord loading pulse
        int phase = (int)((Environment.TickCount64 / 260) % 3);
        float cx = vidR.X + vidR.W * 0.5f, cy = vidR.Y + vidR.H * 0.46f;
        for (int d = 0; d < 3; d++)
        {
            bool hi = d == phase;
            Gfx.FillCircle(cx + (d - 1) * 18f, cy, hi ? 5.5f : 4f, hi ? Th.Brand : 0xFF323646);
        }
        Gfx.DrawText($"Подключение к стриму {streamerName}...", new R(vidR.X + 12, cy + 16, vidR.W - 24, 20), 13, Th.Sub, 600, 1);
        if (!string.IsNullOrEmpty(diag))
            Gfx.DrawText(diag, new R(vidR.X + 12, cy + 38, vidR.W - 24, 16), 10.5f, Th.Dim, 500, 1);
        UI.Animate();
    }

    private static void DrawVoiceStage(R r)
    {
        Gfx.FillRect(r, 0xFF000000);
        var dc = DiscordClient.Instance;
        var guild = dc.Guilds.FirstOrDefault(g => g.Id == dc.ConnectedVoiceGuildId);
        var vch = guild?.Channels.FirstOrDefault(c => c.Id == dc.ConnectedVoiceChannelId);

        // Validate focused stream still active
        WatchSession? focusedWs = null;
        bool focusSelf = dc.FocusedStreamUserId == "self" && _streaming;
        if (!focusSelf && !string.IsNullOrEmpty(dc.FocusedStreamUserId))
        {
            focusedWs = dc.GetWatchSession(dc.FocusedStreamUserId);
            if (focusedWs == null) dc.FocusedStreamUserId = null;
        }
        bool isFocused = focusSelf || focusedWs != null;

        // Top Voice Stage Header (exact f_004.jpg & f_022.jpg!)
        Gfx.DrawIcon(IcVol, "🔊", new R(r.X + 16, r.Y + 12, 22, 22), 16, Th.Sub);
        string hdr = vch?.Name ?? "Голосовой канал";
        if (focusedWs != null) hdr += $"  •  Экран {focusedWs.StreamerName}";
        else if (focusSelf) hdr += "  •  Ваш экран";
        Gfx.DrawText(hdr, new R(r.X + 44, r.Y + 13, 460, 22), 14.5f, Th.Text, 700);

        if (isFocused)
        {
            var st = focusSelf ? _selfScreenStats : focusedWs!.Stats;
            string resPill = st.Width > 0
                ? $"{st.Height}P {st.Fps:F0} КАДРОВ В СЕКУНДУ"
                : (focusSelf ? "Запуск NVENC..." : "Подключение...");
            float pw = Gfx.MeasureText(resPill, 10.5f, 700, 260).W + 24;
            var rp = new R(r.Right - 134 - pw, r.Y + 12, pw, 22);
            Gfx.FillRect(rp, 0xFF1A1C23, 11);
            Gfx.DrawText(resPill, new R(rp.X, rp.Y + 4, rp.W, 14), 10.5f, Th.Text, 700, 1);

            var liveR = new R(r.Right - 126, r.Y + 12, 64, 22);
            Gfx.FillRect(liveR, Th.Red, 11);
            Gfx.DrawText("В ЭФИРЕ", new R(liveR.X, liveR.Y + 4, liveR.W, 14), 10f, 0xFFFFFFFF, 700, 1);
        }

        if (!isFocused && vch != null && UI.Button(UI.Id("vs_inv"), new R(r.Right - 144, r.Y + 10, 90, 28), "🔗 Пригласить", Th.Card, Th.Sub, 6, 11))
            _ = ShellAsync.CreateAndCopyInviteAsync(vch.Id);

        var chatBtn = new R(r.Right - 48, r.Y + 10, 34, 28);
        Gfx.FillRect(chatBtn, UI.Hover(chatBtn) ? Th.Hover : Th.Card, 6);
        Gfx.DrawIcon(IcChat, "💬", chatBtn, 16, Th.Text);
        if (UI.Click(UI.Id("vs_chat"), chatBtn)) _inVoiceStage = false;

        var stageArea = new R(r.X + 12, r.Y + 46, r.W - 24, isFocused ? (r.H - 146) : (r.H - 120));

        if (isFocused)
        {
            // MODE 2: 100% Full-Stage Zero-Copy Hardware Video (exact f_022.jpg!)
            Gfx.FillRect(stageArea, 0xFF0A0B0E, 8);
            if (focusSelf)
            {
                if (_selfScreenStats.FramesEncoded > 0)
                {
                    _selfHostRect = stageArea;
                    _wantSelfHost = true;
                }
                else DrawStreamConnectingPlaceholder(stageArea, "Ваш экран", "Инициализация NVENC...");
            }
            else if (focusedWs != null)
            {
                if (focusedWs.Stats.FramesEncoded > 0)
                    _watchSlotsLayout[focusedWs.Slot] = (true, stageArea);
                else
                    DrawStreamConnectingPlaceholder(stageArea, focusedWs.StreamerName, focusedWs.Stats.GetEncoderName());
            }

            // Clicking anywhere on the focused video returns to Grid View; right-click opens Stream Context Menu!
            if (UI.Click(UI.Id("vs_unfocus_area"), stageArea) && _ctxStreamUserId == null && _audioPopupMode == 0)
                dc.FocusedStreamUserId = null;
            if (UI.RightClick(stageArea))
            {
                _ctxUser = null; _audioPopupMode = 0;
                _ctxStreamUserId = focusSelf ? "self" : focusedWs!.StreamerUserId;
                _ctxStreamX = Math.Clamp(UI.MX, r.X + 8, Gfx.W - 248);
                _ctxStreamY = Math.Clamp(UI.MY, TopH + 8, Gfx.H - 220);
            }

            // Bottom-left streamer name pill
            var lblR = new R(r.X + 16, r.Bottom - 54, 210, 28);
            Gfx.FillRect(lblR, 0xDD111216, 6);
            Gfx.DrawIcon(IcMonitor, "", new R(lblR.X + 6, lblR.Y + 6, 16, 16), 12, Th.Sub);
            Gfx.DrawText(focusSelf ? "Ваш экран" : focusedWs!.StreamerName, new R(lblR.X + 26, lblR.Y + 6, lblR.W - 34, 16), 12, Th.Text, 600);

            // Cleanly spaced "^ Сетка" pill (zero overlap with bottom dock!)
            var gridPill = new R(r.X + (r.W - 108) * 0.5f, r.Bottom - 92, 108, 24);
            if (UI.Button(UI.Id("vs_grid"), gridPill, "▲ Сетка", 0xEE1A1C23, Th.Text, 12, 11.5f))
                dc.FocusedStreamUserId = null;

            // Bottom-right stream volume mute button
            if (focusedWs != null)
            {
                var svBtn = new R(r.Right - 92, r.Bottom - 54, 34, 34);
                Gfx.FillRect(svBtn, UI.Hover(svBtn) ? Th.Hover : Th.Card, 8);
                Gfx.DrawIcon(focusedWs.Muted || focusedWs.Volume < 0.01f ? IcVolOff : IcVol, "V", svBtn, 16, focusedWs.Muted ? Th.Red : Th.Text);
                if (UI.Click(UI.Id("vs_svol"), svBtn)) dc.ToggleStreamMute(focusedWs.StreamerUserId);
            }
        }
        else if (guild != null)
        {
            // MODE 1: Multi-Stream Grid View (exact f_004.jpg & f_037.jpg!)
            List<VoiceMember> vms;
            lock (guild.VoiceStates) vms = guild.VoiceStates.Values.Where(v => v.ChannelId == dc.ConnectedVoiceChannelId).ToList();

            var tiles = new List<(VoiceMember? Vm, bool IsStream, bool IsSelfStream)>();
            if (_streaming) tiles.Add((null, true, true));
            foreach (var vm in vms) if (vm.SelfStream && vm.UserId != dc.Me.Id) tiles.Add((vm, true, false));
            foreach (var vm in vms) tiles.Add((vm, false, false));

            if (tiles.Count == 0)
            {
                Gfx.DrawText("Подключено к голосовому каналу", new R(stageArea.X, stageArea.Y + stageArea.H * 0.45f, stageArea.W, 28), 16, Th.Sub, 600, 1);
            }
            else
            {
                int cols = tiles.Count <= 1 ? 1 : (tiles.Count <= 6 ? 2 : 3);
                int rows = (tiles.Count + cols - 1) / cols;
                float gap = 12f;
                float maxTileW = (stageArea.W - gap * (cols - 1)) / cols;
                float maxTileH = (stageArea.H - gap * (rows - 1)) / rows;
                float tw = MathF.Min(maxTileW, maxTileH * 1.78f);
                float th = MathF.Min(maxTileH, tw / 1.68f);
                if (tw < maxTileW * 0.85f) tw = MathF.Min(maxTileW, th * 1.78f);

                float totalGridH = rows * th + (rows - 1) * gap;
                float oy = stageArea.Y + (stageArea.H - totalGridH) * 0.5f;

                for (int i = 0; i < tiles.Count; i++)
                {
                    var (vm, isStream, isSelfStream) = tiles[i];
                    int row = i / cols;
                    int col = i % cols;
                    int itemsInRow = (row == rows - 1) ? (tiles.Count - row * cols) : cols;
                    float rowW = itemsInRow * tw + (itemsInRow - 1) * gap;
                    float ox = stageArea.X + (stageArea.W - rowW) * 0.5f;
                    var tr = new R(ox + col * (tw + gap), oy + row * (th + gap), tw, th);

                    if (isSelfStream)
                    {
                        Gfx.FillRect(tr, 0xFF0E1015, 10);
                        Gfx.StrokeRect(tr, Th.Green, 1.5f, 10);
                        var vidR = new R(tr.X + 2, tr.Y + 2, tr.W - 4, MathF.Max(16, tr.H - 28));
                        if (_selfScreenStats.FramesEncoded > 0)
                        {
                            _selfHostRect = vidR;
                            _wantSelfHost = true;
                        }
                        else DrawStreamConnectingPlaceholder(vidR, "Ваш экран", "Запуск NVENC...");

                        var barR = new R(tr.X + 2, tr.Bottom - 26, tr.W - 4, 24);
                        Gfx.FillRect(barR, 0xFF101218, 6);
                        Gfx.DrawIcon(IcMonitor, "", new R(barR.X + 6, barR.Y + 4, 16, 16), 12, Th.Green);
                        Gfx.DrawText("Ваш экран", new R(barR.X + 24, barR.Y + 4, barR.W - 140, 16), 11.5f, Th.Text, 600);
                        string badge = _selfScreenStats.Width > 0 ? $"{_selfScreenStats.Height}P {_selfScreenStats.Fps:F0}FPS" : "LIVE";
                        Gfx.DrawText(badge, new R(barR.Right - 134, barR.Y + 4, 72, 16), 10f, Th.Sub, 700, 2);
                        Gfx.FillRect(new R(barR.Right - 56, barR.Y + 4, 52, 16), Th.Red, 8);
                        Gfx.DrawText("В ЭФИРЕ", new R(barR.Right - 56, barR.Y + 5, 52, 14), 9f, 0xFFFFFFFF, 700, 1);

                        if (UI.Click(UI.Id("tile_self_focus"), tr) && _ctxStreamUserId == null && _audioPopupMode == 0)
                            dc.FocusedStreamUserId = "self";
                        if (UI.RightClick(tr))
                        {
                            _ctxUser = null; _audioPopupMode = 0;
                            _ctxStreamUserId = "self";
                            _ctxStreamX = Math.Clamp(UI.MX, r.X + 8, Gfx.W - 248);
                            _ctxStreamY = Math.Clamp(UI.MY, TopH + 8, Gfx.H - 220);
                        }
                    }
                    else if (isStream && vm != null)
                    {
                        var ws = dc.GetWatchSession(vm.UserId);
                        if (ws != null)
                        {
                            Gfx.FillRect(tr, 0xFF0E1015, 10);
                            Gfx.StrokeRect(tr, UI.Hover(tr) ? 0x55FFFFFF : Th.Border, 1f, 10);
                            var vidR = new R(tr.X + 2, tr.Y + 2, tr.W - 4, MathF.Max(16, tr.H - 28));
                            if (ws.Stats.FramesEncoded > 0)
                                _watchSlotsLayout[ws.Slot] = (true, vidR);
                            else
                                DrawStreamConnectingPlaceholder(vidR, vm.Username, ws.Stats.GetEncoderName());

                            var barR = new R(tr.X + 2, tr.Bottom - 26, tr.W - 4, 24);
                            Gfx.FillRect(barR, 0xFF101218, 6);
                            Gfx.DrawIcon(IcMonitor, "", new R(barR.X + 6, barR.Y + 4, 16, 16), 12, Th.Cyan);
                            Gfx.DrawText(vm.Username, new R(barR.X + 24, barR.Y + 4, barR.W - 156, 16), 11.5f, Th.Text, 600);
                            string qTxt = ws.Stats.Width > 0 ? $"{ws.Stats.Height}P {ws.Stats.Fps:F0}FPS" : "Загрузка...";
                            Gfx.DrawText(qTxt, new R(barR.Right - 148, barR.Y + 4, 86, 16), 10f, Th.Sub, 700, 2);
                            Gfx.FillRect(new R(barR.Right - 56, barR.Y + 4, 52, 16), Th.Red, 8);
                            Gfx.DrawText("В ЭФИРЕ", new R(barR.Right - 56, barR.Y + 5, 52, 14), 9f, 0xFFFFFFFF, 700, 1);

                            if (UI.Click(UI.Id("tile_focus_" + vm.UserId), tr) && _ctxStreamUserId == null && _audioPopupMode == 0)
                                dc.FocusedStreamUserId = vm.UserId;
                            if (UI.RightClick(tr))
                            {
                                _ctxUser = null; _audioPopupMode = 0;
                                _ctxStreamUserId = vm.UserId;
                                _ctxStreamX = Math.Clamp(UI.MX, r.X + 8, Gfx.W - 248);
                                _ctxStreamY = Math.Clamp(UI.MY, TopH + 8, Gfx.H - 220);
                            }
                        }
                        else
                        {
                            Gfx.FillRect(tr, 0xFF14161D, 10);
                            Gfx.StrokeRect(tr, UI.Hover(tr) ? 0x44FFFFFF : Th.Border, 1f, 10);

                            var livePill = new R(tr.Right - 68, tr.Y + 10, 58, 20);
                            Gfx.FillRect(livePill, Th.Red, 10);
                            Gfx.DrawText("В ЭФИРЕ", new R(livePill.X, livePill.Y + 3, livePill.W, 14), 9.5f, 0xFFFFFFFF, 700, 1);

                            float grpW = 148 + 8 + 38;
                            float gx = tr.X + (tr.W - grpW) * 0.5f, gy = tr.Y + (tr.H - 36) * 0.5f;
                            if (UI.Button(UI.Id("tile_watch_" + vm.UserId), new R(gx, gy, 148, 36), "Смотреть стрим", 0xDD2B2D31, Th.Text, 8, 12.5f, 700))
                                _ = dc.WatchStreamAsync(guild.Id, dc.ConnectedVoiceChannelId, vm.UserId, vm.Username, focusImmediately: true);

                            var gridWatchBtn = new R(gx + 156, gy, 38, 36);
                            Gfx.FillRect(gridWatchBtn, UI.Hover(gridWatchBtn) ? Th.Hover : 0xDD2B2D31, 8);
                            Gfx.DrawIcon(IcEye, "👁", gridWatchBtn, 16, Th.Text);
                            if (UI.Click(UI.Id("tile_watch_grid_" + vm.UserId), gridWatchBtn))
                                _ = dc.WatchStreamAsync(guild.Id, dc.ConnectedVoiceChannelId, vm.UserId, vm.Username, focusImmediately: false);

                            var nb = new R(tr.X + 10, tr.Bottom - 30, MathF.Min(tr.W - 20, 220), 22);
                            Gfx.FillRect(nb, 0xCC0A0B0E, 6);
                            Gfx.DrawIcon(IcMonitor, "", new R(nb.X + 5, nb.Y + 3, 16, 16), 12, Th.Sub);
                            Gfx.DrawText(vm.Username, new R(nb.X + 24, nb.Y + 3, nb.W - 30, 16), 11.5f, Th.Text, 600);
                        }
                    }
                    else if (vm != null)
                    {
                        bool isMe = vm.UserId == dc.Me.Id;
                        bool spk = dc.IsUserActuallySpeaking(vm);
                        float sa = isMe ? _localSpeakAnim : StepSpeakAnim(vm.UserId, spk);
                        float lvl = isMe ? _localVoiceLevel : (spk ? 0.72f : 0f);
                        bool isMuted = vm.SelfMute || (isMe && _muted);
                        uint cardBg = ImageCache.GetCardColor(vm.AvatarUrl, vm.UserId);
                        Gfx.FillRect(tr, cardBg, 10);
                        if (sa > 0.02f)
                        {
                            Gfx.FillRect(tr, Gfx.Fade(Th.Green, 0.08f * sa), 10);
                            Gfx.StrokeRect(tr.Inset(-1f), Gfx.Fade(0xFF34D399, 0.30f * sa), 4.5f, 11);
                            Gfx.StrokeRect(tr, Gfx.Fade(0xFF34D399, sa), 2.4f, 10);
                        }
                        else
                        {
                            Gfx.StrokeRect(tr, UI.Hover(tr) ? 0x33FFFFFF : Th.Border, 1f, 10);
                        }

                        var av = ImageCache.Get(vm.AvatarUrl, 128, ImgMask.Circle);
                        float avSz = MathF.Min(96f, th * 0.44f);
                        var avR = new R(tr.X + (tr.W - avSz) * 0.5f, tr.Y + (tr.H - avSz) * 0.5f - 4, avSz, avSz);
                        if (sa > 0.02f)
                        {
                            float cx = avR.X + avSz * 0.5f, cy = avR.Y + avSz * 0.5f;
                            Gfx.FillCircle(cx, cy, avSz * 0.5f + 5.5f + lvl * 11f, Gfx.Fade(0xFF23A55A, 0.22f * sa));
                            Gfx.FillCircle(cx, cy, avSz * 0.5f + 3.6f, Gfx.Fade(0xFF34D399, sa));
                            Gfx.FillCircle(cx, cy, avSz * 0.5f + 1.2f, cardBg);
                        }
                        if (av != null) Gfx.DrawBitmap(av, avR);
                        else
                        {
                            Gfx.FillCircle(avR.X + avSz * 0.5f, avR.Y + avSz * 0.5f, avSz * 0.5f, Th.Brand);
                            Gfx.DrawText(vm.Username.Length > 0 ? vm.Username[..1].ToUpperInvariant() : "?", new R(avR.X, avR.Y + avSz * 0.30f, avSz, 28), 24, 0xFFFFFFFF, 700, 1);
                        }

                        var nb = new R(tr.X + 10, tr.Bottom - 30, MathF.Min(tr.W - 20, 224), 22);
                        Gfx.FillRect(nb, 0xCC0A0B0E, 6);
                        if (sa > 0.04f) Gfx.StrokeRect(nb, Gfx.Fade(0xFF34D399, 0.55f * sa), 1f, 6);
                        if (isMuted)
                        {
                            Gfx.DrawIcon(IcMicOff, "M", new R(nb.X + 5, nb.Y + 3, 16, 16), 12, Th.Red);
                            Gfx.DrawText(vm.Username, new R(nb.X + 24, nb.Y + 3, nb.W - 30, 16), 11.5f, Th.Text, 600);
                        }
                        else if (sa > 0.05f)
                        {
                            DrawVoiceBars(nb.X + 7, nb.Y + 11, 3, 11f, MathF.Max(0.32f, lvl), vm.UserId.GetHashCode() * 0.17f, 0xFF34D399, sa);
                            Gfx.DrawText(vm.Username, new R(nb.X + 23, nb.Y + 3, nb.W - 29, 16), 11.5f, 0xFFFFFFFF, 700);
                        }
                        else
                        {
                            Gfx.DrawText(vm.Username, new R(nb.X + 8, nb.Y + 3, nb.W - 16, 16), 11.5f, Th.Text, 600);
                        }

                        if (UI.RightClick(tr))
                        {
                            _ctxStreamUserId = null; _audioPopupMode = 0;
                            _ctxUser = vm;
                            _ctxGuildId = guild.Id;
                            _ctxX = Math.Clamp(UI.MX, 8, Gfx.W - 252);
                            _ctxY = Math.Clamp(UI.MY, TopH + 8, Gfx.H - 220);
                        }
                    }
                }
            }
        }

        // Exact GoofCord Floating Bottom Control Bar with tooltips
        float barW = 304, barH = 48;
        var bar = new R(r.X + (r.W - barW) * 0.5f, r.Bottom - 60, barW, barH);
        Gfx.FillRect(bar, 0xEE18191E, 14);
        Gfx.StrokeRect(bar, Th.Border, 1, 14);
        string barTip = ""; float barTipX = 0;

        var bmGrp = new R(bar.X + 10, bar.Y + 6, 56, 36);
        Gfx.FillRect(bmGrp, _muted ? 0x33ED4245 : (_localSpeakAnim > 0.02f ? Gfx.Fade(Th.Green, 0.24f * _localSpeakAnim) : Th.Card), 10);
        if (!_muted && _localSpeakAnim > 0.02f)
            Gfx.StrokeRect(bmGrp, Gfx.Fade(0xFF34D399, 0.7f * _localSpeakAnim), 1.35f, 10);
        var bmBtn = new R(bmGrp.X, bmGrp.Y, 36, 36);
        if (UI.Hover(bmBtn)) { Gfx.FillRect(bmBtn, Th.Hover, 10); barTip = _muted ? "Включить микрофон" : "Выключить микрофон"; barTipX = bmBtn.X + 18; }
        Gfx.DrawIcon(_muted ? IcMicOff : IcMic, "M", bmBtn, 16, _muted ? Th.Red : (_localSpeakAnim > 0.15f ? 0xFF34D399 : Th.Text));
        if (UI.Click(UI.Id("vb_mute"), bmBtn)) { _muted = !_muted; ApplyAudioSettings(); SoundFx.Play(_muted ? UiSound.Mute : UiSound.Unmute); }

        var bmChev = new R(bmGrp.X + 36, bmGrp.Y, 20, 36);
        if (UI.Hover(bmChev)) { Gfx.FillRect(bmChev, Th.Hover, 6); barTip = "Устройства аудио"; barTipX = bmChev.X + 10; }
        Gfx.DrawIcon(IcChevDown, "v", bmChev, 12, Th.Sub);
        if (UI.Click(UI.Id("vb_mic_pop"), bmChev))
        {
            RefreshHardwareListsAsync();
            _ctxUser = null; _ctxStreamUserId = null;
            _audioPopupMode = _audioPopupMode == 1 ? 0 : 1;
            _audioPopupSub = 0;
            _audioPopupX = bmGrp.X;
            _audioPopupY = bar.Y - 8;
        }

        var bdBtn = new R(bmGrp.Right + 8, bar.Y + 6, 40, 36);
        if (UI.Hover(bdBtn)) { barTip = _deafened ? "Включить звук" : "Отключить звук"; barTipX = bdBtn.X + 20; }
        Gfx.FillRect(bdBtn, _deafened ? 0x33ED4245 : (UI.Hover(bdBtn) ? Th.Hover : Th.Card), 10);
        Gfx.DrawIcon(_deafened ? IcHeadphoneOff : IcHeadphones, "H", bdBtn, 16, _deafened ? Th.Red : Th.Text);
        if (UI.Click(UI.Id("vb_deaf"), bdBtn)) { _deafened = !_deafened; ApplyAudioSettings(); SoundFx.Play(_deafened ? UiSound.Deafen : UiSound.Undeafen); }

        var bsBtn = new R(bdBtn.Right + 8, bar.Y + 6, 40, 36);
        if (UI.Hover(bsBtn)) { barTip = _streaming ? "Остановить трансляцию" : "Демонстрация экрана"; barTipX = bsBtn.X + 20; }
        Gfx.FillRect(bsBtn, _streaming ? 0x3323A55A : (UI.Hover(bsBtn) ? Th.Hover : Th.Card), 10);
        Gfx.DrawIcon(_streaming ? IcShareOff : IcShare, "S", bsBtn, 16, _streaming ? Th.Green : Th.Text);
        if (UI.Click(UI.Id("vb_share"), bsBtn))
        {
            if (_streaming) StopStream();
            else OpenScreenShareModal();
        }

        var bgBtn = new R(bsBtn.Right + 8, bar.Y + 6, 40, 36);
        if (UI.Hover(bgBtn)) { barTip = "Сетка участников"; barTipX = bgBtn.X + 20; }
        Gfx.FillRect(bgBtn, UI.Hover(bgBtn) ? Th.Hover : Th.Card, 10);
        Gfx.DrawIcon(IcGrid, "G", bgBtn, 16, Th.Text);
        if (UI.Click(UI.Id("vb_grid"), bgBtn)) dc.FocusedStreamUserId = null;

        var bsetBtn = new R(bgBtn.Right + 8, bar.Y + 6, 40, 36);
        if (UI.Hover(bsetBtn)) { barTip = "Настройки голоса"; barTipX = bsetBtn.X + 20; }
        Gfx.FillRect(bsetBtn, UI.Hover(bsetBtn) ? Th.Hover : Th.Card, 10);
        Gfx.DrawIcon(IcSettings, "⚙", bsetBtn, 16, Th.Text);
        if (UI.Click(UI.Id("vb_set"), bsetBtn)) { _showSettings = true; _settingsTab = 0; RefreshHardwareListsAsync(); }

        var bdisc = new R(bsetBtn.Right + 8, bar.Y + 6, 46, 36);
        if (UI.Hover(bdisc)) { barTip = "Отключиться"; barTipX = bdisc.X + 23; }
        Gfx.FillRect(bdisc, Th.Red, 10);
        if (UI.Hover(bdisc)) Gfx.FillRect(bdisc, Th.Hover, 10);
        Gfx.DrawIcon(IcPhoneOff, "✕", bdisc, 17, 0xFFFFFFFF);
        if (UI.Click(UI.Id("vb_disc"), bdisc))
        {
            dc.LeaveVoice();
            StopStream();
            _inVoiceStage = false;
        }

        if (!string.IsNullOrEmpty(barTip) && _audioPopupMode == 0 && _ctxStreamUserId == null && !isFocused)
        {
            float ttw = Gfx.MeasureText(barTip, 11.5f, 600, 220).W + 18;
            var ttr = new R(Math.Clamp(barTipX - ttw * 0.5f, r.X + 8, r.Right - ttw - 8), bar.Y - 32, ttw, 24);
            Gfx.FillRect(ttr, 0xF5111216, 6);
            Gfx.StrokeRect(ttr, Th.Border, 1, 6);
            Gfx.DrawText(barTip, new R(ttr.X, ttr.Y + 4, ttr.W, 16), 11.5f, Th.Text, 600, 1);
            _overlayCutouts.Add(ttr);
        }

        // Bottom-right Fullscreen toggle (exact f_004.jpg & f_022.jpg!)
        var fsBtn = new R(r.Right - 48, r.Bottom - 54, 34, 34);
        Gfx.FillRect(fsBtn, UI.Hover(fsBtn) ? Th.Hover : Th.Card, 8);
        Gfx.DrawIcon(W.IsZoomed(App.Hwnd) ? IcMin : IcMax, "⛶", fsBtn, 16, Th.Text);
        if (UI.Click(UI.Id("vs_fs"), fsBtn))
            W.ShowWindow(App.Hwnd, W.IsZoomed(App.Hwnd) ? W.SW_SHOWNORMAL : W.SW_MAXIMIZE);
    }

    // ---------------- Text Chat Stage (Search, Pins, Member List, Markdown Code Blocks, Lightbox, Reactions, Edit, Emoji) ----------------

    private static void DrawMainStage(R r)
    {
        Gfx.FillRect(r, Th.Bg);
        var dc = DiscordClient.Instance;
        var guild = dc.Guilds.FirstOrDefault(g => g.Id == dc.ActiveGuildId);
        var ch = (dc.ActiveGuildId == "@me" ? dc.DmChannels : guild?.Channels)?.FirstOrDefault(c => c.Id == dc.ActiveChannelId);

        // Channel Header Bar
        Gfx.FillRect(new R(r.X, r.Y, r.W, 44), Th.Panel);
        Gfx.FillRect(new R(r.X, r.Y + 44, r.W, 1), Th.Border);
        Gfx.DrawText("# " + (ch?.Name ?? "выберите-канал"), new R(r.X + 16, r.Y + 12, 240, 22), 15, Th.Text, 700);

        float rightX = r.Right - 10;
        bool showMembers = SettingsStore.Current.ShowMemberList && guild != null && r.W > 760;

        // Right-side header controls: [👥 Members] [📌 Pins] [🔍 Search] [Stream / Back to Voice]
        if (guild != null)
        {
            rightX -= 32;
            var mbBtn = new R(rightX, r.Y + 8, 28, 28);
            if (UI.Button(UI.Id("hdr_mem"), mbBtn, "👥", showMembers ? Th.Active : Th.Card, showMembers ? Th.Text : Th.Sub, 6, 13))
            { SettingsStore.Current.ShowMemberList = !SettingsStore.Current.ShowMemberList; SettingsStore.Save(); }
            rightX -= 6;
        }
        if (ch != null)
        {
            rightX -= 32;
            var pinBtn = new R(rightX, r.Y + 8, 28, 28);
            if (UI.Button(UI.Id("hdr_pin"), pinBtn, "📌", _showPinsPopup ? Th.Active : Th.Card, Th.Text, 6, 13))
            {
                _showPinsPopup = !_showPinsPopup;
                if (_showPinsPopup) _ = dc.LoadPinnedMessagesAsync(ch.Id);
            }
            rightX -= 6;

            rightX -= 32;
            var srcBtn = new R(rightX, r.Y + 8, 28, 28);
            if (UI.Button(UI.Id("hdr_src"), srcBtn, "🔍", _showChannelSearch ? Th.Brand : Th.Card, Th.Text, 6, 13))
                ToggleChannelSearch();
            rightX -= 8;
        }

        if (dc.VoiceConnected || dc.IsWatchingAnyStream || _streaming)
        {
            rightX -= 154;
            if (UI.Button(UI.Id("hdr_vstage"), new R(rightX, r.Y + 8, 154, 28), "🔊 К стриму / голосу", Th.Green, 0xFFFFFFFF, 6, 11.5f))
                _inVoiceStage = true;
            rightX -= 8;
        }
        else
        {
            rightX -= 130;
            if (UI.Button(UI.Id("hdr_4k"), new R(rightX, r.Y + 8, 130, 28), "▶ Screen Share", Th.Card, Th.Cyan, 6, 12))
                OpenScreenShareModal();
            rightX -= 8;
        }

        if (!string.IsNullOrEmpty(ch?.Topic) && rightX - (r.X + 250) > 60)
            Gfx.DrawText(ch.Topic, new R(r.X + 250, r.Y + 14, rightX - (r.X + 256), 20), 12.5f, Th.Sub);

        float memW = showMembers ? 224f : 0f;
        float chatW = r.W - memW;
        float contentTop = r.Y + 45;

        // Live In-Channel Search Bar (Ctrl+F / 🔍)
        if (_showChannelSearch && !_showQuickSwitch)
        {
            var sbBar = new R(r.X, contentTop, chatW, 42);
            Gfx.FillRect(sbBar, Th.Sidebar);
            Gfx.FillRect(new R(sbBar.X, sbBar.Bottom - 1, sbBar.W, 1), Th.Border);
            Gfx.DrawText("🔍 Поиск в канале:", new R(sbBar.X + 14, sbBar.Y + 12, 132, 18), 12f, Th.Sub, 600);
            _searchInputRect = new R(sbBar.X + 148, sbBar.Y + 6, MathF.Max(140, chatW - 246), 30);
            _wantSearchInput = !_showSettings && !_showShareModal && _lightboxUrl == null;
            Gfx.FillRect(_searchInputRect, Th.Panel, 6);
            Gfx.StrokeRect(_searchInputRect, Th.Brand, 1, 6);
            if (UI.Button(UI.Id("cs_cls"), new R(sbBar.Right - 86, sbBar.Y + 7, 74, 28), "Закрыть", Th.Card, Th.Sub, 6, 11.5f))
            { _showChannelSearch = false; _searchQuery = ""; App.SetSearchInputText(""); }
            contentTop += 42;
        }

        var msgArea = new R(r.X, contentTop, chatW, r.Bottom - contentTop - InputH - 12);
        if (UI.Hover(msgArea) && UI.Wheel != 0) _chatScroll = MathF.Max(0, _chatScroll + UI.Wheel * 64f);

        Gfx.PushClip(msgArea);
        if (ch != null && dc.Messages.TryGetValue(ch.Id, out var list))
        {
            List<DiscordMessage> snap;
            lock (list) snap = [.. list];
            if (_showChannelSearch && !string.IsNullOrWhiteSpace(_searchQuery))
            {
                string q = _searchQuery;
                snap = snap.Where(x => x.Content.Contains(q, StringComparison.OrdinalIgnoreCase) || x.AuthorName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
                Gfx.DrawText($"Найдено сообщений: {snap.Count}", new R(msgArea.X + 16, msgArea.Y + 6, 260, 18), 11.5f, Th.Cyan, 600);
            }

            float y = msgArea.Bottom - 14 + _chatScroll;
            float totalMsgH = 0;

            for (int i = snap.Count - 1; i >= 0; i--)
            {
                var m = snap[i];
                bool grouped = i > 0 && snap[i - 1].AuthorId == m.AuthorId && snap[i - 1].Timestamp == m.Timestamp && string.IsNullOrEmpty(m.ReplyPreview);
                float textW = msgArea.W - 88;
                bool hasCode = m.Content.Contains("```", StringComparison.Ordinal);
                bool isQuote = !hasCode && m.Content.TrimStart().StartsWith("> ", StringComparison.Ordinal);
                float textH = string.IsNullOrEmpty(m.Content) ? 0f : Gfx.MeasureText(m.Content, 14f, 400, textW).H + (hasCode ? 16f : 0f);
                float replyH = string.IsNullOrEmpty(m.ReplyPreview) ? 0f : 18f;

                // Measure ALL attachments (images + file cards!)
                float attTotalH = 0f;
                var imgAtts = new List<(string Url, Vortice.Direct2D1.ID2D1Bitmap1? Bmp, float W, float H)>();
                var fileAtts = new List<DiscordAttachment>();
                if (m.Attachments.Count > 0)
                {
                    foreach (var a in m.Attachments.Take(4))
                    {
                        if (a.IsImage)
                        {
                            var b = ImageCache.Get(a.Url, 420, ImgMask.None);
                            float dh = b != null ? MathF.Min(240f, b.PixelSize.Height * MathF.Min(1f, 360f / MathF.Max(1, b.PixelSize.Width))) : 132f;
                            float dw = b != null ? MathF.Min(360f, b.PixelSize.Width * (dh / MathF.Max(1, b.PixelSize.Height))) : 210f;
                            imgAtts.Add((a.Url, b, dw, dh));
                            attTotalH += dh + 6f;
                        }
                        else
                        {
                            fileAtts.Add(a);
                            attTotalH += 44f;
                        }
                    }
                }
                else if (m.AttachmentUrls.Count > 0)
                {
                    string u = m.AttachmentUrls[0];
                    if (u.Contains(".png", StringComparison.OrdinalIgnoreCase) || u.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        u.Contains(".jpeg", StringComparison.OrdinalIgnoreCase) || u.Contains(".webp", StringComparison.OrdinalIgnoreCase) ||
                        u.Contains(".gif", StringComparison.OrdinalIgnoreCase) || u.Contains(".bmp", StringComparison.OrdinalIgnoreCase))
                    {
                        var b = ImageCache.Get(u, 420, ImgMask.None);
                        float dh = b != null ? MathF.Min(240f, b.PixelSize.Height * MathF.Min(1f, 360f / MathF.Max(1, b.PixelSize.Width))) : 132f;
                        float dw = b != null ? MathF.Min(360f, b.PixelSize.Width * (dh / MathF.Max(1, b.PixelSize.Height))) : 210f;
                        imgAtts.Add((u, b, dw, dh));
                        attTotalH += dh + 6f;
                    }
                }

                float embH = 0f;
                foreach (var eb in m.Embeds)
                {
                    float eImgH = !string.IsNullOrEmpty(eb.ImageUrl) ? 148f : 0f;
                    float eMeta = (!string.IsNullOrEmpty(eb.Author) ? 16f : 0f) + (!string.IsNullOrEmpty(eb.Footer) ? 16f : 0f);
                    embH += 22f + eMeta + eImgH + (string.IsNullOrEmpty(eb.Description) ? 0f : Gfx.MeasureText(eb.Description, 12.5f, 400, MathF.Min(420, textW - 24)).H) + 14f;
                }
                float reactH = m.Reactions.Count > 0 ? 28f : 0f;
                float headerH = grouped ? 4f : 24f;
                float rowH = MathF.Max(grouped ? 24f : 44f, replyH + headerH + textH + attTotalH + embH + reactH + 8f);
                totalMsgH += rowH;
                y -= rowH;

                if (y + rowH < msgArea.Y - 40 || y > msgArea.Bottom + 40) continue;

                var rowRect = new R(msgArea.X + 4, y, msgArea.W - 12, rowH);
                bool rowHover = UI.Hover(rowRect);
                if (rowHover) Gfx.FillRect(rowRect, 0x0AFFFFFF, 6);

                float cy = y + 2;
                if (!string.IsNullOrEmpty(m.ReplyPreview)) { Gfx.DrawText(m.ReplyPreview, new R(msgArea.X + 64, cy, textW, 16), 11.5f, Th.Dim); cy += 18; }
                if (!grouped)
                {
                    var avBmp = ImageCache.Get(m.AvatarUrl, 64, ImgMask.Circle);
                    var avR = new R(msgArea.X + 18, cy + 2, 36, 36);
                    if (avBmp != null) Gfx.DrawBitmap(avBmp, avR);
                    else { Gfx.FillCircle(avR.X + 18, avR.Y + 18, 18, m.AuthorColor); Gfx.DrawText(m.AuthorName.Length > 0 ? m.AuthorName[..1].ToUpperInvariant() : "?", new R(avR.X, avR.Y + 9, 36, 20), 14, 0xFF0A0B0E, 700, 1); }
                    float nw = Gfx.MeasureText(m.AuthorName, 14, 700, 260).W;
                    var nameR = new R(msgArea.X + 64, cy, MathF.Min(260, nw + 4), 20);
                    Gfx.DrawText(m.AuthorName, nameR, 14, m.AuthorColor, 700);
                    if ((UI.Click(UI.Id("mav", i), avR) || UI.Click(UI.Id("mna", i), nameR)) && !string.IsNullOrEmpty(m.AuthorId) && m.AuthorId != "sys")
                        OpenUserProfile(m.AuthorId, guild?.Id ?? "", UI.MX, UI.MY);
                    Gfx.DrawText(m.Timestamp, new R(msgArea.X + 64 + nw + 10, cy + 2, 80, 18), 11.5f, Th.Dim);
                    cy += 22;
                }
                if (!string.IsNullOrEmpty(m.Content))
                {
                    if (hasCode)
                    {
                        var codeCard = new R(msgArea.X + 64, cy, MathF.Min(textW, 640), textH - 4);
                        Gfx.FillRect(codeCard, 0xFF0E1016, 6);
                        Gfx.StrokeRect(codeCard, Th.Border, 1, 6);
                        Gfx.DrawText(m.Content.Replace("```", "").Trim(), new R(codeCard.X + 10, codeCard.Y + 6, codeCard.W - 20, codeCard.H - 10), 13, 0xFFE2E8F0, 400, 0, true);
                        cy += textH + 2;
                    }
                    else if (isQuote)
                    {
                        Gfx.FillRect(new R(msgArea.X + 64, cy + 2, 3.5f, MathF.Max(16, textH - 2)), Th.Dim, 1.5f);
                        Gfx.DrawText(m.Content.TrimStart().Substring(2), new R(msgArea.X + 74, cy, textW - 10, textH + 6), 14, Th.Sub, 400, 0, true);
                        cy += textH + 4;
                    }
                    else
                    {
                        Gfx.DrawText(m.Content, new R(msgArea.X + 64, cy, textW, textH + 6), 14, Th.Text, 400, 0, true);
                        cy += textH + 4;
                    }
                }
                for (int ai = 0; ai < imgAtts.Count; ai++)
                {
                    var (u, bmp, dw, dh) = imgAtts[ai];
                    var imgR = new R(msgArea.X + 64, cy + 2, dw, dh);
                    if (bmp != null)
                    {
                        Gfx.DrawBitmap(bmp, imgR);
                        if (UI.Hover(imgR)) Gfx.StrokeRect(imgR, Th.Brand, 1.5f, 4);
                        if (UI.Click(UI.Id("mimg_" + i, ai), imgR)) _lightboxUrl = u;
                    }
                    else { Gfx.FillRect(imgR, Th.Panel, 8); Gfx.DrawText("Загрузка...", new R(imgR.X, imgR.Y + dh * 0.42f, imgR.W, 20), 12, Th.Dim, 500, 1); }
                    cy += dh + 6f;
                }
                for (int fi = 0; fi < fileAtts.Count; fi++)
                {
                    var fa = fileAtts[fi];
                    var fr = new R(msgArea.X + 64, cy + 2, MathF.Min(360, textW), 38);
                    Gfx.FillRect(fr, UI.Hover(fr) ? Th.Hover : Th.Panel, 8);
                    Gfx.StrokeRect(fr, Th.Border, 1, 8);
                    string szStr = fa.Size >= 1024 * 1024 ? $"{fa.Size / (1024f * 1024f):F1} МБ" : (fa.Size >= 1024 ? $"{fa.Size / 1024f:F0} КБ" : $"{fa.Size} Б");
                    Gfx.DrawText($"📄  {fa.Filename}", new R(fr.X + 10, fr.Y + 4, fr.W - 20, 16), 12.5f, Th.Cyan, 600);
                    Gfx.DrawText($"{szStr} • Нажмите, чтобы открыть/скачать", new R(fr.X + 10, fr.Y + 20, fr.W - 20, 14), 10.5f, Th.Dim);
                    if (UI.Click(UI.Id("mfil_" + i, fi), fr)) W.OpenUrl(fa.Url);
                    cy += 44f;
                }
                for (int ei = 0; ei < m.Embeds.Count; ei++)
                {
                    var eb = m.Embeds[ei];
                    float ew = MathF.Min(440, textW);
                    float dh = string.IsNullOrEmpty(eb.Description) ? 0f : Gfx.MeasureText(eb.Description, 12.5f, 400, ew - 24).H;
                    float eImgH = !string.IsNullOrEmpty(eb.ImageUrl) ? 148f : 0f;
                    float eMeta = (!string.IsNullOrEmpty(eb.Author) ? 16f : 0f) + (!string.IsNullOrEmpty(eb.Footer) ? 16f : 0f);
                    float bh = 22f + eMeta + dh + eImgH + 10f;
                    Gfx.FillRect(new R(msgArea.X + 64, cy + 2, ew, bh), Th.Panel, 6);
                    Gfx.FillRect(new R(msgArea.X + 64, cy + 2, 4, bh), eb.Color, 2);
                    float ey = cy + 6;
                    if (!string.IsNullOrEmpty(eb.Author)) { Gfx.DrawText(eb.Author, new R(msgArea.X + 76, ey, ew - 20, 15), 11, Th.Sub, 600); ey += 16; }
                    if (!string.IsNullOrEmpty(eb.Title))
                    {
                        var tr = new R(msgArea.X + 76, ey, ew - 20, 18);
                        Gfx.DrawText(eb.Title, tr, 13, !string.IsNullOrEmpty(eb.Url) ? Th.Cyan : Th.Text, 700);
                        if (!string.IsNullOrEmpty(eb.Url) && UI.Click(UI.Id("memb_" + i, ei), tr)) W.OpenUrl(eb.Url);
                        ey += 18;
                    }
                    if (!string.IsNullOrEmpty(eb.Description)) { Gfx.DrawText(eb.Description, new R(msgArea.X + 76, ey, ew - 20, dh + 4), 12.5f, Th.Sub, 400, 0, true); ey += dh + 4; }
                    if (!string.IsNullOrEmpty(eb.ImageUrl))
                    {
                        var ebmp = ImageCache.Get(eb.ImageUrl, 400, ImgMask.None);
                        var eir = new R(msgArea.X + 76, ey + 2, MathF.Min(260, ew - 24), 140);
                        if (ebmp != null)
                        {
                            float sc = MathF.Min(1f, MathF.Min(eir.W / MathF.Max(1, ebmp.PixelSize.Width), eir.H / MathF.Max(1, ebmp.PixelSize.Height)));
                            var fitR = new R(eir.X, eir.Y, ebmp.PixelSize.Width * sc, ebmp.PixelSize.Height * sc);
                            Gfx.DrawBitmap(ebmp, fitR);
                            if (UI.Click(UI.Id("mebi_" + i, ei), fitR)) _lightboxUrl = eb.ImageUrl;
                        }
                        ey += 148;
                    }
                    if (!string.IsNullOrEmpty(eb.Footer)) Gfx.DrawText(eb.Footer, new R(msgArea.X + 76, ey, ew - 20, 15), 10.5f, Th.Dim);
                    cy += bh + 6f;
                }
                if (m.Reactions.Count > 0)
                {
                    float rx = msgArea.X + 64;
                    int rIdx = 0;
                    foreach (var re in m.Reactions.Take(8))
                    {
                        float rw = Gfx.MeasureText(re, 12, 600, 100).W + 16;
                        var rr = new R(rx, cy + 2, rw, 22);
                        Gfx.FillRect(rr, UI.Hover(rr) ? Th.Hover : Th.Card, 6);
                        Gfx.StrokeRect(rr, Th.Border, 1, 6);
                        Gfx.DrawText(re, new R(rx, cy + 5, rw, 16), 12, Th.Text, 600, 1);
                        if (UI.Click(UI.Id("mreact_" + i, rIdx++), rr))
                        {
                            string emo = re.Split(' ')[0];
                            _ = dc.AddReactionAsync(m.ChannelId, m.Id, emo);
                        }
                        rx += rw + 6;
                    }
                }
                if (rowHover)
                {
                    float ax = rowRect.Right - 14;
                    if (m.AuthorId == dc.Me.Id)
                    {
                        ax -= 50;
                        if (UI.Button(UI.Id("mdel", i), new R(ax, y + 4, 46, 22), "Удал.", Th.Card, Th.Red, 5, 11))
                            _ = dc.DeleteMessageAsync(m.ChannelId, m.Id);
                        ax -= 48;
                        if (UI.Button(UI.Id("medit", i), new R(ax, y + 4, 44, 22), "Изм.", Th.Card, Th.Amber, 5, 11))
                        {
                            _editingMsgId = m.Id;
                            _editingOrigText = m.Content.Replace(" (ред.)", "");
                            App.SetChatInputText(_editingOrigText);
                        }
                    }
                    int urlIdx = m.Content.IndexOf("http", StringComparison.OrdinalIgnoreCase);
                    if (urlIdx >= 0)
                    {
                        ax -= 58;
                        if (UI.Button(UI.Id("murl", i), new R(ax, y + 4, 54, 22), "Ссылка", Th.Card, Th.Cyan, 5, 11))
                        {
                            int end = m.Content.IndexOfAny([' ', '\n', '\r', ')', ']'], urlIdx);
                            W.OpenUrl(end > urlIdx ? m.Content[urlIdx..end] : m.Content[urlIdx..]);
                        }
                    }
                    ax -= 52;
                    if (UI.Button(UI.Id("mcpy", i), new R(ax, y + 4, 48, 22), "Копир.", Th.Card, Th.Sub, 5, 11))
                    { W.CopyText(App.Hwnd, m.Content); Toast("Скопировано в буфер"); }
                    ax -= 60;
                    if (UI.Button(UI.Id("mrep", i), new R(ax, y + 4, 56, 22), "Ответить", Th.Card, Th.Text, 5, 11))
                    { _replyToId = m.Id; _replyToLabel = $"Ответ на @{m.AuthorName}"; }

                    // 1-Click Quick Reactions (👍 ❤️ 😂 🔥)
                    string[] qEmo = ["🔥", "😂", "❤️", "👍"];
                    for (int e = 0; e < qEmo.Length; e++)
                    {
                        ax -= 26;
                        if (UI.Button(UI.Id("mqe_" + i, e), new R(ax, y + 4, 24, 22), qEmo[e], Th.Card, Th.Text, 5, 11.5f))
                            _ = dc.AddReactionAsync(m.ChannelId, m.Id, qEmo[e]);
                    }
                }
            }

            float maxChatScroll = MathF.Max(0, totalMsgH - msgArea.H + 32);
            if (_chatScroll > maxChatScroll) _chatScroll = maxChatScroll;
            if (!_showChannelSearch && maxChatScroll > 100 && _chatScroll >= maxChatScroll - 120) _ = dc.LoadOlderMessagesAsync(ch.Id);
            if (maxChatScroll > 0)
            {
                float barH = MathF.Max(32, msgArea.H * (msgArea.H / MathF.Max(msgArea.H, totalMsgH)));
                float barY = msgArea.Bottom - barH - (_chatScroll / maxChatScroll) * (msgArea.H - barH);
                Gfx.FillRect(new R(msgArea.Right - 5, barY, 4, barH), 0x33FFFFFF, 2);
            }
        }
        Gfx.PopClip();

        // Floating "↓ К новым сообщениям" pill when scrolled up in chat history
        if (_chatScroll > 180f)
        {
            var jmp = new R(msgArea.X + (msgArea.W - 190) * 0.5f, msgArea.Bottom - 36, 190, 28);
            if (UI.Button(UI.Id("chat_bot"), jmp, "↓ К новым сообщениям", 0xEE5865F2, 0xFFFFFFFF, 14, 12, 700))
                _chatScroll = 0;
        }

        // Right-Side Member List Sidebar (224px)
        if (showMembers && guild != null)
        {
            DrawMemberListSidebar(new R(r.X + chatW, r.Y + 45, memW, r.H - 45), guild);
        }

        if (!string.IsNullOrEmpty(_hoverGuildTip))
        {
            float tw = Gfx.MeasureText(_hoverGuildTip, 13, 600, 260).W + 22;
            var tr = new R(RailW + 6, Math.Clamp(_hoverGuildTipY, TopH + 6, Gfx.H - 40), tw, 28);
            Gfx.FillRect(tr, 0xF0111318, 6);
            Gfx.StrokeRect(tr, Th.Border, 1, 6);
            Gfx.DrawText(_hoverGuildTip, new R(tr.X + 10, tr.Y + 6, tr.W - 20, 18), 13, Th.Text, 600);
        }

        if (!string.IsNullOrEmpty(_editingMsgId))
        {
            var eb = new R(r.X + 16, r.Bottom - InputH - 28, chatW - 32, 22);
            Gfx.FillRect(eb, 0x33F59E0B, 6);
            Gfx.DrawText("✏️ Редактирование сообщения (Enter — сохранить, Esc — отмена)", new R(eb.X + 10, eb.Y + 3, eb.W - 40, 16), 11.5f, Th.Amber, 600);
            if (UI.Button(UI.Id("ed_x"), new R(eb.Right - 22, eb.Y + 2, 18, 18), "✕", 0x00000000, Th.Sub, 4, 11))
            { _editingMsgId = ""; _editingOrigText = ""; App.TakeChatInputText(); }
        }
        else if (!string.IsNullOrEmpty(_replyToId))
        {
            var rb = new R(r.X + 16, r.Bottom - InputH - 28, chatW - 32, 22);
            Gfx.FillRect(rb, Th.Card, 6);
            Gfx.DrawText(_replyToLabel, new R(rb.X + 10, rb.Y + 3, rb.W - 40, 16), 11.5f, Th.Cyan, 600);
            if (UI.Button(UI.Id("rep_x"), new R(rb.Right - 22, rb.Y + 2, 18, 18), "✕", 0x00000000, Th.Sub, 4, 11))
            { _replyToId = ""; _replyToLabel = ""; }
        }
        else if (ch != null && dc.TypingUsers.TryGetValue(ch.Id, out var typ) && Environment.TickCount64 < typ.Until)
        {
            Gfx.DrawText($"{typ.Name} печатает...", new R(r.X + 20, r.Bottom - InputH - 22, 300, 18), 11.5f, Th.Sub, 600);
        }

        _chatInputRect = new R(r.X + 54, r.Bottom - InputH - 4, MathF.Max(120, chatW - 186), 40);
        _wantChatInput = !_showSettings && !_showQuickSwitch && !_showShareModal && _lightboxUrl == null;
        Gfx.FillRect(new R(r.X + 12, _chatInputRect.Y - 2, chatW - 28, 44), Th.Panel, 10);
        Gfx.StrokeRect(new R(r.X + 12, _chatInputRect.Y - 2, chatW - 28, 44), Th.Border, 1, 10);

        if (UI.Button(UI.Id("btn_up"), new R(r.X + 18, _chatInputRect.Y + 4, 32, 32), "+", Th.Card, Th.Text, 16, 16, 700) && ch != null)
        {
            var fp = W.PickFile(App.Hwnd);
            if (!string.IsNullOrEmpty(fp)) { Toast("Загрузка " + Path.GetFileName(fp) + "..."); _ = dc.UploadFileAsync(ch.Id, fp); }
        }
        if (UI.Button(UI.Id("btn_emo"), new R(r.X + chatW - 126, _chatInputRect.Y + 4, 32, 32), "😀", _showEmojiPicker ? Th.Brand : Th.Card, Th.Text, 7, 14))
            _showEmojiPicker = !_showEmojiPicker;
        if (UI.Button(UI.Id("btn_send"), new R(r.X + chatW - 88, _chatInputRect.Y + 3, 66, 34), string.IsNullOrEmpty(_editingMsgId) ? "Отпр." : "Сохр.", Th.Brand, Th.Text, 7, 13))
            OnChatEnter(App.TakeChatInputText());
    }

    private static void DrawMemberListSidebar(R r, DiscordGuild guild)
    {
        Gfx.FillRect(r, Th.Sidebar);
        Gfx.FillRect(new R(r.X, r.Y, 1, r.H), Th.Border);
        var dc = DiscordClient.Instance;

        List<VoiceMember> voiceList;
        lock (guild.VoiceStates) voiceList = guild.VoiceStates.Values.OrderBy(v => v.Username).ToList();
        var inVoiceIds = new HashSet<string>(voiceList.Select(v => v.UserId));

        var otherUsers = new List<(string Id, string Name, string? AvUrl, bool Online)>();
        if (dc.GuildMemberIds.TryGetValue(guild.Id, out var idSet))
        {
            List<string> ids;
            lock (idSet) ids = [.. idSet];
            foreach (var uid in ids)
            {
                if (inVoiceIds.Contains(uid)) continue;
                string name = dc.GuildNicks.TryGetValue(guild.Id + ":" + uid, out var nk) ? nk : (dc.Users.TryGetValue(uid, out var uo) ? uo.DisplayName : "");
                if (string.IsNullOrEmpty(name)) continue;
                string? av = dc.Users.TryGetValue(uid, out var u2) && !string.IsNullOrEmpty(u2.Avatar)
                    ? $"https://cdn.discordapp.com/avatars/{uid}/{u2.Avatar}.png?size=48" : null;
                otherUsers.Add((uid, name, av, uid == dc.Me.Id));
            }
        }
        if (!inVoiceIds.Contains(dc.Me.Id) && !otherUsers.Any(x => x.Id == dc.Me.Id))
        {
            string? myAv = string.IsNullOrEmpty(dc.Me.Avatar) ? null : $"https://cdn.discordapp.com/avatars/{dc.Me.Id}/{dc.Me.Avatar}.png?size=48";
            otherUsers.Insert(0, (dc.Me.Id, dc.Me.DisplayName, myAv, true));
        }
        otherUsers = otherUsers.OrderByDescending(x => x.Online).ThenBy(x => x.Name).Take(60).ToList();

        float totalH = 20 + (voiceList.Count > 0 ? 26 + voiceList.Count * 38 : 0) + 26 + otherUsers.Count * 38;
        float maxScroll = MathF.Max(0, totalH - r.H);
        if (UI.Hover(r) && UI.Wheel != 0) _memberScroll = Math.Clamp(_memberScroll - UI.Wheel * 44f, 0f, maxScroll);
        else _memberScroll = Math.Clamp(_memberScroll, 0f, maxScroll);

        Gfx.PushClip(r);
        float y = r.Y + 10 - _memberScroll;
        if (voiceList.Count > 0)
        {
            Gfx.DrawText($"В ГОЛОСОВОМ — {voiceList.Count}", new R(r.X + 14, y, r.W - 24, 18), 10.5f, Th.Dim, 700);
            y += 22;
            foreach (var vm in voiceList)
            {
                if (y + 36 >= r.Y && y <= r.Bottom)
                {
                    var row = new R(r.X + 8, y, r.W - 16, 34);
                    if (UI.Hover(row)) Gfx.FillRect(row, Th.Hover, 6);
                    var av = ImageCache.Get(vm.AvatarUrl, 48, ImgMask.Circle);
                    var avr = new R(row.X + 6, row.Y + 4, 26, 26);
                    bool spk = dc.IsUserActuallySpeaking(vm);
                    if (spk) { Gfx.FillCircle(avr.X + 13, avr.Y + 13, 15, Th.Green); Gfx.FillCircle(avr.X + 13, avr.Y + 13, 13.3f, Th.Sidebar); }
                    if (av != null) Gfx.DrawBitmap(av, avr);
                    else Gfx.FillCircle(avr.X + 13, avr.Y + 13, 13, Th.Brand);
                    Gfx.FillCircle(avr.Right - 3, avr.Bottom - 3, 4.2f, Th.Sidebar);
                    Gfx.FillCircle(avr.Right - 3, avr.Bottom - 3, 3f, Th.Green);
                    Gfx.DrawText(vm.Username, new R(row.X + 38, row.Y + 8, row.W - (vm.SelfStream ? 86 : 44), 18), 12.5f, Th.Text, 600);
                    if (vm.SelfStream)
                    {
                        Gfx.FillRect(new R(row.Right - 44, row.Y + 9, 40, 16), Th.Red, 8);
                        Gfx.DrawText("LIVE", new R(row.Right - 44, row.Y + 10, 40, 14), 9f, 0xFFFFFFFF, 700, 1);
                    }
                    if (UI.RightClick(row))
                    {
                        _ctxStreamUserId = null; _audioPopupMode = 0;
                        _ctxUser = vm; _ctxGuildId = guild.Id;
                        _ctxX = Math.Clamp(UI.MX, 8, Gfx.W - 252);
                        _ctxY = Math.Clamp(UI.MY, TopH + 8, Gfx.H - 220);
                    }
                }
                y += 38;
            }
            y += 8;
        }

        Gfx.DrawText($"УЧАСТНИКИ — {otherUsers.Count}", new R(r.X + 14, y, r.W - 24, 18), 10.5f, Th.Dim, 700);
        y += 22;
        foreach (var u in otherUsers)
        {
            if (y + 36 >= r.Y && y <= r.Bottom)
            {
                var row = new R(r.X + 8, y, r.W - 16, 34);
                if (UI.Hover(row)) Gfx.FillRect(row, Th.Hover, 6);
                var av = ImageCache.Get(u.AvUrl, 48, ImgMask.Circle);
                var avr = new R(row.X + 6, row.Y + 4, 26, 26);
                if (av != null) Gfx.DrawBitmap(av, avr);
                else { Gfx.FillCircle(avr.X + 13, avr.Y + 13, 13, Th.Card); Gfx.DrawText(u.Name.Length > 0 ? u.Name[..1].ToUpperInvariant() : "?", new R(avr.X, avr.Y + 5, 26, 18), 11, Th.Text, 700, 1); }
                if (u.Online)
                {
                    Gfx.FillCircle(avr.Right - 3, avr.Bottom - 3, 4.2f, Th.Sidebar);
                    Gfx.FillCircle(avr.Right - 3, avr.Bottom - 3, 3f, Th.Green);
                }
                Gfx.DrawText(u.Name, new R(row.X + 38, row.Y + 8, row.W - 44, 18), 12.5f, u.Online ? Th.Text : Th.Sub, u.Online ? 600 : 400);
                if (UI.Click(UI.Id("mem_" + u.Id), row))
                    OpenUserProfile(u.Id, guild.Id, UI.MX, UI.MY);
            }
            y += 38;
        }
        Gfx.PopClip();
    }

    private static void OpenUserProfile(string userId, string guildId, float mx, float my)
    {
        _ctxUser = null; _ctxStreamUserId = null; _audioPopupMode = 0; _showStatusMenu = false; _showVoiceInfo = false;
        _profileUserId = userId;
        _profileGuildId = guildId;
        _profileX = Math.Clamp(mx + 12, RailW + 8, Gfx.W - 296);
        _profileY = Math.Clamp(my - 40, TopH + 8, Gfx.H - 290);
    }

    // ---------------- Friends List Home View (@me -> Друзья) ----------------

    private static void DrawFriendsStage(R r)
    {
        Gfx.FillRect(r, Th.Bg);
        var dc = DiscordClient.Instance;

        Gfx.FillRect(new R(r.X, r.Y, r.W, 44), Th.Panel);
        Gfx.FillRect(new R(r.X, r.Y + 44, r.W, 1), Th.Border);
        Gfx.DrawText("👥 Друзья", new R(r.X + 16, r.Y + 12, 110, 22), 15, Th.Text, 700);
        if (UI.Button(UI.Id("fr_t0"), new R(r.X + 128, r.Y + 8, 86, 28), "В сети", _friendsTab == 0 ? Th.Active : Th.Card, Th.Text, 6, 12)) _friendsTab = 0;
        if (UI.Button(UI.Id("fr_t1"), new R(r.X + 220, r.Y + 8, 72, 28), "Все", _friendsTab == 1 ? Th.Active : Th.Card, Th.Text, 6, 12)) _friendsTab = 1;

        List<DiscordFriend> list;
        lock (dc.Friends) list = [.. dc.Friends];
        if (_friendsTab == 0) list = list.Where(f => f.Status != "offline").ToList();

        var area = new R(r.X, r.Y + 45, r.W, r.H - 45);
        Gfx.PushClip(area);
        float y = area.Y + 16;
        Gfx.DrawText((_friendsTab == 0 ? "В СЕТИ — " : "ВСЕ ДРУЗЬЯ — ") + list.Count, new R(area.X + 20, y, 260, 18), 11, Th.Dim, 700);
        y += 28;

        if (list.Count == 0)
        {
            Gfx.DrawText("Нет друзей в этой категории", new R(area.X + 20, y + 20, area.W - 40, 24), 14, Th.Dim);
        }
        else
        {
            for (int i = 0; i < list.Count && y < area.Bottom; i++)
            {
                var f = list[i];
                var row = new R(area.X + 16, y, MathF.Min(720, area.W - 32), 54);
                Gfx.FillRect(new R(row.X, row.Y, row.W, 1), Th.Border);
                if (UI.Hover(row)) Gfx.FillRect(row, Th.Hover, 8);

                var av = ImageCache.Get(f.AvatarUrl, 64, ImgMask.Circle);
                var avr = new R(row.X + 10, row.Y + 10, 34, 34);
                if (av != null) Gfx.DrawBitmap(av, avr);
                else { Gfx.FillCircle(avr.X + 17, avr.Y + 17, 17, Th.Brand); Gfx.DrawText(f.DisplayName.Length > 0 ? f.DisplayName[..1].ToUpperInvariant() : "?", new R(avr.X, avr.Y + 8, 34, 20), 14, 0xFFFFFFFF, 700, 1); }

                uint dot = f.Status switch { "online" => Th.Green, "idle" => Th.Amber, "dnd" => Th.Red, _ => Th.Dim };
                Gfx.FillCircle(avr.Right - 4, avr.Bottom - 4, 5.2f, Th.Bg);
                Gfx.FillCircle(avr.Right - 4, avr.Bottom - 4, 3.8f, dot);

                Gfx.DrawText(f.DisplayName, new R(row.X + 54, row.Y + 9, row.W - 190, 18), 14, Th.Text, 700);
                string stTxt = f.Status switch { "online" => "В сети", "idle" => "Неактивен", "dnd" => "Не беспокоить", _ => "Не в сети" };
                Gfx.DrawText($"@{f.Username}  •  {stTxt}", new R(row.X + 54, row.Y + 28, row.W - 190, 16), 11.5f, Th.Sub);

                if (UI.Button(UI.Id("fr_dm", i), new R(row.Right - 118, row.Y + 12, 108, 30), "💬 Написать", Th.Card, Th.Text, 6, 12))
                {
                    _showFriendsView = false;
                    _ = ShellAsync.OpenDmAsync(f.UserId);
                }
                y += 56;
            }
        }
        Gfx.PopClip();
    }

    private static float StepSpeakAnim(string uid, bool speaking)
    {
        _peerSpeakAnim.TryGetValue(uid, out float cur);
        float target = speaking ? 1f : 0f;
        cur += (target - cur) * (speaking ? 0.42f : 0.16f);
        if (cur < 0.005f) { _peerSpeakAnim.Remove(uid); return 0f; }
        _peerSpeakAnim[uid] = cur;
        UI.Animate();
        return cur;
    }

    private static void DrawVoiceBars(float x, float cy, int count, float maxH, float level, float seed, uint color, float alpha = 1f)
    {
        if (alpha <= 0.02f) return;
        float t = Environment.TickCount64 * 0.012f;
        uint col = Gfx.Fade(color, alpha);
        for (int i = 0; i < count; i++)
        {
            float env = count <= 1 ? 1f : 1f - MathF.Abs(i - (count - 1) * 0.5f) / MathF.Max(1f, count * 0.65f);
            float wave = 0.52f + 0.48f * MathF.Sin(t + seed + i * 1.45f);
            float bh = MathF.Max(3f, maxH * Math.Clamp(level * env * wave, 0.22f, 1f));
            Gfx.FillRect(new R(x + i * 4f, cy - bh * 0.5f, 2.5f, bh), col, 1.25f);
        }
    }
}
