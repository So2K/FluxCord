using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FluxCord;

public sealed class WatchSession
{
    public int Slot;
    public string StreamKey = "";
    public string StreamerUserId = "";
    public string StreamerName = "";
    public float Volume = 1.0f;
    public bool Muted;
    public string Status = "";
    public FcScreenStats Stats;

    internal ClientWebSocket? Gw;
    internal CancellationTokenSource? Cts;
    internal string RtcServerId = "";
    internal string RtcChannelId = "";
    internal string Token = "";
    internal string Endpoint = "";
    internal bool GwStarted;
    internal int Seq = -1;
    internal ushort DaveVer;
    internal uint LocalSsrc;
    internal uint VideoSsrc;
    internal readonly Dictionary<int, ushort> DaveTransitions = new();
    internal long ConnectedTick;
    internal long LastKickTick;
    internal long LastDecodedFrames;
    internal long LastDecodedTick;
    internal long LastDiagLogTick;
    internal int LastRxPkts;
    internal int LastKfDrops;
}

/// <summary>
/// Direct Discord REST v10 + Gateway v10 + Voice Gateway v8 + Multi-Stream Go Live (up to 4 simultaneous watchers + outbound publisher).
/// Zero Electron, zero reflection JSON, NativeAOT clean.
/// </summary>
public sealed class DiscordClient
{
    public static readonly DiscordClient Instance = new();

    private readonly HttpClient _http;
    private ClientWebSocket? _gw;
    private ClientWebSocket? _voiceGw;
    private CancellationTokenSource? _gwCts;
    private CancellationTokenSource? _voiceCts;

    private string _token = "";
    private int? _lastSeq;
    private string _sessionId = "";
    private string _resumeGatewayUrl = "";
    private volatile bool _hbAcked = true;

    // Voice handshake state
    private string _voiceGuildId = "";
    private string _voiceChannelId = "";
    private string _voiceSessionId = "";
    private string _voiceToken = "";
    private string _voiceEndpoint = "";
    private uint _voiceSsrc;
    private bool _voiceGwStarted;
    private int _voiceSeq = -1;
    private ushort _davePendingVersion;
    private readonly Dictionary<int, ushort> _voiceDaveTransitions = new();
    private bool _lastSentVoiceSpeaking;
    private long _lastVoiceHbSent;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientWebSocket, SemaphoreSlim> _wsSendLocks = new();

    // Live Voice Ping History (40 samples for exact Discord Connection Graph!) + RTC Region Turbo Engine
    public int VoicePingMs { get; private set; }
    public int VoicePingAvgMs { get; private set; }
    public string VoiceEndpointHost { get; private set; } = "";
    public string PubEndpointHost { get; private set; } = "";
    public List<int> VoicePingHistory { get; } = [];
    public List<RtcRegionInfo> Regions { get; } = [];
    public string BestRtcRegion { get; private set; } = "";
    public bool IsBenchmarkingRegions { get; private set; }
    public int PubVideoTxPkts { get; private set; }
    public int PubAudioTxPkts { get; private set; }
    public bool PubDaveActive { get; private set; }

    private void RecordVoicePing(int ms)
    {
        ms = Math.Clamp(ms, 1, 999);
        VoicePingMs = ms;
        lock (VoicePingHistory)
        {
            VoicePingHistory.Add(ms);
            if (VoicePingHistory.Count > 40) VoicePingHistory.RemoveAt(0);
            int sum = 0;
            for (int i = 0; i < VoicePingHistory.Count; i++) sum += VoicePingHistory[i];
            VoicePingAvgMs = sum / VoicePingHistory.Count;
        }
        // Update matching RTC region with real live measured Discord ping!
        string? mapped = MapHostToRegion(VoiceEndpointHost);
        if (!string.IsNullOrEmpty(mapped))
        {
            lock (Regions)
            {
                var r = Regions.FirstOrDefault(x => string.Equals(x.Id, mapped, StringComparison.OrdinalIgnoreCase));
                if (r != null) { r.PingMs = ms; r.IsOfficialDiscordPing = true; }
            }
        }
    }

    private static string? MapHostToRegion(string host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        string h = host.ToLowerInvariant();
        if (h.Contains("hel")) return "helsinki";
        if (h.Contains("ams") || h.Contains("rtm")) return "rotterdam";
        if (h.Contains("fra")) return "frankfurt";
        if (h.Contains("waw")) return "warsaw";
        if (h.Contains("arn") || h.Contains("sto")) return "stockholm";
        if (h.Contains("mil")) return "milan";
        if (h.Contains("mad")) return "madrid";
        if (h.Contains("bom")) return "india";
        if (h.Contains("dxb")) return "dubai";
        if (h.Contains("sin")) return "singapore";
        if (h.Contains("mow") || h.Contains("led") || h.Contains("rus")) return "russia";
        if (h.Contains("iad") || h.Contains("atl")) return "us-east";
        return null;
    }

    // 4-Slot Multi-Stream Watching Pool (simultaneous live playback in Grid View + instant Focus/Unfocus!)
    private readonly object _watchLock = new();
    private readonly Dictionary<string, WatchSession> _watches = new();
    public string? FocusedStreamUserId { get; set; } // null = Grid View, "self" = our stream focused, or streamer UserId

    // Outbound Go Live Publisher Stream Voice Gateway v8
    private ClientWebSocket? _pubGw;
    private CancellationTokenSource? _pubCts;
    private string _pubStreamKey = "";
    private string _pubRtcServerId = "";
    private string _pubRtcChannelId = "";
    private string _pubToken = "";
    private string _pubEndpoint = "";
    private bool _pubGwStarted;
    private int _pubSeq = -1;
    private ushort _pubDaveVer;
    private uint _pubMySsrc;
    private uint _pubVideoSsrc;
    private readonly Dictionary<int, ushort> _pubDaveTransitions = new();
    private int _pubWidth = 1920, _pubHeight = 1080, _pubFps = 60;
    private bool _pubIsDualScreen;
    public bool IsPublishingStream { get; private set; }
    public string PublishStatus { get; private set; } = "";

    public bool IsWatchingAnyStream { get { lock (_watchLock) return _watches.Count > 0; } }
    public bool IsWatchingStream(string userId) { lock (_watchLock) return _watches.ContainsKey(userId); }

    public WatchSession? GetWatchSession(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        lock (_watchLock) return _watches.TryGetValue(userId, out var s) ? s : null;
    }

    public List<WatchSession> GetWatchSessionsSnapshot()
    {
        lock (_watchLock) return [.. _watches.Values];
    }

    private static int ParseStatToken(string text, string key)
    {
        int idx = text.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return -1;
        idx += key.Length;
        int val = 0;
        bool any = false;
        while (idx < text.Length && text[idx] >= '0' && text[idx] <= '9')
        {
            val = val * 10 + (text[idx] - '0');
            idx++;
            any = true;
        }
        return any ? val : -1;
    }

    public unsafe void PollWatchStats()
    {
        List<WatchSession> snap;
        lock (_watchLock) snap = [.. _watches.Values];
        long now = Environment.TickCount64;
        foreach (var ws in snap)
        {
            FcScreenStats st;
            MediaNative.fc_watch_get_stats_slot(ws.Slot, &st);
            ws.Stats = st;
            string encName = st.GetEncoderName();
            int rxPkts = ParseStatToken(encName, "rx=");
            int kfDrops = ParseStatToken(encName, "kf=");

            if (st.FramesEncoded != ws.LastDecodedFrames)
            {
                ws.LastDecodedFrames = st.FramesEncoded;
                ws.LastDecodedTick = now;
                if (rxPkts >= 0) ws.LastRxPkts = rxPkts;
                if (kfDrops >= 0) ws.LastKfDrops = kfDrops;
            }
            if (now - ws.LastDiagLogTick >= 15000)
            {
                ws.LastDiagLogTick = now;
                SettingsStore.Log($"WatchSlot[{ws.Slot}:{ws.StreamerName}] {st.Width}x{st.Height} @{st.Fps:F1}fps dec={st.DecodeMs:F1}ms kbps={st.BitrateKbps:F0} {encName}");
            }
            // SFU Keyframe Bounce: trigger on initial 0-frame stall (>1.8s), OR mid-stream freeze (>2.5s) ONLY if packets are actively arriving / kf dropping (never spam on static desktop!)
            long refTick = ws.LastDecodedTick > 0 ? ws.LastDecodedTick : ws.ConnectedTick;
            bool initialStall = st.FramesEncoded == 0 && refTick > 0 && (now - refTick >= 1800);
            bool activeFreeze = st.FramesEncoded > 0 && refTick > 0 && (now - refTick >= 2500) &&
                                ((rxPkts >= 0 && rxPkts - ws.LastRxPkts > 25) || (kfDrops >= 0 && kfDrops > ws.LastKfDrops));
            if ((initialStall || activeFreeze) && now - ws.LastKickTick >= 3500 && ws.Gw?.State == WebSocketState.Open)
            {
                ws.LastKickTick = now;
                if (rxPkts >= 0) ws.LastRxPkts = rxPkts;
                if (kfDrops >= 0) ws.LastKfDrops = kfDrops;
                _ = KickStreamKeyframeAsync(ws);
            }
        }

        if (IsPublishingStream && now - _lastPubStatsTick >= 2000)
        {
            _lastPubStatsTick = now;
            int vp = 0, ap = 0, plis = 0, daveAct = 0;
            MediaNative.fc_pub_get_stats_ex(&vp, &ap, &plis, &daveAct);
            PubVideoTxPkts = vp; PubAudioTxPkts = ap; PubDaveActive = daveAct != 0;
            if (now - _lastPubDiagTick >= 15000)
            {
                _lastPubDiagTick = now;
                SettingsStore.Log($"PubTx v={vp} a={ap} pli_rx={plis} dave_enc={daveAct} host={PubEndpointHost}");
            }
        }
    }
    private long _lastPubStatsTick;
    private long _lastPubDiagTick;

    private static async Task KickStreamKeyframeAsync(WatchSession session)
    {
        try
        {
            if (session.Gw?.State != WebSocketState.Open) return;
            SettingsStore.Log($"WatchGW[{session.Slot}] SFU keyframe bounce (OP15 0->100) vssrc={session.VideoSsrc}");
            string offMsg = session.VideoSsrc != 0 ? $"{{\"op\":15,\"d\":{{\"{session.VideoSsrc}\":0,\"any\":0}}}}" : "{\"op\":15,\"d\":{\"any\":0}}";
            string onMsg = session.VideoSsrc != 0 ? $"{{\"op\":15,\"d\":{{\"{session.VideoSsrc}\":100,\"any\":100}}}}" : "{\"op\":15,\"d\":{\"any\":100}}";
            await SendWsTextAsync(session.Gw, offMsg, CancellationToken.None);
            await Task.Delay(120);
            if (session.Gw?.State == WebSocketState.Open)
                await SendWsTextAsync(session.Gw, onMsg, CancellationToken.None);
        }
        catch { }
    }

    public void SetStreamVolume(string userId, float vol)
    {
        vol = Math.Clamp(vol, 0f, 2f);
        WatchSession? ws;
        lock (_watchLock) _watches.TryGetValue(userId, out ws);
        if (ws == null) return;
        ws.Volume = vol;
        ws.Muted = vol < 0.005f;
        MediaNative.fc_watch_set_volume_slot(ws.Slot, ws.Muted ? 0f : ws.Volume);
        Notify();
    }

    public void ToggleStreamMute(string userId)
    {
        WatchSession? ws;
        lock (_watchLock) _watches.TryGetValue(userId, out ws);
        if (ws == null) return;
        ws.Muted = !ws.Muted;
        MediaNative.fc_watch_set_volume_slot(ws.Slot, ws.Muted ? 0f : ws.Volume);
        Notify();
    }

    public bool LocalSpeaking { get; private set; }
    public float LocalVoiceLevel { get; private set; }

    public void UpdateLocalSpeaking(bool speaking, float level)
    {
        LocalSpeaking = speaking;
        LocalVoiceLevel = level;
        if (!VoiceConnected || IsDemoMode) return;
        var ws = _voiceGw;
        uint ssrc = _voiceSsrc;
        if (ws == null || ws.State != WebSocketState.Open || ssrc == 0) return;
        if (speaking == _lastSentVoiceSpeaking) return;
        _lastSentVoiceSpeaking = speaking;
        int flag = speaking ? 1 : 0;
        _ = Task.Run(async () =>
        {
            try { await SendWsTextAsync(ws, $"{{\"op\":5,\"d\":{{\"speaking\":{flag},\"delay\":0,\"ssrc\":{ssrc}}}}}", CancellationToken.None); }
            catch { }
        });
    }

    public bool IsUserActuallySpeaking(VoiceMember vm)
    {
        if (vm.UserId == Me.Id) return VoiceConnected && LocalSpeaking;
        if (_userToSsrc.TryGetValue(vm.UserId, out var ssrc) && MediaNative.fc_voice_is_ssrc_speaking(ssrc) != 0)
            return true;
        return vm.Speaking && Environment.TickCount64 < vm.SpeakingUntil;
    }

    public bool IsConnected { get; private set; }
    public bool IsConnecting { get; private set; }
    public bool IsDemoMode { get; private set; }
    public string StatusText { get; private set; } = "Отключён";
    public int GatewayPingMs { get; private set; }
    public DiscordUser Me { get; private set; } = new() { Id = "0", Username = "Guest", DisplayName = "Guest" };

    public List<DiscordGuild> Guilds { get; } = [];
    public List<DiscordChannel> DmChannels { get; } = [];
    public List<DiscordFriend> Friends { get; } = [];
    public ConcurrentDictionary<string, DiscordUser> Users { get; } = new();
    public ConcurrentDictionary<string, string> GuildNicks { get; } = new(); // "guildId:userId" -> nick
    public ConcurrentDictionary<string, HashSet<string>> GuildMemberIds { get; } = new(); // guildId -> userIds
    public ConcurrentDictionary<string, List<DiscordMessage>> Messages { get; } = new();
    public ConcurrentDictionary<string, List<DiscordMessage>> PinnedMessages { get; } = new();
    public ConcurrentDictionary<string, (string Name, long Until)> TypingUsers { get; } = new();
    public ConcurrentDictionary<string, byte> UnreadChannels { get; } = new();
    private readonly ConcurrentDictionary<string, uint> _userToSsrc = new();
    private readonly ConcurrentDictionary<string, float> _userVolumes = new();
    private readonly ConcurrentDictionary<string, byte> _loadingOlder = new();
    private readonly ConcurrentDictionary<string, byte> _reachedChannelStart = new();

    public string ActiveGuildId { get; set; } = "@me";
    public string ActiveChannelId { get; set; } = "";
    public string ConnectedVoiceGuildId => _voiceGuildId;
    public string ConnectedVoiceChannelId => _voiceChannelId;
    public bool VoiceConnected { get; private set; }
    public string VoiceStatus { get; private set; } = "";

    public event Action? StateChanged;
    public event Action? ReadyCompleted;

    private DiscordClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            EnableMultipleHttp2Connections = true,
            ConnectCallback = async (ctx, ct) =>
            {
                var sock = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                try
                {
                    await sock.ConnectAsync(ctx.DnsEndPoint, ct);
                    return new System.Net.Sockets.NetworkStream(sock, ownsSocket: true);
                }
                catch
                {
                    sock.Dispose();
                    throw;
                }
            }
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
    }

    private void Notify() => StateChanged?.Invoke();

    public void EnterDemoMode()
    {
        Disconnect();
        IsDemoMode = true;
        IsConnecting = false;
        IsConnected = true;
        StatusText = "Demo / Local Benchmark";
        GatewayPingMs = 8;
        Me = new DiscordUser { Id = "1001", Username = "flux_user", DisplayName = "Flux User (RTX 4070 Ti)", Status = "online" };

        Guilds.Clear();
        DmChannels.Clear();
        Messages.Clear();

        var g1 = new DiscordGuild { Id = "g1", Name = "FluxCord HQ" };
        g1.Channels.Add(new DiscordChannel { Id = "c_cat1", GuildId = "g1", Name = "ТЕКСТОВЫЕ КАНАЛЫ", Type = 4, Position = 0 });
        g1.Channels.Add(new DiscordChannel { Id = "c_gen", GuildId = "g1", Name = "общий", Type = 0, Position = 1, ParentId = "c_cat1", Topic = "Ультрабыстрый нативный клиент FluxCord — .NET 10 NativeAOT + Direct2D + Zero-Copy NVENC 4K60" });
        g1.Channels.Add(new DiscordChannel { Id = "c_dev", GuildId = "g1", Name = "gamedev-granny2", Type = 0, Position = 2, ParentId = "c_cat1", Topic = "Обсуждение ассетов, UE 5.7 и тестов" });
        g1.Channels.Add(new DiscordChannel { Id = "c_cat2", GuildId = "g1", Name = "ГОЛОСОВЫЕ И 4K СТРИМЫ", Type = 4, Position = 3 });
        g1.Channels.Add(new DiscordChannel { Id = "v_main", GuildId = "g1", Name = "GTA 6", Type = 2, Position = 4, ParentId = "c_cat2" });
        g1.Channels.Add(new DiscordChannel { Id = "v_chill", GuildId = "g1", Name = "Тест шумодава (Pro Audio)", Type = 2, Position = 5, ParentId = "c_cat2" });
        g1.VoiceStates["2001"] = new VoiceMember { UserId = "2001", ChannelId = "v_main", Username = "Draka (slowed+reverb)", SelfStream = true };
        g1.VoiceStates["2002"] = new VoiceMember { UserId = "2002", ChannelId = "v_main", Username = "lapki >.<", SelfStream = true };
        g1.VoiceStates["2003"] = new VoiceMember { UserId = "2003", ChannelId = "v_main", Username = "MR MEAT", SelfStream = false };
        Guilds.Add(g1);

        var g2 = new DiscordGuild { Id = "g2", Name = "Granny2 Team" };
        g2.Channels.Add(new DiscordChannel { Id = "c_g2", GuildId = "g2", Name = "релиз-билд", Type = 0, Position = 0, Topic = "Сборка и тесты" });
        g2.Channels.Add(new DiscordChannel { Id = "v_g2", GuildId = "g2", Name = "Созвон команды", Type = 2, Position = 1 });
        Guilds.Add(g2);

        DmChannels.Add(new DiscordChannel { Id = "dm_1", GuildId = "@me", Name = "Сохранённые заметки", Type = 1 });
        Friends.Clear();
        Friends.Add(new DiscordFriend { UserId = "2001", Username = "draka", DisplayName = "Draka (slowed+reverb)", Status = "online", Type = 1 });
        Friends.Add(new DiscordFriend { UserId = "2002", Username = "lapki", DisplayName = "lapki >.<", Status = "online", Type = 1 });
        Friends.Add(new DiscordFriend { UserId = "2003", Username = "mrmeat", DisplayName = "MR MEAT", Status = "idle", Type = 1 });

        Messages["c_gen"] =
        [
            new DiscordMessage
            {
                Id = "m1", ChannelId = "c_gen", AuthorId = "sys", AuthorName = "FluxCord Engine", AuthorColor = 0xFF7BD88F,
                Timestamp = DateTime.Now.ToString("HH:mm"),
                Content = "Добро пожаловать в FluxCord! Оболочка: .NET 10 NativeAOT + Direct2D FlipDiscard (0% CPU в простое). " + SettingsStore.MigrationSummary
            },
            new DiscordMessage
            {
                Id = "m2", ChannelId = "c_gen", AuthorId = "sys", AuthorName = "Zero-Copy 4K Pipeline", AuthorColor = 0xFF60A5FA,
                Timestamp = DateTime.Now.ToString("HH:mm"),
                Content = "Нажми на голосовой канал «GTA 6» слева — откроется полноценная сетка как в GoofCord/Discord с мульти-стримами, быстрым выбором микрофона/наушников (⌄) и превью экранов/окон."
            }
        ];

        ActiveGuildId = "g1";
        ActiveChannelId = "c_gen";
        _ = BenchmarkRegionsAsync();
        Notify();
        ReadyCompleted?.Invoke();
    }

    private async Task<HttpResponseMessage?> SendRestAsync(Func<HttpRequestMessage> buildReq, string tag)
    {
        if (string.IsNullOrEmpty(_token)) return null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var req = buildReq();
            req.Headers.TryAddWithoutValidation("Authorization", _token);
            var resp = await _http.SendAsync(req);
            if ((int)resp.StatusCode == 429 && attempt == 0)
            {
                int delayMs = 1000;
                if (resp.Headers.TryGetValues("Retry-After", out var vals))
                {
                    string? raw = vals.FirstOrDefault();
                    if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var sec))
                        delayMs = Math.Clamp((int)Math.Ceiling(sec * 1000.0) + 50, 250, 5000);
                }
                SettingsStore.Log($"{tag} HTTP 429 -> retry in {delayMs}ms");
                resp.Dispose();
                await Task.Delay(delayMs);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
                SettingsStore.Log($"{tag} HTTP {(int)resp.StatusCode}");
            return resp;
        }
        return null;
    }

    public async Task ConnectAsync(string token)
    {
        Disconnect();
        _token = token.Trim().Trim('"');
        if (string.IsNullOrEmpty(_token))
        {
            StatusText = "Укажите токен Discord";
            Notify();
            return;
        }

        IsDemoMode = false;
        IsConnecting = true;
        _sessionId = "";
        _lastSeq = null;
        _resumeGatewayUrl = "";
        StatusText = "Подключение к Discord Gateway...";
        Notify();

        _gwCts = new CancellationTokenSource();
        var ct = _gwCts.Token;

        _ = Task.Run(async () =>
        {
            int backoffMs = 1000;
            var buf = new byte[65536];
            using var ms = new MemoryStream();

            while (!ct.IsCancellationRequested)
            {
                using var gw = new ClientWebSocket();
                using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _gw = gw;
                _hbAcked = true;
                bool fatalClose = false;

                try
                {
                    gw.Options.SetRequestHeader("Origin", "https://discord.com");
                    gw.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                    string targetUrl = !string.IsNullOrEmpty(_sessionId) && _lastSeq.HasValue && !string.IsNullOrEmpty(_resumeGatewayUrl)
                        ? (_resumeGatewayUrl.Contains('?') ? _resumeGatewayUrl : _resumeGatewayUrl.TrimEnd('/') + "/?v=10&encoding=json")
                        : "wss://gateway.discord.gg/?v=10&encoding=json";

                    await gw.ConnectAsync(new Uri(targetUrl), connCts.Token);

                    while (!connCts.IsCancellationRequested && gw.State == WebSocketState.Open)
                    {
                        ms.SetLength(0);
                        WebSocketReceiveResult res;
                        do
                        {
                            res = await gw.ReceiveAsync(buf, connCts.Token);
                            if (res.MessageType == WebSocketMessageType.Close)
                            {
                                int code = (int?)res.CloseStatus ?? 0;
                                SettingsStore.Log($"GW closed: {code} {res.CloseStatusDescription}");
                                IsConnected = false;
                                if (code is 4004 or 4010 or 4011 or 4012 or 4013 or 4014)
                                {
                                    IsConnecting = false;
                                    if (code == 4004)
                                    {
                                        SettingsStore.SetToken("");
                                        _token = "";
                                    }
                                    StatusText = code == 4004 ? "Неверный токен Discord (4004)" : $"Отключено ({code})";
                                    fatalClose = true;
                                }
                                else if (code is 4007 or 4009)
                                {
                                    _sessionId = "";
                                    _lastSeq = null;
                                    _resumeGatewayUrl = "";
                                    StatusText = "Переподключение к Discord...";
                                }
                                else
                                {
                                    StatusText = "Восстановление связи с Discord...";
                                }
                                Notify();
                                break;
                            }
                            ms.Write(buf, 0, res.Count);
                        } while (!res.EndOfMessage);

                        if (res.MessageType == WebSocketMessageType.Close) break;
                        HandleGatewayPayload(gw, ms.GetBuffer(), (int)ms.Length, connCts, ref backoffMs);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    // OP 7 RECONNECT / OP 9 INVALID_SESSION / Heartbeat timeout -> reconnect cleanly
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        IsConnected = false;
                        StatusText = "Переподключение: " + ex.Message;
                        SettingsStore.Log("GW error: " + ex.Message);
                        Notify();
                    }
                }
                finally
                {
                    try { connCts.Cancel(); } catch { }
                    if (ReferenceEquals(_gw, gw)) _gw = null;
                }

                if (fatalClose || ct.IsCancellationRequested) break;

                try { await Task.Delay(backoffMs, ct); }
                catch { break; }
                backoffMs = Math.Min(backoffMs * 2, 20000);
            }
        }, ct);
    }

    public void Disconnect()
    {
        LeaveVoice();
        try { _gwCts?.Cancel(); } catch { }
        try { _gwCts?.Dispose(); } catch { }
        _gwCts = null;
        try { _gw?.Dispose(); } catch { }
        _gw = null;
        _sessionId = "";
        _lastSeq = null;
        _resumeGatewayUrl = "";
        IsConnected = false;
        IsConnecting = false;
        IsDemoMode = false;
    }

    private void HandleGatewayPayload(ClientWebSocket gw, byte[] data, int len, CancellationTokenSource connCts, ref int backoffMs)
    {
        using var doc = JsonDocument.Parse(data.AsMemory(0, len));
        var root = doc.RootElement;
        int op = root.GetProperty("op").GetInt32();
        if (root.TryGetProperty("s", out var sEl) && sEl.ValueKind == JsonValueKind.Number)
            _lastSeq = sEl.GetInt32();

        switch (op)
        {
            case 1: // Server requests immediate heartbeat
            {
                string seqStr = _lastSeq.HasValue ? _lastSeq.Value.ToString() : "null";
                _ = SendWsTextAsync(gw, $"{{\"op\":1,\"d\":{seqStr}}}", connCts.Token);
                break;
            }
            case 7: // RECONNECT
                SettingsStore.Log("GW OP 7 RECONNECT -> resuming");
                IsConnected = false;
                StatusText = "Переподключение к Discord...";
                Notify();
                connCts.Cancel();
                break;
            case 9: // INVALID_SESSION
            {
                bool resumable = root.TryGetProperty("d", out var d9) && d9.ValueKind == JsonValueKind.True;
                SettingsStore.Log($"GW OP 9 INVALID_SESSION resumable={resumable}");
                if (!resumable)
                {
                    _sessionId = "";
                    _lastSeq = null;
                    _resumeGatewayUrl = "";
                }
                IsConnected = false;
                StatusText = "Обновление сессии Discord...";
                Notify();
                connCts.Cancel();
                break;
            }
            case 10: // HELLO
            {
                int hbMs = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                _ = HeartbeatLoopAsync(gw, hbMs, connCts.Token);
                if (!string.IsNullOrEmpty(_sessionId) && _lastSeq.HasValue)
                    _ = SendResumeAsync(gw, connCts.Token);
                else
                    _ = SendIdentifyAsync(gw, connCts.Token);
                break;
            }
            case 11: // HEARTBEAT ACK
                _hbAcked = true;
                GatewayPingMs = Math.Clamp((int)(Environment.TickCount64 - _lastHbSent), 1, 999);
                Notify();
                break;
            case 0: // DISPATCH
            {
                string t = root.GetProperty("t").GetString() ?? "";
                if (t is "READY" or "RESUMED") backoffMs = 1000;
                if (t == "RESUMED")
                {
                    IsConnected = true;
                    IsConnecting = false;
                    int gCnt; lock (Guilds) gCnt = Guilds.Count;
                    StatusText = $"Подключён: {Me.DisplayName} ({gCnt} серверов)";
                    SettingsStore.Log("GW RESUMED cleanly");
                    Notify();
                    break;
                }
                var d = root.GetProperty("d");
                HandleDispatch(t, d);
                break;
            }
        }
    }

    private long _lastHbSent;

    private async Task HeartbeatLoopAsync(ClientWebSocket gw, int intervalMs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(Math.Min(intervalMs / 2, 5000), ct);
            while (!ct.IsCancellationRequested && gw.State == WebSocketState.Open)
            {
                if (!_hbAcked)
                {
                    SettingsStore.Log("GW heartbeat ACK missed -> aborting zombie socket");
                    gw.Abort();
                    return;
                }
                _hbAcked = false;
                _lastHbSent = Environment.TickCount64;
                string seqStr = _lastSeq.HasValue ? _lastSeq.Value.ToString() : "null";
                await SendWsTextAsync(gw, $"{{\"op\":1,\"d\":{seqStr}}}", ct);
                await Task.Delay(intervalMs, ct);
            }
        }
        catch { }
    }

    private async Task SendIdentifyAsync(ClientWebSocket gw, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("op", 2);
            w.WriteStartObject("d");
            w.WriteString("token", _token);
            w.WriteNumber("capabilities", 16381);
            w.WriteStartObject("properties");
            w.WriteString("os", "Windows");
            w.WriteString("browser", "Chrome");
            w.WriteString("device", "");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        await SendWsTextAsync(gw, Encoding.UTF8.GetString(ms.ToArray()), ct);
    }

    private async Task SendResumeAsync(ClientWebSocket gw, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("op", 6);
            w.WriteStartObject("d");
            w.WriteString("token", _token);
            w.WriteString("session_id", _sessionId);
            w.WriteNumber("seq", _lastSeq ?? 0);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        SettingsStore.Log($"GW sending OP 6 RESUME seq={_lastSeq}");
        await SendWsTextAsync(gw, Encoding.UTF8.GetString(ms.ToArray()), ct);
    }

    private void HandleDispatch(string type, JsonElement d)
    {
        switch (type)
        {
            case "READY": ParseReady(d); break;
            case "READY_SUPPLEMENTAL": ParseReadySupplemental(d); break;
            case "GUILD_CREATE": ParseGuildCreate(d); break;
            case "GUILD_DELETE": ParseGuildDelete(d); break;
            case "CHANNEL_CREATE": ParseChannelUpsert(d); break;
            case "CHANNEL_UPDATE": ParseChannelUpsert(d); break;
            case "CHANNEL_DELETE": ParseChannelDelete(d); break;
            case "MESSAGE_CREATE": ParseMessageCreate(d); break;
            case "MESSAGE_UPDATE": ParseMessageUpdate(d); break;
            case "MESSAGE_DELETE": ParseMessageDelete(d); break;
            case "MESSAGE_DELETE_BULK": ParseMessageDeleteBulk(d); break;
            case "MESSAGE_REACTION_ADD": ParseReactionAdd(d); break;
            case "MESSAGE_REACTION_REMOVE": ParseReactionRemove(d); break;
            case "PRESENCE_UPDATE": ParsePresenceUpdate(d); break;
            case "TYPING_START": ParseTypingStart(d); break;
            case "VOICE_STATE_UPDATE": ParseVoiceStateUpdate(d); break;
            case "VOICE_SERVER_UPDATE": ParseVoiceServerUpdate(d); break;
            case "STREAM_CREATE": ParseStreamCreate(d); break;
            case "STREAM_SERVER_UPDATE": ParseStreamServerUpdate(d); break;
            case "STREAM_DELETE": ParseStreamDelete(d); break;
        }
    }

    private static ulong ParsePermBits(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String && ulong.TryParse(el.GetString(), out var s)) return s;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt64(out var n)) return n;
        return 0UL;
    }

    private static DiscordChannel ParseGuildChannelElement(JsonElement cEl, string guildId)
    {
        var ch = new DiscordChannel
        {
            Id = cEl.GetProperty("id").GetString() ?? "",
            GuildId = guildId,
            Name = cEl.TryGetProperty("name", out var cn) ? cn.GetString() ?? "" : "",
            Topic = cEl.TryGetProperty("topic", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() ?? "" : "",
            Type = cEl.TryGetProperty("type", out var ct) ? ct.GetInt32() : 0,
            Position = cEl.TryGetProperty("position", out var pos) ? pos.GetInt32() : 0,
            ParentId = cEl.TryGetProperty("parent_id", out var pid) && pid.ValueKind == JsonValueKind.String ? pid.GetString() ?? "" : ""
        };
        if (cEl.TryGetProperty("permission_overwrites", out var pows) && pows.ValueKind == JsonValueKind.Array)
        {
            foreach (var ow in pows.EnumerateArray())
            {
                string oid = ow.TryGetProperty("id", out var oie) ? oie.GetString() ?? "" : "";
                int otype = 0;
                if (ow.TryGetProperty("type", out var ote))
                {
                    if (ote.ValueKind == JsonValueKind.Number) otype = ote.GetInt32();
                    else if (ote.ValueKind == JsonValueKind.String && ote.GetString() == "member") otype = 1;
                }
                ulong allow = ow.TryGetProperty("allow", out var ae) ? ParsePermBits(ae) : 0UL;
                ulong deny = ow.TryGetProperty("deny", out var de) ? ParsePermBits(de) : 0UL;
                if (!string.IsNullOrEmpty(oid)) ch.Overwrites.Add(new ChannelOverwrite(oid, otype, allow, deny));
            }
        }
        return ch;
    }

    private bool CanViewChannel(DiscordGuild g, DiscordChannel ch)
    {
        if (ch.Inaccessible) return false;
        const ulong PERM_ADMIN = 1UL << 3;
        const ulong PERM_VIEW = 1UL << 10;
        if (!string.IsNullOrEmpty(g.OwnerId) && g.OwnerId == Me.Id) return true;
        if (g.RolePerms.Count == 0 && g.EveryonePerms == ulong.MaxValue) return true;

        ulong perms = g.EveryonePerms;
        foreach (var rid in g.MyRoleIds)
            if (g.RolePerms.TryGetValue(rid, out var rp)) perms |= rp;
        if ((perms & PERM_ADMIN) != 0) return true;

        // 1. @everyone overwrite on channel
        for (int i = 0; i < ch.Overwrites.Count; i++)
        {
            var ow = ch.Overwrites[i];
            if (ow.Id == g.Id) perms = (perms & ~ow.Deny) | ow.Allow;
        }
        // 2. Role overwrites OR'd together
        ulong roleAllow = 0UL, roleDeny = 0UL;
        for (int i = 0; i < ch.Overwrites.Count; i++)
        {
            var ow = ch.Overwrites[i];
            if (ow.Id != g.Id && g.MyRoleIds.Contains(ow.Id))
            {
                roleAllow |= ow.Allow;
                roleDeny |= ow.Deny;
            }
        }
        perms = (perms & ~roleDeny) | roleAllow;
        // 3. Member-specific overwrite
        for (int i = 0; i < ch.Overwrites.Count; i++)
        {
            var ow = ch.Overwrites[i];
            if (ow.Id == Me.Id) perms = (perms & ~ow.Deny) | ow.Allow;
        }
        return (perms & PERM_VIEW) != 0;
    }

    private void RebuildGuildVisibleChannels(DiscordGuild g)
    {
        var visibleNonCat = g.AllChannels.Where(c => !c.IsCategory && CanViewChannel(g, c)).ToList();
        var nonEmptyParentIds = new HashSet<string>(visibleNonCat.Where(c => !string.IsNullOrEmpty(c.ParentId)).Select(c => c.ParentId));
        var cats = g.AllChannels.Where(c => c.IsCategory && nonEmptyParentIds.Contains(c.Id)).OrderBy(c => c.Position).ToList();
        var uncat = visibleNonCat.Where(c => string.IsNullOrEmpty(c.ParentId) || cats.All(cat => cat.Id != c.ParentId))
            .OrderBy(c => c.IsVoice ? 1 : 0).ThenBy(c => c.Position);

        var ordered = new List<DiscordChannel>(visibleNonCat.Count + cats.Count);
        ordered.AddRange(uncat);
        foreach (var cat in cats)
        {
            ordered.Add(cat);
            ordered.AddRange(visibleNonCat.Where(c => c.ParentId == cat.Id).OrderBy(c => c.IsVoice ? 1 : 0).ThenBy(c => c.Position));
        }
        g.Channels = ordered;
    }

    private DiscordGuild ParseGuildElement(JsonElement gEl, JsonElement? mergedMembersSlice)
    {
        string gid = gEl.GetProperty("id").GetString() ?? "";
        string gname = "Server";
        string gicon = "";
        string ownerId = "";
        if (gEl.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            if (props.TryGetProperty("name", out var pn) && pn.ValueKind == JsonValueKind.String) gname = pn.GetString()!;
            if (props.TryGetProperty("icon", out var pi) && pi.ValueKind == JsonValueKind.String) gicon = pi.GetString()!;
            if (props.TryGetProperty("owner_id", out var po) && po.ValueKind == JsonValueKind.String) ownerId = po.GetString()!;
        }
        if (gEl.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) gname = n.GetString()!;
        if (gEl.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String) gicon = ic.GetString()!;
        if (gEl.TryGetProperty("owner_id", out var oi) && oi.ValueKind == JsonValueKind.String) ownerId = oi.GetString()!;

        var g = new DiscordGuild { Id = gid, Name = gname, Icon = gicon, OwnerId = ownerId };

        if (gEl.TryGetProperty("roles", out var rolesEl) && rolesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var rEl in rolesEl.EnumerateArray())
            {
                string rid = rEl.TryGetProperty("id", out var rie) ? rie.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(rid)) continue;
                ulong p = rEl.TryGetProperty("permissions", out var pe) ? ParsePermBits(pe) : 0UL;
                g.RolePerms[rid] = p;
                if (rid == gid) g.EveryonePerms = p;
            }
        }

        void ParseMemberObj(JsonElement mm)
        {
            string muid = mm.TryGetProperty("user_id", out var mu) ? mu.GetString() ?? "" :
                          (mm.TryGetProperty("user", out var uo) && uo.ValueKind == JsonValueKind.Object && uo.TryGetProperty("id", out var uie) ? uie.GetString() ?? "" : "");
            string mnick = mm.TryGetProperty("nick", out var mn) && mn.ValueKind == JsonValueKind.String ? mn.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(muid))
            {
                var mset = GuildMemberIds.GetOrAdd(gid, _ => new HashSet<string>());
                lock (mset) mset.Add(muid);
                if (!string.IsNullOrEmpty(mnick)) GuildNicks[gid + ":" + muid] = mnick;
                if (muid == Me.Id && mm.TryGetProperty("roles", out var mr) && mr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var re in mr.EnumerateArray())
                        if (re.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(re.GetString()))
                            g.MyRoleIds.Add(re.GetString()!);
                }
            }
        }

        if (mergedMembersSlice.HasValue && mergedMembersSlice.Value.ValueKind == JsonValueKind.Array)
            foreach (var mm in mergedMembersSlice.Value.EnumerateArray()) ParseMemberObj(mm);
        if (gEl.TryGetProperty("members", out var memsEl) && memsEl.ValueKind == JsonValueKind.Array)
            foreach (var mm in memsEl.EnumerateArray()) ParseMemberObj(mm);

        if (gEl.TryGetProperty("channels", out var chs) && chs.ValueKind == JsonValueKind.Array)
        {
            foreach (var cEl in chs.EnumerateArray())
                g.AllChannels.Add(ParseGuildChannelElement(cEl, gid));
            RebuildGuildVisibleChannels(g);
        }
        if (gEl.TryGetProperty("voice_states", out var vss) && vss.ValueKind == JsonValueKind.Array)
        {
            foreach (var vs in vss.EnumerateArray()) UpsertVoiceState(g, vs, emitSounds: false, allowFetch: false);
        }
        return g;
    }

    private void ParseGuildCreate(JsonElement d)
    {
        if (!d.TryGetProperty("id", out var idEl)) return;
        string gid = idEl.GetString() ?? "";
        if (string.IsNullOrEmpty(gid)) return;
        var parsed = ParseGuildElement(d, null);
        lock (Guilds)
        {
            int idx = Guilds.FindIndex(x => x.Id == gid);
            if (idx >= 0) Guilds[idx] = parsed;
            else Guilds.Add(parsed);
        }
        Notify();
    }

    private void ParseGuildDelete(JsonElement d)
    {
        string gid = d.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(gid)) return;
        if (d.TryGetProperty("unavailable", out var unEl) && unEl.ValueKind == JsonValueKind.True) return;
        if (_voiceGuildId == gid) LeaveVoice();
        lock (Guilds) Guilds.RemoveAll(x => x.Id == gid);
        if (ActiveGuildId == gid)
        {
            ActiveGuildId = "@me";
            ActiveChannelId = "";
        }
        Notify();
    }

    private void ParseChannelUpsert(JsonElement d)
    {
        string cid = d.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(cid)) return;
        int type = d.TryGetProperty("type", out var te) ? te.GetInt32() : 0;
        if (type is 1 or 3)
        {
            string name = d.TryGetProperty("name", out var cn) && cn.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(cn.GetString()) ? cn.GetString()! : "DM";
            string recId = "", recAv = "";
            if (d.TryGetProperty("recipients", out var recs) && recs.ValueKind == JsonValueKind.Array && recs.GetArrayLength() > 0)
            {
                var r0 = recs[0];
                recId = r0.TryGetProperty("id", out var rid) ? rid.GetString() ?? "" : "";
                recAv = r0.TryGetProperty("avatar", out var rav) && rav.ValueKind == JsonValueKind.String ? rav.GetString() ?? "" : "";
                if (name == "DM")
                    name = r0.TryGetProperty("global_name", out var rgn) && rgn.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rgn.GetString())
                        ? rgn.GetString()! : (r0.TryGetProperty("username", out var rn) ? rn.GetString() ?? "DM" : "DM");
            }
            var dm = new DiscordChannel { Id = cid, GuildId = "@me", Name = name, RecipientId = recId, RecipientAvatar = recAv, Type = type };
            lock (DmChannels)
            {
                int idx = DmChannels.FindIndex(x => x.Id == cid);
                if (idx >= 0) DmChannels[idx] = dm;
                else DmChannels.Insert(0, dm);
            }
            Notify();
            return;
        }
        string gid = d.TryGetProperty("guild_id", out var ge) && ge.ValueKind == JsonValueKind.String ? ge.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(gid)) return;
        lock (Guilds)
        {
            var g = Guilds.FirstOrDefault(x => x.Id == gid);
            if (g != null)
            {
                var parsed = ParseGuildChannelElement(d, gid);
                int idx = g.AllChannels.FindIndex(x => x.Id == cid);
                if (idx >= 0) g.AllChannels[idx] = parsed;
                else g.AllChannels.Add(parsed);
                RebuildGuildVisibleChannels(g);
            }
        }
        Notify();
    }

    private void ParseChannelDelete(JsonElement d)
    {
        string cid = d.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(cid)) return;
        string gid = d.TryGetProperty("guild_id", out var ge) && ge.ValueKind == JsonValueKind.String ? ge.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(gid))
        {
            lock (DmChannels) DmChannels.RemoveAll(x => x.Id == cid);
        }
        else
        {
            lock (Guilds)
            {
                var g = Guilds.FirstOrDefault(x => x.Id == gid);
                if (g != null)
                {
                    g.AllChannels.RemoveAll(x => x.Id == cid);
                    RebuildGuildVisibleChannels(g);
                    if (ActiveChannelId == cid)
                    {
                        var fallback = g.Channels.FirstOrDefault(c => c.Type == 0 || c.Type == 5);
                        ActiveChannelId = fallback?.Id ?? "";
                        if (!string.IsNullOrEmpty(ActiveChannelId)) _ = LoadMessagesAsync(ActiveChannelId);
                    }
                }
            }
        }
        Messages.TryRemove(cid, out _);
        UnreadChannels.TryRemove(cid, out _);
        LocalCache.DeleteChannelMessages(cid);
        Notify();
    }

    private void ParseMessageDeleteBulk(JsonElement d)
    {
        string cid = d.TryGetProperty("channel_id", out var c) ? c.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(cid) || !d.TryGetProperty("ids", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array) return;
        var ids = new List<string>();
        foreach (var ie in idsEl.EnumerateArray())
            if (ie.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ie.GetString()))
                ids.Add(ie.GetString()!);
        if (ids.Count == 0) return;
        var idSet = new HashSet<string>(ids);
        if (Messages.TryGetValue(cid, out var list))
        {
            lock (list) list.RemoveAll(m => idSet.Contains(m.Id));
            Notify();
        }
        LocalCache.DeleteMessagesBulk(ids);
    }

    private void ParseReady(JsonElement d)
    {
        _sessionId = d.TryGetProperty("session_id", out var sid) ? sid.GetString() ?? "" : "";
        _resumeGatewayUrl = d.TryGetProperty("resume_gateway_url", out var rgu) && rgu.ValueKind == JsonValueKind.String
            ? rgu.GetString() ?? "" : "";
        if (d.TryGetProperty("user", out var u))
        {
            Me = new DiscordUser
            {
                Id = u.GetProperty("id").GetString() ?? "",
                Username = u.GetProperty("username").GetString() ?? "",
                DisplayName = u.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(gn.GetString())
                    ? gn.GetString()! : (u.GetProperty("username").GetString() ?? ""),
                Avatar = u.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString() ?? "" : "",
            };
        }

        // Cache users FIRST so voice states and merged_members resolve immediately
        if (d.TryGetProperty("users", out var usrs) && usrs.ValueKind == JsonValueKind.Array)
        {
            foreach (var ue in usrs.EnumerateArray())
            {
                string uid = ue.TryGetProperty("id", out var uide) ? uide.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(uid)) continue;
                Users[uid] = new DiscordUser
                {
                    Id = uid,
                    Username = ue.TryGetProperty("username", out var une) ? une.GetString() ?? "" : "",
                    DisplayName = ue.TryGetProperty("global_name", out var gne) && gne.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(gne.GetString())
                        ? gne.GetString()! : (ue.TryGetProperty("username", out var un2) ? un2.GetString() ?? "" : ""),
                    Avatar = ue.TryGetProperty("avatar", out var ave) && ave.ValueKind == JsonValueKind.String ? ave.GetString() ?? "" : "",
                };
            }
        }

        var parsedGuilds = new List<DiscordGuild>();
        if (d.TryGetProperty("guilds", out var gs) && gs.ValueKind == JsonValueKind.Array)
        {
            int gIdx = 0;
            bool hasMerged = d.TryGetProperty("merged_members", out var mergedArr) && mergedArr.ValueKind == JsonValueKind.Array;
            int mergedLen = hasMerged ? mergedArr.GetArrayLength() : 0;

            foreach (var gEl in gs.EnumerateArray())
            {
                JsonElement? mmSlice = (hasMerged && gIdx < mergedLen) ? mergedArr[gIdx] : null;
                gIdx++;
                parsedGuilds.Add(ParseGuildElement(gEl, mmSlice));
            }
        }
        lock (Guilds) { Guilds.Clear(); Guilds.AddRange(parsedGuilds); }

        var parsedDms = new List<DiscordChannel>();
        if (d.TryGetProperty("private_channels", out var pcs) && pcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var cEl in pcs.EnumerateArray())
            {
                string name = "DM";
                string recId = "", recAv = "";
                if (cEl.TryGetProperty("name", out var cn) && cn.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(cn.GetString()))
                    name = cn.GetString()!;
                else if (cEl.TryGetProperty("recipient_ids", out var rids) && rids.ValueKind == JsonValueKind.Array && rids.GetArrayLength() > 0)
                {
                    recId = rids[0].GetString() ?? "";
                    if (Users.TryGetValue(recId, out var ru)) { name = ru.DisplayName; recAv = ru.Avatar; }
                }
                else if (cEl.TryGetProperty("recipients", out var recs) && recs.ValueKind == JsonValueKind.Array && recs.GetArrayLength() > 0)
                {
                    var r0 = recs[0];
                    recId = r0.TryGetProperty("id", out var rid) ? rid.GetString() ?? "" : "";
                    recAv = r0.TryGetProperty("avatar", out var rav) && rav.ValueKind == JsonValueKind.String ? rav.GetString() ?? "" : "";
                    name = r0.TryGetProperty("global_name", out var rgn) && rgn.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rgn.GetString())
                        ? rgn.GetString()! : (r0.TryGetProperty("username", out var rn) ? rn.GetString() ?? "DM" : "DM");
                }
                parsedDms.Add(new DiscordChannel
                {
                    Id = cEl.GetProperty("id").GetString() ?? "",
                    GuildId = "@me",
                    Name = name,
                    RecipientId = recId,
                    RecipientAvatar = recAv,
                    LastMessageId = cEl.TryGetProperty("last_message_id", out var lm) && lm.ValueKind == JsonValueKind.String ? lm.GetString() ?? "" : "",
                    Type = cEl.TryGetProperty("type", out var ct) ? ct.GetInt32() : 1
                });
            }
            parsedDms.Sort((a, b) =>
            {
                ulong la = ulong.TryParse(a.LastMessageId, out var x) ? x : 0;
                ulong lb = ulong.TryParse(b.LastMessageId, out var y) ? y : 0;
                return lb.CompareTo(la);
            });
        }
        lock (DmChannels) { DmChannels.Clear(); DmChannels.AddRange(parsedDms); }

        // Parse Friends (relationships) + initial presences
        var initStatus = new Dictionary<string, string>();
        if (d.TryGetProperty("presences", out var pres) && pres.ValueKind == JsonValueKind.Array)
        {
            foreach (var pr in pres.EnumerateArray())
            {
                string puid = pr.TryGetProperty("user", out var pu) && pu.TryGetProperty("id", out var pi) ? pi.GetString() ?? "" : "";
                string pst = pr.TryGetProperty("status", out var ps) && ps.ValueKind == JsonValueKind.String ? ps.GetString() ?? "offline" : "offline";
                if (!string.IsNullOrEmpty(puid))
                {
                    initStatus[puid] = pst;
                    if (Users.TryGetValue(puid, out var uo)) uo.Status = pst;
                }
            }
        }
        var parsedFriends = new List<DiscordFriend>();
        if (d.TryGetProperty("relationships", out var rels) && rels.ValueKind == JsonValueKind.Array)
        {
            foreach (var rel in rels.EnumerateArray())
            {
                int rtype = rel.TryGetProperty("type", out var rt) ? rt.GetInt32() : 0;
                if (rtype != 1) continue; // Friends only
                string fuid = rel.TryGetProperty("user_id", out var f1) ? f1.GetString() ?? "" : (rel.TryGetProperty("id", out var f2) ? f2.GetString() ?? "" : "");
                if (string.IsNullOrEmpty(fuid)) continue;
                string uname = "", dname = "", fav = "";
                if (rel.TryGetProperty("user", out var fu) && fu.ValueKind == JsonValueKind.Object)
                {
                    uname = fu.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "";
                    dname = fu.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(gn.GetString()) ? gn.GetString()! : uname;
                    fav = fu.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString() ?? "" : "";
                }
                else if (Users.TryGetValue(fuid, out var cu))
                {
                    uname = cu.Username; dname = cu.DisplayName; fav = cu.Avatar;
                }
                if (string.IsNullOrEmpty(dname)) dname = uname;
                string fst = initStatus.GetValueOrDefault(fuid, "offline");
                parsedFriends.Add(new DiscordFriend { UserId = fuid, Username = uname, DisplayName = dname, Avatar = fav, Status = fst, Type = rtype });
            }
            parsedFriends.Sort((a, b) =>
            {
                int oa = a.Status == "offline" ? 1 : 0, ob = b.Status == "offline" ? 1 : 0;
                int c = oa.CompareTo(ob);
                return c != 0 ? c : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
        }
        lock (Friends) { Friends.Clear(); Friends.AddRange(parsedFriends); }

        IsConnected = true;
        IsConnecting = false;
        StatusText = $"Подключён: {Me.DisplayName} ({parsedGuilds.Count} серверов)";

        // Restore last visited server & channel if valid, else fall back to first guild text channel
        bool restored = false;
        string savedGid = SettingsStore.Current.LastGuildId;
        string savedCid = SettingsStore.Current.LastChannelId;
        if (savedGid == "@me" && !string.IsNullOrEmpty(savedCid) && parsedDms.Any(c => c.Id == savedCid))
        {
            ActiveGuildId = "@me";
            ActiveChannelId = savedCid;
            _ = LoadMessagesAsync(savedCid);
            restored = true;
        }
        else if (!string.IsNullOrEmpty(savedGid))
        {
            var sg = parsedGuilds.FirstOrDefault(x => x.Id == savedGid);
            if (sg != null)
            {
                ActiveGuildId = sg.Id;
                var sc = sg.Channels.FirstOrDefault(c => c.Id == savedCid && !c.IsCategory && !c.IsVoice)
                         ?? sg.Channels.FirstOrDefault(c => c.Type == 0 || c.Type == 5);
                if (sc != null)
                {
                    ActiveChannelId = sc.Id;
                    _ = LoadMessagesAsync(sc.Id);
                }
                restored = true;
            }
        }
        if (!restored && parsedGuilds.Count > 0)
        {
            ActiveGuildId = parsedGuilds[0].Id;
            var firstText = parsedGuilds[0].Channels.FirstOrDefault(c => c.Type == 0 || c.Type == 5);
            if (firstText != null)
            {
                ActiveChannelId = firstText.Id;
                _ = LoadMessagesAsync(firstText.Id);
            }
        }

        Me.Status = SettingsStore.Current.PresenceStatus;
        SettingsStore.SetToken(_token);
        _ = BenchmarkRegionsAsync();
        if (Me.Status != "online") _ = SetPresenceStatusAsync(Me.Status);
        Notify();
        ReadyCompleted?.Invoke();
    }

    private void ParseMessageCreate(JsonElement d)
    {
        var msg = ParseMessage(d);
        var list = Messages.GetOrAdd(msg.ChannelId, _ => []);
        lock (list)
        {
            list.Add(msg);
            if (list.Count > 300) list.RemoveAt(0);
        }
        LocalCache.SaveMessages(msg.ChannelId, [msg]);
        if (!string.IsNullOrEmpty(msg.AuthorId) && !Users.ContainsKey(msg.AuthorId))
            Users[msg.AuthorId] = new DiscordUser { Id = msg.AuthorId, Username = msg.AuthorName, DisplayName = msg.AuthorName, Avatar = msg.AuthorAvatar };
        if (d.TryGetProperty("guild_id", out var gEl) && gEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(msg.AuthorId))
        {
            var mset = GuildMemberIds.GetOrAdd(gEl.GetString()!, _ => new HashSet<string>());
            lock (mset) mset.Add(msg.AuthorId);
        }
        if (msg.ChannelId != ActiveChannelId && msg.AuthorId != Me.Id)
            UnreadChannels[msg.ChannelId] = 1;
        if (msg.AuthorId != Me.Id && (msg.ChannelId != ActiveChannelId || Shell.IsInVoiceStage))
            SoundFx.Play(UiSound.Message);
        if (msg.AuthorId != Me.Id && SettingsStore.Current.DesktopNotifications && !SettingsStore.Current.StreamerMode &&
            W.GetForegroundWindow() != App.Hwnd)
        {
            W.FlashWindow(App.Hwnd, true);
        }
        TypingUsers.TryRemove(msg.ChannelId, out _);
        Notify();
    }

    private void ParsePresenceUpdate(JsonElement d)
    {
        string uid = d.TryGetProperty("user", out var u) && u.TryGetProperty("id", out var ie) ? ie.GetString() ?? "" : "";
        string st = d.TryGetProperty("status", out var se) && se.ValueKind == JsonValueKind.String ? se.GetString() ?? "offline" : "offline";
        if (string.IsNullOrEmpty(uid)) return;
        if (Users.TryGetValue(uid, out var uo)) uo.Status = st;
        lock (Friends)
        {
            var fr = Friends.FirstOrDefault(f => f.UserId == uid);
            if (fr != null) fr.Status = st;
        }
        Notify();
    }

    private static (string Display, string ApiKey) ParseEmojiKey(JsonElement em)
    {
        string name = em.TryGetProperty("name", out var en) && en.ValueKind == JsonValueKind.String ? en.GetString() ?? "" : "";
        string id = em.TryGetProperty("id", out var ei) && ei.ValueKind == JsonValueKind.String ? ei.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(name)) return ("", "");
        if (!string.IsNullOrEmpty(id)) return ($":{name}:", $"{name}:{id}");
        return (name, name);
    }

    private void ParseReactionAdd(JsonElement d)
    {
        string cid = d.TryGetProperty("channel_id", out var c) ? c.GetString() ?? "" : "";
        string mid = d.TryGetProperty("message_id", out var m) ? m.GetString() ?? "" : "";
        string uid = d.TryGetProperty("user_id", out var u) ? u.GetString() ?? "" : "";
        if (!d.TryGetProperty("emoji", out var em)) return;
        var (disp, apiKey) = ParseEmojiKey(em);
        if (string.IsNullOrEmpty(cid) || string.IsNullOrEmpty(mid) || string.IsNullOrEmpty(apiKey)) return;
        if (Messages.TryGetValue(cid, out var list))
        {
            lock (list)
            {
                var msg = list.FirstOrDefault(x => x.Id == mid);
                if (msg != null)
                {
                    var next = msg.Reactions.Select(r => new ReactionEntry { Display = r.Display, ApiKey = r.ApiKey, Count = r.Count, Me = r.Me }).ToList();
                    int idx = next.FindIndex(r => r.ApiKey == apiKey);
                    if (idx >= 0)
                    {
                        next[idx].Count++;
                        if (uid == Me.Id) next[idx].Me = true;
                    }
                    else next.Add(new ReactionEntry { Display = disp, ApiKey = apiKey, Count = 1, Me = uid == Me.Id });
                    msg.Reactions = [.. next];
                }
            }
            Notify();
        }
    }

    private void ParseReactionRemove(JsonElement d)
    {
        string cid = d.TryGetProperty("channel_id", out var c) ? c.GetString() ?? "" : "";
        string mid = d.TryGetProperty("message_id", out var m) ? m.GetString() ?? "" : "";
        string uid = d.TryGetProperty("user_id", out var u) ? u.GetString() ?? "" : "";
        if (!d.TryGetProperty("emoji", out var em)) return;
        var (_, apiKey) = ParseEmojiKey(em);
        if (string.IsNullOrEmpty(cid) || string.IsNullOrEmpty(mid) || string.IsNullOrEmpty(apiKey)) return;
        if (Messages.TryGetValue(cid, out var list))
        {
            lock (list)
            {
                var msg = list.FirstOrDefault(x => x.Id == mid);
                if (msg != null)
                {
                    var next = msg.Reactions.Select(r => new ReactionEntry { Display = r.Display, ApiKey = r.ApiKey, Count = r.Count, Me = r.Me }).ToList();
                    int idx = next.FindIndex(r => r.ApiKey == apiKey);
                    if (idx >= 0)
                    {
                        next[idx].Count--;
                        if (uid == Me.Id) next[idx].Me = false;
                        if (next[idx].Count <= 0) next.RemoveAt(idx);
                    }
                    msg.Reactions = [.. next];
                }
            }
            Notify();
        }
    }

    private void ParseMessageUpdate(JsonElement d)
    {
        string cid = d.TryGetProperty("channel_id", out var c) ? c.GetString() ?? "" : "";
        string mid = d.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(cid) || string.IsNullOrEmpty(mid)) return;

        bool hasContent = d.TryGetProperty("content", out var cnt) && cnt.ValueKind == JsonValueKind.String;
        bool hasEdited = d.TryGetProperty("edited_timestamp", out var et) && et.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(et.GetString());
        bool hasEmbeds = d.TryGetProperty("embeds", out var embs) && embs.ValueKind == JsonValueKind.Array;
        if (!hasContent && !hasEmbeds && !hasEdited) return;

        string? newContent = null;
        if (Messages.TryGetValue(cid, out var list))
        {
            lock (list)
            {
                var existing = list.FirstOrDefault(m => m.Id == mid);
                if (existing != null)
                {
                    if (hasContent)
                    {
                        newContent = ResolveMentions(cnt.GetString()!);
                        existing.Content = newContent;
                    }
                    if (hasEdited) existing.IsEdited = true;
                    if (hasEmbeds)
                    {
                        var nextEmbs = new List<DiscordEmbed>();
                        ParseEmbeds(embs, nextEmbs);
                        existing.Embeds = [.. nextEmbs];
                    }
                }
            }
            if (newContent != null) LocalCache.UpdateMessage(mid, newContent);
            Notify();
        }
    }

    private void ParseMessageDelete(JsonElement d)
    {
        string cid = d.TryGetProperty("channel_id", out var c) ? c.GetString() ?? "" : "";
        string mid = d.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(cid) || string.IsNullOrEmpty(mid)) return;
        if (Messages.TryGetValue(cid, out var list))
        {
            lock (list) list.RemoveAll(m => m.Id == mid);
            Notify();
        }
        LocalCache.DeleteMessage(mid);
    }

    private void ParseTypingStart(JsonElement d)
    {
        string cid = d.TryGetProperty("channel_id", out var c) ? c.GetString() ?? "" : "";
        string uid = d.TryGetProperty("user_id", out var u) ? u.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(cid) || uid == Me.Id) return;
        string name = Users.TryGetValue(uid, out var uo) ? uo.DisplayName : "Кто-то";
        TypingUsers[cid] = (name, Environment.TickCount64 + 8000);
        Notify();
    }

    private static void ParseEmbeds(JsonElement embs, List<DiscordEmbed> target)
    {
        foreach (var e in embs.EnumerateArray())
        {
            string imgU = "";
            if (e.TryGetProperty("image", out var im) && im.ValueKind == JsonValueKind.Object && im.TryGetProperty("url", out var iu) && iu.ValueKind == JsonValueKind.String)
                imgU = iu.GetString()!;
            else if (e.TryGetProperty("thumbnail", out var th) && th.ValueKind == JsonValueKind.Object && th.TryGetProperty("url", out var tu) && tu.ValueKind == JsonValueKind.String)
                imgU = tu.GetString()!;
            var emb = new DiscordEmbed
            {
                Title = e.TryGetProperty("title", out var et) && et.ValueKind == JsonValueKind.String ? et.GetString()! : "",
                Description = e.TryGetProperty("description", out var ed) && ed.ValueKind == JsonValueKind.String ? ed.GetString()! : "",
                Url = e.TryGetProperty("url", out var eu) && eu.ValueKind == JsonValueKind.String ? eu.GetString()! : "",
                Author = e.TryGetProperty("author", out var ea) && ea.ValueKind == JsonValueKind.Object && ea.TryGetProperty("name", out var ean) ? ean.GetString() ?? "" : "",
                Footer = e.TryGetProperty("footer", out var ef) && ef.ValueKind == JsonValueKind.Object && ef.TryGetProperty("text", out var eft) ? eft.GetString() ?? "" : "",
                ImageUrl = imgU,
                Color = e.TryGetProperty("color", out var ec) && ec.ValueKind == JsonValueKind.Number ? (0xFF000000u | (uint)ec.GetInt32()) : 0xFF5865F2u
            };
            if (!string.IsNullOrEmpty(emb.Title) || !string.IsNullOrEmpty(emb.Description) || !string.IsNullOrEmpty(emb.ImageUrl))
                target.Add(emb);
        }
    }

    private static string ResolveMentions(string s)
    {
        if (string.IsNullOrEmpty(s) || !s.Contains("<@", StringComparison.Ordinal)) return s;
        var sb = new StringBuilder(s.Length + 16);
        int pos = 0;
        while (pos < s.Length)
        {
            int open = s.IndexOf("<@", pos, StringComparison.Ordinal);
            if (open < 0) { sb.Append(s, pos, s.Length - pos); break; }
            sb.Append(s, pos, open - pos);
            int idStart = open + 2;
            if (idStart < s.Length && s[idStart] == '!') idStart++;
            int close = s.IndexOf('>', idStart);
            if (close > idStart && close - idStart <= 24)
            {
                bool allDigits = true;
                for (int k = idStart; k < close; k++)
                    if (s[k] < '0' || s[k] > '9') { allDigits = false; break; }
                if (allDigits)
                {
                    string uid = s[idStart..close];
                    if (Instance.Users.TryGetValue(uid, out var uo) && !string.IsNullOrEmpty(uo.DisplayName))
                    {
                        sb.Append('@').Append(uo.DisplayName);
                        pos = close + 1;
                        continue;
                    }
                }
            }
            sb.Append("<@");
            pos = open + 2;
        }
        return sb.ToString();
    }

    private static uint ComputeAuthorColor(string seed)
    {
        uint h = 2166136261u;
        if (!string.IsNullOrEmpty(seed))
            foreach (char c in seed) h = (h ^ c) * 16777619u;
        return 0xFF000000u | (((h & 0x7F) + 0x70) << 16) | ((((h >> 8) & 0x7F) + 0x70) << 8) | (((h >> 16) & 0x7F) + 0x70);
    }

    private static DiscordMessage ParseMessage(JsonElement d)
    {
        var msg = new DiscordMessage
        {
            Id = d.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            ChannelId = d.TryGetProperty("channel_id", out var cid) ? cid.GetString() ?? "" : "",
            Content = d.TryGetProperty("content", out var cnt) ? cnt.GetString() ?? "" : "",
            IsEdited = d.TryGetProperty("edited_timestamp", out var et) && et.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(et.GetString()),
        };
        if (d.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(ts.GetString(), out var dto))
            {
                var loc = dto.ToLocalTime();
                msg.Timestamp = loc.Date == DateTime.Now.Date ? loc.ToString("HH:mm") : loc.ToString("dd.MM HH:mm");
            }
        }
        if (d.TryGetProperty("author", out var au))
        {
            msg.AuthorId = au.TryGetProperty("id", out var aid) ? aid.GetString() ?? "" : "";
            msg.AuthorAvatar = au.TryGetProperty("avatar", out var aav) && aav.ValueKind == JsonValueKind.String ? aav.GetString() ?? "" : "";
            msg.AuthorName = au.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(gn.GetString())
                ? gn.GetString()!
                : (au.TryGetProperty("username", out var un) ? un.GetString() ?? "User" : "User");
            msg.AuthorColor = ComputeAuthorColor(!string.IsNullOrEmpty(msg.AuthorId) ? msg.AuthorId : msg.AuthorName);
        }
        if (string.IsNullOrEmpty(msg.Content) && d.TryGetProperty("sticker_items", out var stks) && stks.ValueKind == JsonValueKind.Array && stks.GetArrayLength() > 0)
        {
            string sname = stks[0].TryGetProperty("name", out var sn) && sn.ValueKind == JsonValueKind.String ? sn.GetString() ?? "стикер" : "стикер";
            msg.Content = $"[Стикер: {sname}]";
        }
        if (d.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in atts.EnumerateArray())
            {
                string url = a.TryGetProperty("url", out var uel) && uel.ValueKind == JsonValueKind.String ? uel.GetString()! : "";
                if (string.IsNullOrEmpty(url)) continue;
                string fname = a.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString()! : Path.GetFileName(url.Split('?')[0]);
                long fsz = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
                string ct = a.TryGetProperty("content_type", out var cte) && cte.ValueKind == JsonValueKind.String ? cte.GetString()! : "";
                bool isImg = ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                             url.Contains(".png", StringComparison.OrdinalIgnoreCase) || url.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ||
                             url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase) || url.Contains(".webp", StringComparison.OrdinalIgnoreCase) ||
                             url.Contains(".gif", StringComparison.OrdinalIgnoreCase) || url.Contains(".bmp", StringComparison.OrdinalIgnoreCase);
                msg.AttachmentUrls.Add(url);
                msg.Attachments.Add(new DiscordAttachment { Url = url, Filename = fname, Size = fsz, IsImage = isImg });
            }
        }
        if (d.TryGetProperty("embeds", out var embs) && embs.ValueKind == JsonValueKind.Array)
        {
            var embList = new List<DiscordEmbed>();
            ParseEmbeds(embs, embList);
            msg.Embeds = [.. embList];
        }
        if (d.TryGetProperty("reactions", out var reacts) && reacts.ValueKind == JsonValueKind.Array)
        {
            var rList = new List<ReactionEntry>();
            foreach (var r in reacts.EnumerateArray())
            {
                int rCount = r.TryGetProperty("count", out var rc) ? rc.GetInt32() : 1;
                if (r.TryGetProperty("emoji", out var em))
                {
                    var (disp, apiKey) = ParseEmojiKey(em);
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        bool me = r.TryGetProperty("me", out var meEl) && meEl.ValueKind == JsonValueKind.True;
                        rList.Add(new ReactionEntry { Display = disp, ApiKey = apiKey, Count = rCount, Me = me });
                    }
                }
            }
            msg.Reactions = [.. rList];
        }
        if (d.TryGetProperty("referenced_message", out var refm) && refm.ValueKind == JsonValueKind.Object)
        {
            string ra = refm.TryGetProperty("author", out var rau) && rau.TryGetProperty("username", out var run) ? run.GetString() ?? "" : "";
            string rc = refm.TryGetProperty("content", out var rcnt) ? rcnt.GetString() ?? "" : "";
            if (rc.Length > 70) rc = rc[..70] + "…";
            if (!string.IsNullOrEmpty(ra)) msg.ReplyPreview = $"↩ @{ra}: {rc}";
        }
        msg.Content = ResolveMentions(msg.Content);
        return msg;
    }

    public async Task LoadOlderMessagesAsync(string channelId)
    {
        if (IsDemoMode || string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(channelId)) return;
        if (_reachedChannelStart.ContainsKey(channelId)) return;
        if (!Messages.TryGetValue(channelId, out var list)) return;
        string oldestId;
        lock (list)
        {
            if (list.Count == 0) return;
            oldestId = list[0].Id;
        }
        if (string.IsNullOrEmpty(oldestId) || !_loadingOlder.TryAdd(channelId, 1)) return;
        try
        {
            using var resp = await SendRestAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"https://discord.com/api/v10/channels/{channelId}/messages?before={oldestId}&limit=50"),
                "LoadOlder");
            if (resp == null || !resp.IsSuccessStatusCode) return;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            using var doc = JsonDocument.Parse(bytes);
            var parsed = new List<DiscordMessage>();
            foreach (var el in doc.RootElement.EnumerateArray()) parsed.Add(ParseMessage(el));
            if (parsed.Count < 50) _reachedChannelStart[channelId] = 1;
            if (parsed.Count > 0)
            {
                parsed.Reverse();
                lock (list)
                {
                    var existingIds = new HashSet<string>(list.Select(x => x.Id));
                    var fresh = parsed.Where(x => !existingIds.Contains(x.Id)).ToList();
                    if (fresh.Count > 0) list.InsertRange(0, fresh);
                }
                Notify();
            }
        }
        catch { }
        finally { _loadingOlder.TryRemove(channelId, out _); }
    }

    public async Task LoadMessagesAsync(string channelId)
    {
        if (string.IsNullOrEmpty(channelId)) return;
        UnreadChannels.TryRemove(channelId, out _);
        if (!Messages.ContainsKey(channelId))
        {
            var cached = LocalCache.LoadMessages(channelId, 50);
            if (cached.Count > 0) { Messages[channelId] = cached; Notify(); }
        }
        if (IsDemoMode || string.IsNullOrEmpty(_token)) return;
        try
        {
            using var resp = await SendRestAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"https://discord.com/api/v10/channels/{channelId}/messages?limit=50"),
                "LoadMessages");
            if (resp == null) return;
            if ((int)resp.StatusCode == 403)
            {
                // Hide inaccessible channel so FluxCord never re-polls HTTP 403!
                lock (Guilds)
                {
                    foreach (var g in Guilds)
                    {
                        var ch = g.AllChannels.FirstOrDefault(c => c.Id == channelId);
                        if (ch != null)
                        {
                            ch.Inaccessible = true;
                            RebuildGuildVisibleChannels(g);
                            if (ActiveChannelId == channelId)
                            {
                                var next = g.Channels.FirstOrDefault(c => c.Type == 0 || c.Type == 5);
                                ActiveChannelId = next?.Id ?? "";
                                if (!string.IsNullOrEmpty(ActiveChannelId)) _ = LoadMessagesAsync(ActiveChannelId);
                            }
                            break;
                        }
                    }
                }
                Notify();
                return;
            }
            if (!resp.IsSuccessStatusCode) return;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            using var doc = JsonDocument.Parse(bytes);
            var parsed = new List<DiscordMessage>();
            foreach (var el in doc.RootElement.EnumerateArray()) parsed.Add(ParseMessage(el));
            if (parsed.Count < 50) _reachedChannelStart[channelId] = 1;
            parsed.Reverse();
            ulong minFetchedId = ulong.MaxValue;
            var fetchedIds = new HashSet<string>(parsed.Count);
            foreach (var m in parsed)
            {
                fetchedIds.Add(m.Id);
                if (ulong.TryParse(m.Id, out var u) && u < minFetchedId) minFetchedId = u;
            }
            var list = Messages.GetOrAdd(channelId, _ => []);
            lock (list)
            {
                var byId = new Dictionary<string, DiscordMessage>(parsed.Count + list.Count);
                foreach (var old in list)
                {
                    // Drop messages in the fetched window that were deleted while offline!
                    if (minFetchedId != ulong.MaxValue && ulong.TryParse(old.Id, out var ou) && ou >= minFetchedId && !fetchedIds.Contains(old.Id))
                        continue;
                    byId[old.Id] = old;
                }
                foreach (var m in parsed) byId[m.Id] = m;
                list.Clear();
                list.AddRange(byId.Values.OrderBy(m => ulong.TryParse(m.Id, out var u) ? u : 0UL));
                if (list.Count > 300) list.RemoveRange(0, list.Count - 300);
            }
            LocalCache.ReconcileRecentChannelMessages(channelId, parsed);
            Notify();
        }
        catch (Exception ex) { SettingsStore.Log("LoadMessages error: " + ex.Message); }
    }

    public async Task<bool> SendMessageAsync(string channelId, string content, string? replyToMessageId = null)
    {
        content = content.Trim();
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(channelId)) return false;
        if (IsDemoMode)
        {
            var list = Messages.GetOrAdd(channelId, _ => []);
            lock (list)
            {
                list.Add(new DiscordMessage
                {
                    Id = Guid.NewGuid().ToString("N"), ChannelId = channelId, AuthorId = Me.Id,
                    AuthorName = Me.DisplayName, AuthorColor = 0xFF5865F2, Content = content,
                    Timestamp = DateTime.Now.ToString("HH:mm")
                });
            }
            Notify();
            return true;
        }
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("content", content);
                if (!string.IsNullOrEmpty(replyToMessageId))
                {
                    w.WriteStartObject("message_reference");
                    w.WriteString("message_id", replyToMessageId);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            var bodyBytes = ms.ToArray();
            using var resp = await SendRestAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Post, $"https://discord.com/api/v10/channels/{channelId}/messages");
                r.Content = new ByteArrayContent(bodyBytes);
                r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return r;
            }, "SendMessage");
            return resp is { IsSuccessStatusCode: true };
        }
        catch (Exception ex) { SettingsStore.Log("SendMessage error: " + ex.Message); return false; }
    }

    public async Task DeleteMessageAsync(string channelId, string messageId)
    {
        if (IsDemoMode)
        {
            if (Messages.TryGetValue(channelId, out var list))
            {
                lock (list) list.RemoveAll(m => m.Id == messageId);
                Notify();
            }
            return;
        }
        if (string.IsNullOrEmpty(_token)) return;
        try
        {
            using var resp = await SendRestAsync(
                () => new HttpRequestMessage(HttpMethod.Delete, $"https://discord.com/api/v10/channels/{channelId}/messages/{messageId}"),
                "DeleteMessage");
        }
        catch { }
    }

    public async Task EditMessageAsync(string channelId, string messageId, string newContent)
    {
        newContent = newContent.Trim();
        if (string.IsNullOrEmpty(newContent) || string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(messageId)) return;
        if (IsDemoMode)
        {
            if (Messages.TryGetValue(channelId, out var list))
            {
                lock (list)
                {
                    var m = list.FirstOrDefault(x => x.Id == messageId);
                    if (m != null) { m.Content = newContent; m.IsEdited = true; }
                }
                Notify();
            }
            return;
        }
        if (string.IsNullOrEmpty(_token)) return;
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("content", newContent);
                w.WriteEndObject();
            }
            var bodyBytes = ms.ToArray();
            using var resp = await SendRestAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Patch, $"https://discord.com/api/v10/channels/{channelId}/messages/{messageId}");
                r.Content = new ByteArrayContent(bodyBytes);
                r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return r;
            }, "EditMessage");
        }
        catch (Exception ex) { SettingsStore.Log("EditMessage error: " + ex.Message); }
    }

    public Task ToggleReactionAsync(string channelId, string messageId, string emojiApiKey)
    {
        bool mine = false;
        if (Messages.TryGetValue(channelId, out var list))
        {
            lock (list)
            {
                var msg = list.FirstOrDefault(x => x.Id == messageId);
                if (msg != null) mine = msg.Reactions.Any(r => r.ApiKey == emojiApiKey && r.Me);
            }
        }
        return mine ? RemoveReactionAsync(channelId, messageId, emojiApiKey) : AddReactionAsync(channelId, messageId, emojiApiKey);
    }

    public async Task AddReactionAsync(string channelId, string messageId, string emoji)
    {
        if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(emoji)) return;
        if (IsDemoMode)
        {
            if (Messages.TryGetValue(channelId, out var list))
            {
                lock (list)
                {
                    var msg = list.FirstOrDefault(x => x.Id == messageId);
                    if (msg != null && !msg.Reactions.Any(r => r.ApiKey == emoji && r.Me))
                    {
                        var next = msg.Reactions.Select(r => new ReactionEntry { Display = r.Display, ApiKey = r.ApiKey, Count = r.Count, Me = r.Me }).ToList();
                        int idx = next.FindIndex(r => r.ApiKey == emoji);
                        if (idx >= 0) { next[idx].Count++; next[idx].Me = true; }
                        else next.Add(new ReactionEntry { Display = emoji, ApiKey = emoji, Count = 1, Me = true });
                        msg.Reactions = [.. next];
                    }
                }
                Notify();
            }
            return;
        }
        if (string.IsNullOrEmpty(_token)) return;
        try
        {
            string enc = Uri.EscapeDataString(emoji);
            using var resp = await SendRestAsync(
                () => new HttpRequestMessage(HttpMethod.Put, $"https://discord.com/api/v10/channels/{channelId}/messages/{messageId}/reactions/{enc}/@me"),
                "AddReaction");
        }
        catch { }
    }

    public async Task RemoveReactionAsync(string channelId, string messageId, string emoji)
    {
        if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(emoji)) return;
        if (IsDemoMode)
        {
            if (Messages.TryGetValue(channelId, out var list))
            {
                lock (list)
                {
                    var msg = list.FirstOrDefault(x => x.Id == messageId);
                    if (msg != null && msg.Reactions.Any(r => r.ApiKey == emoji && r.Me))
                    {
                        var next = msg.Reactions.Select(r => new ReactionEntry { Display = r.Display, ApiKey = r.ApiKey, Count = r.Count, Me = r.Me }).ToList();
                        int idx = next.FindIndex(r => r.ApiKey == emoji);
                        if (idx >= 0)
                        {
                            next[idx].Count--;
                            next[idx].Me = false;
                            if (next[idx].Count <= 0) next.RemoveAt(idx);
                        }
                        msg.Reactions = [.. next];
                    }
                }
                Notify();
            }
            return;
        }
        if (string.IsNullOrEmpty(_token)) return;
        try
        {
            string enc = Uri.EscapeDataString(emoji);
            using var resp = await SendRestAsync(
                () => new HttpRequestMessage(HttpMethod.Delete, $"https://discord.com/api/v10/channels/{channelId}/messages/{messageId}/reactions/{enc}/@me"),
                "RemoveReaction");
        }
        catch { }
    }

    public async Task LoadPinnedMessagesAsync(string channelId)
    {
        if (string.IsNullOrEmpty(channelId)) return;
        if (IsDemoMode)
        {
            if (Messages.TryGetValue(channelId, out var list))
            {
                lock (list) PinnedMessages[channelId] = list.Take(2).ToList();
                Notify();
            }
            return;
        }
        if (string.IsNullOrEmpty(_token)) return;
        try
        {
            using var resp = await SendRestAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"https://discord.com/api/v10/channels/{channelId}/pins"),
                "LoadPins");
            if (resp == null || !resp.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
            var pins = new List<DiscordMessage>();
            foreach (var el in doc.RootElement.EnumerateArray()) pins.Add(ParseMessage(el));
            PinnedMessages[channelId] = pins;
            Notify();
        }
        catch { }
    }

    public async Task SetPresenceStatusAsync(string status)
    {
        status = status is "online" or "idle" or "dnd" or "invisible" ? status : "online";
        Me.Status = status;
        SettingsStore.Current.PresenceStatus = status;
        SettingsStore.Save();
        Notify();
        if (!IsDemoMode && _gw?.State == WebSocketState.Open)
        {
            try
            {
                string json = $"{{\"op\":3,\"d\":{{\"since\":null,\"activities\":[],\"status\":\"{status}\",\"afk\":false}}}}";
                await SendWsTextAsync(_gw, json, CancellationToken.None);
            }
            catch { }
        }
    }

    private long _lastTypingSentTick;
    public async Task SendTypingAsync(string channelId)
    {
        if (IsDemoMode || string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(channelId)) return;
        long now = Environment.TickCount64;
        if (now - _lastTypingSentTick < 7000) return;
        _lastTypingSentTick = now;
        try
        {
            using var resp = await SendRestAsync(
                () => new HttpRequestMessage(HttpMethod.Post, $"https://discord.com/api/v10/channels/{channelId}/typing"),
                "Typing");
        }
        catch { }
    }

    public async Task<string?> OpenOrCreateDmAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        DiscordChannel? existing;
        lock (DmChannels) existing = DmChannels.FirstOrDefault(c => c.RecipientId == userId);
        if (existing != null)
        {
            ActiveGuildId = "@me"; ActiveChannelId = existing.Id;
            _ = LoadMessagesAsync(existing.Id);
            Notify();
            return existing.Id;
        }
        if (IsDemoMode)
        {
            string name = Users.TryGetValue(userId, out var uo) ? uo.DisplayName : "";
            if (string.IsNullOrEmpty(name)) lock (Friends) name = Friends.FirstOrDefault(f => f.UserId == userId)?.DisplayName ?? "Друг";
            var dm = new DiscordChannel { Id = "dm_" + userId, GuildId = "@me", Name = name, RecipientId = userId, Type = 1 };
            lock (DmChannels) DmChannels.Insert(0, dm);
            ActiveGuildId = "@me"; ActiveChannelId = dm.Id;
            Notify();
            return dm.Id;
        }
        if (string.IsNullOrEmpty(_token)) return null;
        try
        {
            using var resp = await SendRestAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Post, "https://discord.com/api/v10/users/@me/channels");
                r.Content = new StringContent($"{{\"recipient_id\":\"{userId}\"}}", Encoding.UTF8, "application/json");
                return r;
            }, "OpenDM");
            if (resp == null || !resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
            var cEl = doc.RootElement;
            string cid = cEl.GetProperty("id").GetString() ?? "";
            if (string.IsNullOrEmpty(cid)) return null;
            string name = Users.TryGetValue(userId, out var u2) ? u2.DisplayName : "";
            string av = Users.TryGetValue(userId, out var u3) ? u3.Avatar : "";
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(av))
            {
                lock (Friends)
                {
                    var fr = Friends.FirstOrDefault(f => f.UserId == userId);
                    if (string.IsNullOrEmpty(name)) name = fr?.DisplayName ?? "DM";
                    if (string.IsNullOrEmpty(av)) av = fr?.Avatar ?? "";
                }
            }
            var ch = new DiscordChannel { Id = cid, GuildId = "@me", Name = name, RecipientId = userId, RecipientAvatar = av, Type = 1 };
            lock (DmChannels) DmChannels.Insert(0, ch);
            ActiveGuildId = "@me"; ActiveChannelId = cid;
            _ = LoadMessagesAsync(cid);
            Notify();
            return cid;
        }
        catch { return null; }
    }

    public async Task<string?> CreateInviteUrlAsync(string channelId)
    {
        if (string.IsNullOrEmpty(channelId)) return null;
        if (IsDemoMode) return "https://discord.gg/fluxcord-demo";
        if (string.IsNullOrEmpty(_token)) return null;
        try
        {
            using var resp = await SendRestAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Post, $"https://discord.com/api/v10/channels/{channelId}/invites");
                r.Content = new StringContent("{\"max_age\":86400,\"max_uses\":0,\"temporary\":false}", Encoding.UTF8, "application/json");
                return r;
            }, "CreateInvite");
            if (resp == null || !resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
            string code = doc.RootElement.TryGetProperty("code", out var ce) ? ce.GetString() ?? "" : "";
            return string.IsNullOrEmpty(code) ? null : $"https://discord.gg/{code}";
        }
        catch { return null; }
    }

    public void Logout()
    {
        Disconnect();
        SettingsStore.SetToken("");
        _token = "";
        lock (Guilds) Guilds.Clear();
        lock (DmChannels) DmChannels.Clear();
        lock (Friends) Friends.Clear();
        Messages.Clear();
        PinnedMessages.Clear();
        Users.Clear();
        GuildNicks.Clear();
        GuildMemberIds.Clear();
        UnreadChannels.Clear();
        _reachedChannelStart.Clear();
        _fetchingMembers.Clear();
        ActiveGuildId = "@me";
        ActiveChannelId = "";
        StatusText = "Вышли из аккаунта";
        App.InvokeOnUiThread(App.ClearTokenInput);
        RemoteAuthQr.Instance.Start();
        Notify();
    }

    // ---------------- RTC Region Turbo Benchmarker & Live Region Hopper ----------------

    public async Task BenchmarkRegionsAsync()
    {
        if (IsBenchmarkingRegions) return;
        IsBenchmarkingRegions = true;
        Notify();
        try
        {
            var probed = new Dictionary<string, RtcRegionInfo>(StringComparer.OrdinalIgnoreCase);

            // 1. Query Discord's official RTC latency server list & probe live TCP:443 RTT in parallel!
            try
            {
                using var resp = await _http.GetAsync("https://latency.discord.media/rtc");
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
                    var tasks = new List<Task<(string Reg, int Ms)>>();
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        string reg = el.TryGetProperty("region", out var rEl) ? rEl.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(reg)) continue;
                        var ips = new List<string>();
                        if (el.TryGetProperty("ips", out var ipsEl) && ipsEl.ValueKind == JsonValueKind.Array)
                            foreach (var ip in ipsEl.EnumerateArray())
                                if (ip.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ip.GetString())) ips.Add(ip.GetString()!);
                        if (ips.Count == 0) continue;
                        tasks.Add(ProbeRegionTcpAsync(reg, ips));
                    }
                    var results = await Task.WhenAll(tasks);
                    foreach (var (reg, ms) in results)
                    {
                        probed[reg] = new RtcRegionInfo
                        {
                            Id = reg,
                            Name = FormatRegionName(reg),
                            PingMs = ms,
                            Optimal = ms > 0 && ms < 110,
                            IsOfficialDiscordPing = ms > 0
                        };
                    }
                }
            }
            catch { }

            // 2. Also add official Discord selectable voice regions (GET /api/v10/voice/regions) so user can hop anywhere!
            if (!IsDemoMode && !string.IsNullOrEmpty(_token))
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/voice/regions");
                    req.Headers.TryAddWithoutValidation("Authorization", _token);
                    using var resp = await _http.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            string id = el.TryGetProperty("id", out var ie) ? ie.GetString() ?? "" : "";
                            string nm = el.TryGetProperty("name", out var ne) ? ne.GetString() ?? id : id;
                            bool dep = el.TryGetProperty("deprecated", out var de) && de.ValueKind == JsonValueKind.True;
                            bool opt = el.TryGetProperty("optimal", out var oe) && oe.ValueKind == JsonValueKind.True;
                            if (string.IsNullOrEmpty(id) || dep) continue;
                            if (probed.TryGetValue(id, out var ex)) { ex.Name = nm; ex.Optimal |= opt; }
                            else probed[id] = new RtcRegionInfo { Id = id, Name = nm, PingMs = -1, Optimal = opt };
                        }
                    }
                }
                catch { }
            }

            // City-level fallback RTT hints for UI display ONLY (NEVER used to override official Discord RTC BestRtcRegion; no fake ya.ru anchor!)
            (string Id, string Name, string ProbeHost)[] cityAnchors =
            [
                ("rotterdam", "Rotterdam (EU West)", "ams-nl-ping.vultr.com"),
                ("frankfurt", "Frankfurt (EU Central)", "fra-de-ping.vultr.com"),
                ("warsaw", "Warsaw (EU East)", "waw-pl-ping.vultr.com"),
                ("stockholm", "Stockholm (EU North)", "sto-se-ping.vultr.com"),
                ("milan", "Milan (EU South)", "mil-it-ping.vultr.com"),
                ("madrid", "Madrid (EU South)", "mad-es-ping.vultr.com"),
                ("singapore", "Singapore", "sgp-ping.vultr.com"),
                ("japan", "Japan (Tokyo)", "hnd-jp-ping.vultr.com"),
                ("us-east", "US East", "nj-us-ping.vultr.com"),
                ("us-central", "US Central", "il-us-ping.vultr.com"),
                ("us-west", "US West", "lax-ca-ping.vultr.com")
            ];
            var anchorTasks = new List<Task<(string Id, string Name, int Ms)>>();
            foreach (var (id, nm, host) in cityAnchors)
            {
                if (!probed.ContainsKey(id)) probed[id] = new RtcRegionInfo { Id = id, Name = nm, PingMs = -1 };
                if (probed[id].PingMs <= 0) anchorTasks.Add(ProbeHostTcpAsync(id, nm, host));
            }
            if (anchorTasks.Count > 0)
            {
                foreach (var (id, _, ms) in await Task.WhenAll(anchorTasks))
                {
                    if (ms > 0 && probed.TryGetValue(id, out var info) && info.PingMs <= 0)
                        info.PingMs = ms;
                }
            }

            // Preserve real live measured Discord ping for currently connected voice host!
            string? curMapped = MapHostToRegion(VoiceEndpointHost);
            if (!string.IsNullOrEmpty(curMapped) && VoicePingMs > 0 && probed.TryGetValue(curMapped, out var curR))
            {
                curR.PingMs = VoicePingMs;
                curR.IsOfficialDiscordPing = true;
            }

            var ordered = probed.Values
                .OrderBy(r => r.IsOfficialDiscordPing && r.PingMs > 0 ? 0 : (r.PingMs > 0 ? 1 : (r.Optimal ? 2 : 3)))
                .ThenBy(r => r.PingMs > 0 ? r.PingMs : 9999)
                .ThenBy(r => r.Name)
                .ToList();

            lock (Regions)
            {
                Regions.Clear();
                Regions.AddRange(ordered);
            }

            // Pick BestRtcRegion STRICTLY from official Discord media servers (latency.discord.media/rtc or live VoiceGW RTT)!
            var fastest = ordered.FirstOrDefault(r => r.IsOfficialDiscordPing && r.PingMs > 0)
                          ?? ordered.FirstOrDefault(r => r.Optimal && r.PingMs > 0)
                          ?? ordered.FirstOrDefault(r => r.PingMs > 0);
            if (fastest != null)
            {
                BestRtcRegion = fastest.Id;
                SettingsStore.Log($"RTC Turbo benchmark fastest={fastest.Id} ({fastest.PingMs}ms, official={fastest.IsOfficialDiscordPing}) from {ordered.Count} regions");
            }
        }
        catch (Exception ex) { SettingsStore.Log("BenchmarkRegions error: " + ex.Message); }
        finally { IsBenchmarkingRegions = false; Notify(); }
    }

    private static string FormatRegionName(string id) => id switch
    {
        "helsinki" => "Helsinki (EU North)",
        "finland" => "Finland (EU North)",
        "rotterdam" => "Rotterdam (EU West)",
        "frankfurt" => "Frankfurt (EU Central)",
        "warsaw" => "Warsaw (EU East)",
        "stockholm" => "Stockholm (EU North)",
        "milan" => "Milan (EU South)",
        "russia" => "Russia",
        "india" => "India (Mumbai)",
        "dubai" => "Dubai (Middle East)",
        "tel-aviv" => "Tel Aviv",
        "singapore" => "Singapore",
        _ => char.ToUpperInvariant(id[0]) + id[1..]
    };

    private static async Task<(string Reg, int Ms)> ProbeRegionTcpAsync(string region, List<string> ips)
    {
        int best = -1;
        foreach (var ip in ips.Take(2))
        {
            using var sock = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            using var cts = new CancellationTokenSource(650);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await sock.ConnectAsync(ip, 443, cts.Token);
                int ms = (int)Math.Round((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                if (ms > 0 && (best < 0 || ms < best)) best = ms;
            }
            catch { }
        }
        return (region, best);
    }

    private static async Task<(string Id, string Name, int Ms)> ProbeHostTcpAsync(string id, string name, string host)
    {
        using var sock = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        using var cts = new CancellationTokenSource(650);
        try
        {
            var addrs = await System.Net.Dns.GetHostAddressesAsync(host, cts.Token);
            var ip = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (ip == null) return (id, name, -1);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            await sock.ConnectAsync(ip, 443, cts.Token);
            int ms = (int)Math.Round((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            return (id, name, ms > 0 ? ms : 1);
        }
        catch { return (id, name, -1); }
    }

    public async Task<(bool Ok, string Message)> SwitchVoiceRegionAsync(string? regionId)
    {
        string normalized = string.IsNullOrWhiteSpace(regionId) ? "" : regionId.Trim().ToLowerInvariant();
        SettingsStore.Current.PreferredRtcRegion = normalized;
        SettingsStore.Save();
        Notify();

        if (IsDemoMode || string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(_voiceChannelId))
            return (true, string.IsNullOrEmpty(normalized) ? "Регион Go Live: Авто (быстрейший)" : $"Регион Go Live: {normalized}");

        string?[] candidates = string.IsNullOrEmpty(normalized)
            ? [null]
            : (normalized is "helsinki" or "finland" ? [normalized, "stockholm", "rotterdam"] : [normalized]);

        try
        {
            foreach (var cand in candidates)
            {
                string body = cand == null ? "{\"rtc_region\":null}" : $"{{\"rtc_region\":\"{cand}\"}}";
                using var req = new HttpRequestMessage(HttpMethod.Patch, $"https://discord.com/api/v10/channels/{_voiceChannelId}");
                req.Headers.TryAddWithoutValidation("Authorization", _token);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    SettingsStore.Log($"Switched voice channel {_voiceChannelId} rtc_region -> {cand ?? "auto"}");
                    return (true, $"Канал переключён на сервер: {cand ?? "Авто"} ⚡");
                }
                if ((int)resp.StatusCode == 403)
                    return (false, $"Go Live регион: {normalized} (смена региона канала требует прав модератора)");
            }
            return (false, $"Go Live регион сохранён: {normalized}");
        }
        catch (Exception ex) { return (false, "Ошибка смены региона: " + ex.Message); }
    }

    public async Task UploadFileAsync(string channelId, string filePath, string caption = "")
    {
        if (IsDemoMode || string.IsNullOrEmpty(_token) || !File.Exists(filePath)) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            string fileName = Path.GetFileName(filePath);
            byte[]? payloadBytes = null;
            if (!string.IsNullOrWhiteSpace(caption))
            {
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteString("content", caption);
                    w.WriteEndObject();
                }
                payloadBytes = ms.ToArray();
            }
            using var resp = await SendRestAsync(() =>
            {
                var form = new MultipartFormDataContent();
                if (payloadBytes != null)
                {
                    var jc = new ByteArrayContent(payloadBytes);
                    jc.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    form.Add(jc, "payload_json");
                }
                form.Add(new ByteArrayContent(bytes), "files[0]", fileName);
                var req = new HttpRequestMessage(HttpMethod.Post, $"https://discord.com/api/v10/channels/{channelId}/messages");
                req.Content = form;
                return req;
            }, "UploadFile");
        }
        catch (Exception ex) { SettingsStore.Log("UploadFile error: " + ex.Message); }
    }

    public float GetUserVolume(string userId) => _userVolumes.GetValueOrDefault(userId, 1.0f);

    public void SetUserVolume(string userId, float vol)
    {
        vol = Math.Clamp(vol, 0f, 2f);
        _userVolumes[userId] = vol;
        if (_userToSsrc.TryGetValue(userId, out var ssrc))
            MediaNative.fc_voice_set_user_volume(ssrc, vol);
        Notify();
    }

    // ---------------- Voice Gateway v8 + Native WASAPI / UDP Transport ----------------

    public async Task UpdateVoiceSelfStateAsync(bool selfMute, bool selfDeaf)
    {
        bool effMute = selfMute || selfDeaf;
        if (!string.IsNullOrEmpty(_voiceGuildId))
        {
            DiscordGuild? g;
            lock (Guilds) g = Guilds.FirstOrDefault(x => x.Id == _voiceGuildId);
            if (g != null)
            {
                lock (g.VoiceStates)
                {
                    if (g.VoiceStates.TryGetValue(Me.Id, out var vm))
                    {
                        vm.SelfMute = effMute;
                        vm.SelfDeaf = selfDeaf;
                    }
                }
            }
        }
        if (IsDemoMode || _gw?.State != WebSocketState.Open || string.IsNullOrEmpty(_voiceGuildId) || string.IsNullOrEmpty(_voiceChannelId))
            return;
        try
        {
            string sm = effMute ? "true" : "false";
            string sd = selfDeaf ? "true" : "false";
            string json = $"{{\"op\":4,\"d\":{{\"guild_id\":\"{_voiceGuildId}\",\"channel_id\":\"{_voiceChannelId}\",\"self_mute\":{sm},\"self_deaf\":{sd}}}}}";
            await SendWsTextAsync(_gw, json, CancellationToken.None);
        }
        catch { }
    }

    private void TearDownVoiceLocalOnly()
    {
        StopAllWatchedStreams();
        StopPublishingGoLive();
        try { _voiceCts?.Cancel(); } catch { }
        try { _voiceCts?.Dispose(); } catch { }
        _voiceCts = null;
        try { _voiceGw?.Dispose(); } catch { }
        _voiceGw = null;
        MediaNative.fc_transport_disconnect();
        MediaNative.fc_voice_stop();
        _voiceSessionId = "";
        _voiceToken = "";
        _voiceEndpoint = "";
        _voiceGwStarted = false;
        _voiceSeq = -1;
        _voiceDaveTransitions.Clear();
        _userToSsrc.Clear();
        VoiceConnected = false;
        VoiceStatus = "";
        VoiceEndpointHost = "";
        VoicePingMs = 0;
        VoicePingAvgMs = 0;
        lock (VoicePingHistory) VoicePingHistory.Clear();
        LocalSpeaking = false;
        LocalVoiceLevel = 0f;
        _lastSentVoiceSpeaking = false;
        FocusedStreamUserId = null;
    }

    public async Task JoinVoiceAsync(string guildId, string channelId, bool selfMute = false, bool selfDeaf = false)
    {
        // When moving directly between voice channels in the SAME guild, do NOT send OP 4 channel_id:null first
        // (that creates a race where Discord echoes null back and tears down the new channel!).
        bool sameGuildSwitch = !string.IsNullOrEmpty(_voiceGuildId) && _voiceGuildId == guildId && !string.IsNullOrEmpty(_voiceChannelId);
        if (sameGuildSwitch)
            TearDownVoiceLocalOnly();
        else
            LeaveVoice();

        _voiceGuildId = guildId;
        _voiceChannelId = channelId;

        var s = SettingsStore.Current;
        bool effMute = selfMute || selfDeaf || s.PushToTalk;
        MediaNative.fc_voice_set_noise_config(s.NoiseMode, s.NoiseStrength, s.HpfEnabled ? 1 : 0, s.AgcEnabled ? 1 : 0, s.GateDb);
        MediaNative.fc_voice_set_input_gain(s.InputGain);
        MediaNative.fc_voice_set_output_volume(s.OutputVolume);
        MediaNative.fc_voice_set_muted(effMute ? 1 : 0);
        MediaNative.fc_voice_set_deafened(selfDeaf ? 1 : 0);
        MediaNative.fc_voice_start(s.CaptureDeviceId, s.RenderDeviceId);

        if (IsDemoMode)
        {
            VoiceConnected = true;
            VoiceStatus = "Голосовая связь подключена";
            VoiceEndpointHost = "c-ams13-turbo";
            RecordVoicePing(28);
            SoundFx.Play(UiSound.VoiceJoin);
            Notify();
            return;
        }

        VoiceStatus = "Подключение к голосу...";
        Notify();
        if (_gw?.State == WebSocketState.Open)
        {
            string sm = (selfMute || selfDeaf) ? "true" : "false";
            string sd = selfDeaf ? "true" : "false";
            string json = $"{{\"op\":4,\"d\":{{\"guild_id\":\"{guildId}\",\"channel_id\":\"{channelId}\",\"self_mute\":{sm},\"self_deaf\":{sd}}}}}";
            await SendWsTextAsync(_gw, json, CancellationToken.None);
        }
    }

    public void LeaveVoice()
    {
        bool wasConnected = VoiceConnected || !string.IsNullOrEmpty(_voiceChannelId);
        string prevGuild = _voiceGuildId;
        if (wasConnected) SoundFx.Play(UiSound.VoiceLeave);

        if (!IsDemoMode && _gw?.State == WebSocketState.Open && !string.IsNullOrEmpty(prevGuild))
        {
            string json = $"{{\"op\":4,\"d\":{{\"guild_id\":\"{prevGuild}\",\"channel_id\":null,\"self_mute\":false,\"self_deaf\":false}}}}";
            _ = SendWsTextAsync(_gw, json, CancellationToken.None);
        }
        TearDownVoiceLocalOnly();
        _voiceGuildId = "";
        _voiceChannelId = "";
        Notify();
    }

    private void ParseReadySupplemental(JsonElement d)
    {
        if (d.TryGetProperty("guilds", out var gs) && gs.ValueKind == JsonValueKind.Array)
        {
            foreach (var gEl in gs.EnumerateArray())
            {
                string gid = gEl.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                DiscordGuild? g;
                lock (Guilds) g = Guilds.FirstOrDefault(x => x.Id == gid);
                if (g == null) continue;
                if (gEl.TryGetProperty("voice_states", out var vss) && vss.ValueKind == JsonValueKind.Array)
                    foreach (var vs in vss.EnumerateArray()) UpsertVoiceState(g, vs, emitSounds: false, allowFetch: false);
            }
            Notify();
        }
    }

    private void UpsertVoiceState(DiscordGuild g, JsonElement vs, bool emitSounds = false, bool allowFetch = true)
    {
        string vuid = vs.TryGetProperty("user_id", out var vu) ? vu.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(vuid)) return;

        string prevCid = "";
        lock (g.VoiceStates)
        {
            if (g.VoiceStates.TryGetValue(vuid, out var existingVm)) prevCid = existingVm.ChannelId;
        }

        string vcid = vs.TryGetProperty("channel_id", out var vc) && vc.ValueKind == JsonValueKind.String ? vc.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(vcid))
        {
            lock (g.VoiceStates) g.VoiceStates.Remove(vuid);
            if (IsWatchingStream(vuid)) StopWatchingStream(vuid);
            if (emitSounds && VoiceConnected && vuid != Me.Id && prevCid == _voiceChannelId && !string.IsNullOrEmpty(_voiceChannelId))
                SoundFx.Play(UiSound.UserLeave);
            return;
        }
        if (emitSounds && VoiceConnected && vuid != Me.Id && !string.IsNullOrEmpty(_voiceChannelId))
        {
            if (prevCid != _voiceChannelId && vcid == _voiceChannelId) SoundFx.Play(UiSound.UserJoin);
            else if (prevCid == _voiceChannelId && vcid != _voiceChannelId) SoundFx.Play(UiSound.UserLeave);
        }

        string uname = "";
        string uav = "";
        if (vs.TryGetProperty("member", out var mem) && mem.ValueKind == JsonValueKind.Object)
        {
            if (mem.TryGetProperty("nick", out var nk) && nk.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(nk.GetString()))
            {
                uname = nk.GetString()!;
                GuildNicks[g.Id + ":" + vuid] = uname;
            }
            if (mem.TryGetProperty("user", out var mu) && mu.ValueKind == JsonValueKind.Object)
            {
                string rawUn = mu.TryGetProperty("username", out var u2) ? u2.GetString() ?? "" : "";
                string globalUn = mu.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(gn.GetString())
                    ? gn.GetString()! : rawUn;
                if (string.IsNullOrEmpty(uname)) uname = globalUn;
                if (mu.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.String) uav = av.GetString()!;
                string prevStatus = Users.TryGetValue(vuid, out var prevU) ? prevU.Status : "offline";
                Users[vuid] = new DiscordUser { Id = vuid, Username = rawUn, DisplayName = globalUn, Avatar = uav, Status = prevStatus };
            }
        }
        if (string.IsNullOrEmpty(uname) && GuildNicks.TryGetValue(g.Id + ":" + vuid, out var gn2))
            uname = gn2;
        if (Users.TryGetValue(vuid, out var cached))
        {
            if (string.IsNullOrEmpty(uname)) uname = cached.DisplayName;
            if (string.IsNullOrEmpty(uav)) uav = cached.Avatar;
        }
        bool missingName = string.IsNullOrEmpty(uname);
        if (missingName)
        {
            uname = vuid == Me.Id ? Me.DisplayName : ("User " + (vuid.Length >= 4 ? vuid[^4..] : vuid));
            if (vuid == Me.Id && string.IsNullOrEmpty(uav)) uav = Me.Avatar;
        }
        if (allowFetch && vuid != Me.Id && (missingName || g.Id == _voiceGuildId || g.Id == ActiveGuildId) && !GuildNicks.ContainsKey(g.Id + ":" + vuid))
            _ = FetchGuildMemberAsync(g.Id, vuid);

        bool selfStream = vs.TryGetProperty("self_stream", out var ss) && ss.ValueKind == JsonValueKind.True;
        if (!selfStream && IsWatchingStream(vuid)) StopWatchingStream(vuid);

        lock (g.VoiceStates)
        {
            bool prevSpk = g.VoiceStates.TryGetValue(vuid, out var oldVm) && oldVm.Speaking;
            long prevUntil = oldVm?.SpeakingUntil ?? 0;
            g.VoiceStates[vuid] = new VoiceMember
            {
                UserId = vuid,
                ChannelId = vcid,
                Username = uname,
                Avatar = uav,
                SelfMute = vs.TryGetProperty("self_mute", out var sm) && sm.ValueKind == JsonValueKind.True,
                SelfDeaf = vs.TryGetProperty("self_deaf", out var sd) && sd.ValueKind == JsonValueKind.True,
                SelfStream = selfStream,
                Speaking = prevSpk,
                SpeakingUntil = prevUntil,
            };
        }
    }

    private readonly ConcurrentDictionary<string, byte> _fetchingMembers = new();
    private async Task FetchGuildMemberAsync(string guildId, string userId)
    {
        string key = guildId + ":" + userId;
        if (IsDemoMode || string.IsNullOrEmpty(_token) || !_fetchingMembers.TryAdd(key, 1)) return;
        try
        {
            using var resp = await SendRestAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"https://discord.com/api/v10/guilds/{guildId}/members/{userId}"),
                "FetchMember");
            if (resp == null || !resp.IsSuccessStatusCode) return;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
            var m = doc.RootElement;
            string nick = m.TryGetProperty("nick", out var nk) && nk.ValueKind == JsonValueKind.String ? nk.GetString() ?? "" : "";
            string rawUn = "", globalUn = "", av = "";
            if (m.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                rawUn = u.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "";
                globalUn = u.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(gn.GetString())
                    ? gn.GetString()! : rawUn;
                av = u.TryGetProperty("avatar", out var ave) && ave.ValueKind == JsonValueKind.String ? ave.GetString() ?? "" : "";
            }
            string bestName = !string.IsNullOrEmpty(nick) ? nick : globalUn;
            if (string.IsNullOrEmpty(bestName)) return;

            GuildNicks[key] = bestName;
            string prevStatus = Users.TryGetValue(userId, out var prevU) ? prevU.Status : "offline";
            Users[userId] = new DiscordUser
            {
                Id = userId,
                Username = string.IsNullOrEmpty(rawUn) ? (prevU?.Username ?? "") : rawUn,
                DisplayName = string.IsNullOrEmpty(globalUn) ? (prevU?.DisplayName ?? bestName) : globalUn,
                Avatar = string.IsNullOrEmpty(av) ? (prevU?.Avatar ?? "") : av,
                Status = prevStatus
            };
            DiscordGuild? g;
            lock (Guilds) g = Guilds.FirstOrDefault(x => x.Id == guildId);
            if (g != null)
            {
                lock (g.VoiceStates)
                {
                    if (g.VoiceStates.TryGetValue(userId, out var vm))
                    {
                        vm.Username = bestName;
                        if (!string.IsNullOrEmpty(av)) vm.Avatar = av;
                    }
                }
            }
            lock (_watchLock)
            {
                if (_watches.TryGetValue(userId, out var ws)) ws.StreamerName = bestName;
            }
            Notify();
        }
        catch { }
    }

    private void ParseVoiceStateUpdate(JsonElement d)
    {
        string gid = d.TryGetProperty("guild_id", out var gEl) && gEl.ValueKind == JsonValueKind.String ? gEl.GetString() ?? "" : "";
        string vcid = d.TryGetProperty("channel_id", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() ?? "" : "";
        if (!string.IsNullOrEmpty(gid))
        {
            DiscordGuild? g;
            lock (Guilds) g = Guilds.FirstOrDefault(x => x.Id == gid);
            if (g != null) { UpsertVoiceState(g, d, emitSounds: true); Notify(); }
        }
        string uid = d.TryGetProperty("user_id", out var u) ? u.GetString() ?? "" : "";
        if (uid == Me.Id)
        {
            if (string.IsNullOrEmpty(vcid))
            {
                // Disconnected from voice (by us, moderator, or another session) -> full clean stop!
                TearDownVoiceLocalOnly();
                _voiceChannelId = "";
                _voiceGuildId = "";
                App.InvokeOnUiThread(Shell.StopStream);
                Notify();
                return;
            }
            _voiceChannelId = vcid;
            if (!string.IsNullOrEmpty(gid)) _voiceGuildId = gid;
            _voiceSessionId = d.TryGetProperty("session_id", out var sid) ? sid.GetString() ?? "" : "";
            TryStartVoiceGateway();
        }
    }

    private void ParseVoiceServerUpdate(JsonElement d)
    {
        string newTok = d.TryGetProperty("token", out var tk) ? tk.GetString() ?? "" : "";
        string newEp = d.TryGetProperty("endpoint", out var ep) && ep.ValueKind == JsonValueKind.String ? ep.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(newTok) || string.IsNullOrEmpty(newEp)) return;
        // Seamless HOT RTC region/server migration when channel rtc_region changes!
        if (_voiceGwStarted && (newEp != _voiceEndpoint || newTok != _voiceToken))
        {
            SettingsStore.Log($"VoiceGW hot-migrating server: {_voiceEndpoint} -> {newEp}");
            try { _voiceCts?.Cancel(); } catch { }
            try { _voiceGw?.Dispose(); } catch { }
            _voiceGw = null;
            MediaNative.fc_transport_disconnect();
            _voiceGwStarted = false;
        }
        _voiceToken = newTok;
        _voiceEndpoint = newEp;
        TryStartVoiceGateway();
    }

    private static string SelectSupportedVoiceCipher(JsonElement data)
    {
        string? xsalsaFallback = null;
        if (data.TryGetProperty("modes", out var modesEl) && modesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in modesEl.EnumerateArray())
            {
                string mode = m.GetString() ?? "";
                if (mode == "aead_xchacha20_poly1305_rtpsize") return mode;
                if (xsalsaFallback == null && mode.StartsWith("xsalsa20_poly1305", StringComparison.OrdinalIgnoreCase))
                    xsalsaFallback = mode;
            }
        }
        return xsalsaFallback ?? "aead_xchacha20_poly1305_rtpsize";
    }

    private void SeedVoiceDaveUsers()
    {
        DiscordGuild? g;
        lock (Guilds) g = Guilds.FirstOrDefault(x => x.Id == _voiceGuildId);
        if (g != null)
        {
            lock (g.VoiceStates)
            {
                foreach (var vm in g.VoiceStates.Values)
                    if (vm.ChannelId == _voiceChannelId) MediaNative.fc_dave_add_user(vm.UserId);
            }
        }
        foreach (var kv in _userToSsrc)
        {
            MediaNative.fc_dave_add_user(kv.Key);
            MediaNative.fc_dave_map_ssrc(kv.Value, kv.Key);
        }
    }

    private void SeedPubDaveUsers()
    {
        MediaNative.fc_pub_dave_add_user(Me.Id);
        DiscordGuild? g;
        lock (Guilds) g = Guilds.FirstOrDefault(x => x.Id == _voiceGuildId);
        if (g != null)
        {
            lock (g.VoiceStates)
            {
                foreach (var vm in g.VoiceStates.Values)
                    if (vm.ChannelId == _voiceChannelId) MediaNative.fc_pub_dave_add_user(vm.UserId);
            }
        }
    }

    private void SeedWatchDaveUsers(WatchSession ws)
    {
        MediaNative.fc_watch_dave_add_user_slot(ws.Slot, ws.StreamerUserId);
        MediaNative.fc_watch_dave_add_user_slot(ws.Slot, Me.Id);
        DiscordGuild? g;
        lock (Guilds) g = Guilds.FirstOrDefault(x => x.Id == _voiceGuildId);
        if (g != null)
        {
            lock (g.VoiceStates)
            {
                foreach (var vm in g.VoiceStates.Values)
                    if (vm.ChannelId == _voiceChannelId) MediaNative.fc_watch_dave_add_user_slot(ws.Slot, vm.UserId);
            }
        }
    }

    private void TryStartVoiceGateway()
    {
        if (_voiceGwStarted || string.IsNullOrEmpty(_voiceSessionId) || string.IsNullOrEmpty(_voiceToken) || string.IsNullOrEmpty(_voiceEndpoint)) return;
        _voiceGwStarted = true;
        _voiceSeq = -1;
        _voiceDaveTransitions.Clear();
        try { _voiceCts?.Cancel(); } catch { }
        try { _voiceCts?.Dispose(); } catch { }
        var localCts = new CancellationTokenSource();
        _voiceCts = localCts;
        var ct = localCts.Token;

        _ = Task.Run(async () =>
        {
            using var gw = new ClientWebSocket();
            _voiceGw = gw;
            bool hbAcked = true;
            try
            {
                await gw.ConnectAsync(new Uri($"wss://{_voiceEndpoint}/?v=8"), ct);
                string identify = $"{{\"op\":0,\"d\":{{\"server_id\":\"{_voiceGuildId}\",\"user_id\":\"{Me.Id}\",\"session_id\":\"{_voiceSessionId}\",\"token\":\"{_voiceToken}\",\"max_dave_protocol_version\":1}}}}";
                await SendWsTextAsync(gw, identify, ct);
                SettingsStore.Log($"VoiceGW connecting to {_voiceEndpoint} (DAVE v1)");

                var buf = new byte[65536];
                using var ms = new MemoryStream();
                while (!ct.IsCancellationRequested && gw.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await gw.ReceiveAsync(buf, ct);
                        if (res.MessageType == WebSocketMessageType.Close)
                        {
                            int code = (int?)res.CloseStatus ?? 0;
                            SettingsStore.Log($"VoiceGW closed: {code} {res.CloseStatusDescription}");
                            if (ReferenceEquals(_voiceGw, gw))
                            {
                                VoiceStatus = $"Голос отключён ({code})";
                                VoiceConnected = false;
                                _voiceGwStarted = false;
                                if (code is 4006 or 4014) { _voiceToken = ""; _voiceSessionId = ""; }
                                Notify();
                                if (!ct.IsCancellationRequested && code is 1006 or 4015 && !string.IsNullOrEmpty(_voiceChannelId) && !string.IsNullOrEmpty(_voiceToken))
                                {
                                    await Task.Delay(1500, ct);
                                    TryStartVoiceGateway();
                                }
                            }
                            return;
                        }
                        ms.Write(buf, 0, res.Count);
                    } while (!res.EndOfMessage);

                    if (res.MessageType == WebSocketMessageType.Binary)
                    {
                        await HandleVoiceBinaryAsync(gw, ms.GetBuffer(), (int)ms.Length, ct);
                        continue;
                    }

                    using var doc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("seq", out var seqEl) && seqEl.ValueKind == JsonValueKind.Number) _voiceSeq = seqEl.GetInt32();
                    int op = root.GetProperty("op").GetInt32();
                    var data = root.TryGetProperty("d", out var dEl) ? dEl : default;

                    if (op == 8)
                    {
                        int hb = Math.Clamp((int)data.GetProperty("heartbeat_interval").GetDouble(), 2000, 60000);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                while (!ct.IsCancellationRequested && gw.State == WebSocketState.Open)
                                {
                                    if (!hbAcked)
                                    {
                                        SettingsStore.Log("VoiceGW heartbeat ACK missed -> aborting zombie socket");
                                        gw.Abort();
                                        return;
                                    }
                                    hbAcked = false;
                                    _lastVoiceHbSent = Environment.TickCount64;
                                    await SendWsTextAsync(gw, $"{{\"op\":3,\"d\":{{\"t\":{_lastVoiceHbSent},\"seq_ack\":{_voiceSeq}}}}}", ct);
                                    await Task.Delay(hb, ct);
                                }
                            }
                            catch { }
                        }, ct);
                    }
                    else if (op == 6) // HEARTBEAT_ACK -> exact live Voice RTT for Discord Connection Graph!
                    {
                        hbAcked = true;
                        if (_lastVoiceHbSent > 0)
                        {
                            int prev = VoicePingMs;
                            int rtt = (int)(Environment.TickCount64 - _lastVoiceHbSent);
                            RecordVoicePing(rtt);
                            if (prev == 0 || Math.Abs(VoicePingMs - prev) >= 3) Notify();
                        }
                    }
                    else if (op == 2)
                    {
                        _voiceSsrc = data.GetProperty("ssrc").GetUInt32();
                        string ip = data.GetProperty("ip").GetString() ?? "";
                        ushort port = (ushort)data.GetProperty("port").GetInt32();
                        string chosenMode = SelectSupportedVoiceCipher(data);

                        if (DiscoverUdpEndpoint(ip, port, _voiceSsrc, out string extIp, out ushort extPort) &&
                            System.Net.IPAddress.TryParse(extIp, out _))
                        {
                            SettingsStore.Log($"Voice UDP discovered: {extIp}:{extPort} -> {ip}:{port} mode={chosenMode}");
                            string sel = $"{{\"op\":1,\"d\":{{\"protocol\":\"udp\",\"data\":{{\"address\":\"{extIp}\",\"port\":{extPort},\"mode\":\"{chosenMode}\"}}}}}}";
                            await SendWsTextAsync(gw, sel, ct);
                        }
                        else
                        {
                            SettingsStore.Log($"Voice UDP discovery FAILED for {ip}:{port} ssrc={_voiceSsrc}");
                            VoiceStatus = "Ошибка UDP-обнаружения (проверьте сеть/VPN)";
                            Notify();
                        }
                    }
                    else if (op == 4)
                    {
                        var keyArr = data.GetProperty("secret_key");
                        byte[] key = new byte[32];
                        int i = 0;
                        foreach (var el in keyArr.EnumerateArray()) if (i < 32) key[i++] = (byte)el.GetInt32();
                        string mode = data.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "";
                        bool useXchacha = mode.Contains("xchacha", StringComparison.OrdinalIgnoreCase);
                        ushort daveVer = data.TryGetProperty("dave_protocol_version", out var dv) ? (ushort)dv.GetInt32() : (ushort)0;
                        _davePendingVersion = daveVer;

                        SetTransportKey(key, useXchacha);
                        ulong chIdU64 = ulong.TryParse(_voiceChannelId, out var c64) ? c64 : 0;
                        MediaNative.fc_dave_init(daveVer, chIdU64, Me.Id, _voiceSsrc);
                        SeedVoiceDaveUsers();

                        if (daveVer > 0)
                        {
                            var kp = GetDaveKeyPackage();
                            if (kp.Length > 0) await SendWsBinaryAsync(gw, 26, kp, ct);
                        }

                        _lastSentVoiceSpeaking = false;
                        MediaNative.fc_transport_send_silence();

                        bool wasVoice = VoiceConnected;
                        VoiceConnected = true;
                        VoiceStatus = "Голосовая связь подключена";
                        VoiceEndpointHost = _voiceEndpoint.Split(':')[0].Replace(".discord.media", "");
                        int udpPing = MediaNative.fc_transport_get_ping_ms();
                        if (udpPing > 0) RecordVoicePing(udpPing);
                        if (!wasVoice) SoundFx.Play(UiSound.VoiceJoin);
                        SettingsStore.Log($"Voice connected! host={VoiceEndpointHost} dave_ver={daveVer} ping={udpPing}ms");

                        // If user started local screen capture BEFORE joining voice, auto-publish Go Live now!
                        if (Shell.IsLocalStreaming && string.IsNullOrEmpty(_pubStreamKey))
                        {
                            _ = StartPublishingGoLiveAsync(_pubWidth, _pubHeight, _pubFps, _pubIsDualScreen);
                        }
                        Notify();
                    }
                    else if (op == 5)
                    {
                        if (data.TryGetProperty("ssrc", out var se) && data.TryGetProperty("user_id", out var ue))
                        {
                            string uid = ue.GetString() ?? "";
                            uint rSsrc = se.GetUInt32();
                            bool isSpk = data.TryGetProperty("speaking", out var spkEl) &&
                                         ((spkEl.ValueKind == JsonValueKind.Number && spkEl.GetInt32() != 0) || spkEl.ValueKind == JsonValueKind.True);
                            if (!string.IsNullOrEmpty(uid))
                            {
                                _userToSsrc[uid] = rSsrc;
                                MediaNative.fc_dave_add_user(uid);
                                MediaNative.fc_dave_map_ssrc(rSsrc, uid);
                                if (_userVolumes.TryGetValue(uid, out var uv))
                                    MediaNative.fc_voice_set_user_volume(rSsrc, uv);
                                DiscordGuild? g;
                                lock (Guilds) g = Guilds.FirstOrDefault(x => x.Id == _voiceGuildId);
                                if (g != null)
                                {
                                    lock (g.VoiceStates)
                                    {
                                        if (g.VoiceStates.TryGetValue(uid, out var vm))
                                        {
                                            vm.Speaking = isSpk;
                                            vm.SpeakingUntil = isSpk ? Environment.TickCount64 + 650 : 0;
                                        }
                                    }
                                    Notify();
                                }
                            }
                        }
                    }
                    else if (op == 11 && data.TryGetProperty("user_ids", out var uids) && uids.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var u in uids.EnumerateArray())
                            if (u.ValueKind == JsonValueKind.String) MediaNative.fc_dave_add_user(u.GetString()!);
                    }
                    else if (op == 13 && data.TryGetProperty("user_id", out var du) && du.ValueKind == JsonValueKind.String)
                    {
                        string remUid = du.GetString()!;
                        _userToSsrc.TryRemove(remUid, out _);
                        MediaNative.fc_dave_remove_user(remUid);
                    }
                    else if (op == 21)
                    {
                        ushort pv = data.TryGetProperty("protocol_version", out var pvEl) ? (ushort)pvEl.GetInt32() : (ushort)1;
                        int tid = data.TryGetProperty("transition_id", out var tEl) ? tEl.GetInt32() : 0;
                        _davePendingVersion = pv;
                        if (tid == 0) MediaNative.fc_dave_execute_transition(pv);
                        else
                        {
                            _voiceDaveTransitions[tid] = pv;
                            await SendWsTextAsync(gw, $"{{\"op\":23,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                        }
                    }
                    else if (op == 22)
                    {
                        int tid = data.TryGetProperty("transition_id", out var tEl) ? tEl.GetInt32() : 0;
                        ushort ver = _voiceDaveTransitions.Remove(tid, out var v) ? v : _davePendingVersion;
                        MediaNative.fc_dave_execute_transition(ver);
                    }
                    else if (op == 24)
                    {
                        ushort pv = data.TryGetProperty("protocol_version", out var pve) ? (ushort)pve.GetInt32() : (ushort)1;
                        int epoch = data.TryGetProperty("epoch", out var ep) ? ep.GetInt32() : 0;
                        _davePendingVersion = pv;
                        if (epoch == 1)
                        {
                            ulong chIdU64 = ulong.TryParse(_voiceChannelId, out var c64) ? c64 : 0;
                            MediaNative.fc_dave_init(pv, chIdU64, Me.Id, _voiceSsrc);
                            SeedVoiceDaveUsers();
                            var kp = GetDaveKeyPackage();
                            if (kp.Length > 0) await SendWsBinaryAsync(gw, 26, kp, ct);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested && ReferenceEquals(_voiceGw, gw))
                {
                    VoiceConnected = false;
                    VoiceStatus = "Ошибка голоса: " + ex.Message;
                    _voiceGwStarted = false;
                    SettingsStore.Log("VoiceGW error: " + ex.Message);
                    Notify();
                }
            }
            finally
            {
                if (ReferenceEquals(_voiceGw, gw)) _voiceGw = null;
            }
        }, ct);
    }

    private async Task HandleVoiceBinaryAsync(ClientWebSocket gw, byte[] buf, int len, CancellationToken ct)
    {
        if (len < 3) return;
        _voiceSeq = (buf[0] << 8) | buf[1];
        byte op = buf[2];
        int off = 3, plen = len - 3;
        ushort targetVer = _davePendingVersion > 0 ? _davePendingVersion : (ushort)1;
        switch (op)
        {
            case 25: SetDaveExternalSender(buf, off, plen); break;
            case 27:
            {
                var cw = ProcessDaveProposals(buf, off, plen);
                if (cw.Length > 0) await SendWsBinaryAsync(gw, 28, cw, ct);
                break;
            }
            case 29:
            {
                if (plen < 2) break;
                int tid = (buf[off] << 8) | buf[off + 1];
                int res = ProcessDaveCommit(buf, off + 2, plen - 2);
                if (res == 2) break; // Ignored commit per libdave -> no-op!
                if (res == 1)
                {
                    ushort ver = _voiceDaveTransitions.GetValueOrDefault(tid, targetVer);
                    if (tid == 0) MediaNative.fc_dave_execute_transition(ver);
                    else await SendWsTextAsync(gw, $"{{\"op\":23,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                }
                else
                {
                    await SendWsTextAsync(gw, $"{{\"op\":31,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                    ulong chIdU64 = ulong.TryParse(_voiceChannelId, out var c64) ? c64 : 0;
                    MediaNative.fc_dave_init(targetVer, chIdU64, Me.Id, _voiceSsrc);
                    SeedVoiceDaveUsers();
                    var kp = GetDaveKeyPackage();
                    if (kp.Length > 0) await SendWsBinaryAsync(gw, 26, kp, ct);
                }
                break;
            }
            case 30:
            {
                if (plen < 2) break;
                int tid = (buf[off] << 8) | buf[off + 1];
                if (ProcessDaveWelcome(buf, off + 2, plen - 2))
                {
                    ushort ver = _voiceDaveTransitions.GetValueOrDefault(tid, targetVer);
                    if (tid == 0) MediaNative.fc_dave_execute_transition(ver);
                    else await SendWsTextAsync(gw, $"{{\"op\":23,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                }
                else
                {
                    await SendWsTextAsync(gw, $"{{\"op\":31,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                    ulong chIdU64 = ulong.TryParse(_voiceChannelId, out var c64) ? c64 : 0;
                    MediaNative.fc_dave_init(targetVer, chIdU64, Me.Id, _voiceSsrc);
                    SeedVoiceDaveUsers();
                    var kp = GetDaveKeyPackage();
                    if (kp.Length > 0) await SendWsBinaryAsync(gw, 26, kp, ct);
                }
                break;
            }
        }
    }

    // ---------------- 4-Slot Multi-Stream Watching (OP 20 STREAM_WATCH -> Stream Voice GW v8 -> NVDEC) ----------------

    public async Task WatchStreamAsync(string guildId, string channelId, string streamerUserId, string streamerName, bool focusImmediately = true, bool selfMute = false, bool selfDeaf = false)
    {
        if (!VoiceConnected || _voiceChannelId != channelId)
        {
            await JoinVoiceAsync(guildId, channelId, selfMute, selfDeaf);
            for (int wait = 0; wait < 40 && string.IsNullOrEmpty(_voiceSessionId); wait++)
                await Task.Delay(100);
        }

        WatchSession? existing;
        lock (_watchLock) _watches.TryGetValue(streamerUserId, out existing);
        if (existing != null)
        {
            if (focusImmediately) FocusedStreamUserId = streamerUserId;
            Notify();
            return;
        }

        WatchSession ws;
        lock (_watchLock)
        {
            int freeSlot = -1;
            for (int s = 0; s < App.MaxWatchSlots; s++)
            {
                if (!_watches.Values.Any(w => w.Slot == s)) { freeSlot = s; break; }
            }
            if (freeSlot < 0)
            {
                var oldest = _watches.Values.First();
                freeSlot = oldest.Slot;
                StopWatchSessionLocked(oldest);
                _watches.Remove(oldest.StreamerUserId);
            }

            ws = new WatchSession
            {
                Slot = freeSlot,
                StreamKey = $"guild:{guildId}:{channelId}:{streamerUserId}",
                StreamerUserId = streamerUserId,
                StreamerName = streamerName,
                Status = $"Подключение к стриму {streamerName}..."
            };
            _watches[streamerUserId] = ws;
            if (focusImmediately) FocusedStreamUserId = streamerUserId;
        }
        Notify();

        if (!IsDemoMode && _gw?.State == WebSocketState.Open)
        {
            await SendWsTextAsync(_gw, $"{{\"op\":20,\"d\":{{\"stream_key\":\"{ws.StreamKey}\"}}}}", CancellationToken.None);
            SettingsStore.Log($"Sent STREAM_WATCH slot={ws.Slot}: {ws.StreamKey}");
        }
        SoundFx.Play(UiSound.StreamStart);
    }

    public void StopWatchingStream(string streamerUserId)
    {
        bool removed = false;
        lock (_watchLock)
        {
            if (_watches.TryGetValue(streamerUserId, out var ws))
            {
                StopWatchSessionLocked(ws);
                _watches.Remove(streamerUserId);
                removed = true;
            }
            if (FocusedStreamUserId == streamerUserId) FocusedStreamUserId = null;
        }
        if (removed) SoundFx.Play(UiSound.StreamStop);
        Notify();
    }

    public void StopAllWatchedStreams()
    {
        lock (_watchLock)
        {
            foreach (var ws in _watches.Values) StopWatchSessionLocked(ws);
            _watches.Clear();
            if (FocusedStreamUserId != "self") FocusedStreamUserId = null;
        }
        Notify();
    }

    private void StopWatchSessionLocked(WatchSession ws)
    {
        if (!IsDemoMode && _gw?.State == WebSocketState.Open && !string.IsNullOrEmpty(ws.StreamKey))
            _ = SendWsTextAsync(_gw, $"{{\"op\":19,\"d\":{{\"stream_key\":\"{ws.StreamKey}\"}}}}", CancellationToken.None);
        try { ws.Cts?.Cancel(); } catch { }
        try { ws.Cts?.Dispose(); } catch { }
        ws.Cts = null;
        try { ws.Gw?.Dispose(); } catch { }
        ws.Gw = null;
        MediaNative.fc_watch_stop_slot(ws.Slot);
    }

    // ---------------- Outbound Go Live Publisher (OP 18 STREAM_CREATE -> Stream GW v8 + DAVE -> NVENC TX) ----------------

    public async Task StartPublishingGoLiveAsync(int width, int height, int fps, bool isDualScreen = false)
    {
        StopPublishingGoLive();
        _pubWidth = width; _pubHeight = height; _pubFps = fps; _pubIsDualScreen = isDualScreen;
        if (IsDemoMode || !VoiceConnected || string.IsNullOrEmpty(_voiceGuildId) || string.IsNullOrEmpty(_voiceChannelId))
        {
            IsPublishingStream = true;
            PublishStatus = isDualScreen ? $"Локально: 2 Экрана {width}×{height}" : "Локальный 4K Zero-Copy";
            Notify();
            return;
        }

        _pubStreamKey = $"guild:{_voiceGuildId}:{_voiceChannelId}:{Me.Id}";
        IsPublishingStream = true;
        PublishStatus = isDualScreen ? "Запуск Go Live (2 Экрана)..." : "Запуск Go Live...";
        Notify();

        if (_gw?.State == WebSocketState.Open)
        {
            string pref = !string.IsNullOrEmpty(SettingsStore.Current.PreferredRtcRegion)
                ? SettingsStore.Current.PreferredRtcRegion
                : BestRtcRegion;
            string prefJson = string.IsNullOrEmpty(pref) ? "null" : $"\"{pref}\"";
            await SendWsTextAsync(_gw, $"{{\"op\":18,\"d\":{{\"type\":\"guild\",\"guild_id\":\"{_voiceGuildId}\",\"channel_id\":\"{_voiceChannelId}\",\"preferred_region\":{prefJson}}}}}", CancellationToken.None);
            await SendWsTextAsync(_gw, $"{{\"op\":22,\"d\":{{\"stream_key\":\"{_pubStreamKey}\",\"paused\":false}}}}", CancellationToken.None);
            SettingsStore.Log($"Sent OP 18 STREAM_CREATE (region={prefJson}) + OP 22 unpaused: {_pubStreamKey} ({width}x{height}@{fps} dual={isDualScreen})");
        }
    }

    public void StopPublishingGoLive()
    {
        if (!IsDemoMode && _gw?.State == WebSocketState.Open && !string.IsNullOrEmpty(_pubStreamKey))
            _ = SendWsTextAsync(_gw, $"{{\"op\":19,\"d\":{{\"stream_key\":\"{_pubStreamKey}\"}}}}", CancellationToken.None);
        try { _pubCts?.Cancel(); } catch { }
        try { _pubCts?.Dispose(); } catch { }
        _pubCts = null;
        try { _pubGw?.Dispose(); } catch { }
        _pubGw = null;
        MediaNative.fc_pub_disconnect();
        _pubStreamKey = "";
        _pubRtcServerId = "";
        _pubRtcChannelId = "";
        _pubToken = "";
        _pubEndpoint = "";
        _pubGwStarted = false;
        _pubDaveTransitions.Clear();
        _pubIsDualScreen = false;
        IsPublishingStream = false;
        PublishStatus = "";
        if (FocusedStreamUserId == "self") FocusedStreamUserId = null;
        Notify();
    }

    private void ParseStreamDelete(JsonElement d)
    {
        string sk = d.TryGetProperty("stream_key", out var ske) ? ske.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(sk)) return;
        string? uidToStop = null;
        lock (_watchLock)
        {
            var ws = _watches.Values.FirstOrDefault(w => w.StreamKey == sk);
            if (ws != null) uidToStop = ws.StreamerUserId;
        }
        if (uidToStop != null)
        {
            SettingsStore.Log($"STREAM_DELETE received for {sk} -> stopping watch");
            StopWatchingStream(uidToStop);
        }
    }

    private void ParseStreamCreate(JsonElement d)
    {
        string sk = d.TryGetProperty("stream_key", out var ske) ? ske.GetString() ?? "" : "";
        string rtcServer = d.TryGetProperty("rtc_server_id", out var rs) ? rs.GetString() ?? "" : "";
        string rtcChannel = d.TryGetProperty("rtc_channel_id", out var rc) ? rc.GetString() ?? "" : "";

        if (sk == _pubStreamKey && !string.IsNullOrEmpty(_pubStreamKey))
        {
            _pubRtcServerId = rtcServer;
            _pubRtcChannelId = rtcChannel;
            TryStartPublishGateway();
            return;
        }

        WatchSession? ws = null;
        lock (_watchLock) ws = _watches.Values.FirstOrDefault(w => w.StreamKey == sk);
        if (ws != null)
        {
            ws.RtcServerId = rtcServer;
            ws.RtcChannelId = rtcChannel;
            TryStartWatchGateway(ws);
        }
    }

    private void ParseStreamServerUpdate(JsonElement d)
    {
        string sk = d.TryGetProperty("stream_key", out var ske) ? ske.GetString() ?? "" : "";
        string token = d.TryGetProperty("token", out var tk) ? tk.GetString() ?? "" : "";
        string endpoint = d.TryGetProperty("endpoint", out var ep) && ep.ValueKind == JsonValueKind.String ? ep.GetString() ?? "" : "";

        if (sk == _pubStreamKey && !string.IsNullOrEmpty(_pubStreamKey))
        {
            bool changed = _pubGwStarted && (!string.Equals(_pubEndpoint, endpoint, StringComparison.Ordinal) || !string.Equals(_pubToken, token, StringComparison.Ordinal));
            _pubToken = token;
            _pubEndpoint = endpoint;
            if (string.IsNullOrEmpty(endpoint))
            {
                PublishStatus = "Переключение медиа-сервера...";
                Notify();
                return;
            }
            if (changed) _pubGwStarted = false;
            TryStartPublishGateway();
            return;
        }

        WatchSession? ws = null;
        lock (_watchLock) ws = _watches.Values.FirstOrDefault(w => w.StreamKey == sk);
        if (ws != null)
        {
            bool changed = ws.GwStarted && (!string.Equals(ws.Endpoint, endpoint, StringComparison.Ordinal) || !string.Equals(ws.Token, token, StringComparison.Ordinal));
            ws.Token = token;
            ws.Endpoint = endpoint;
            if (string.IsNullOrEmpty(endpoint))
            {
                ws.Status = "Переключение медиа-сервера...";
                Notify();
                return;
            }
            if (changed) ws.GwStarted = false;
            TryStartWatchGateway(ws);
        }
    }

    private void TryStartWatchGateway(WatchSession ws)
    {
        if (ws.GwStarted || string.IsNullOrEmpty(ws.RtcServerId) || string.IsNullOrEmpty(ws.Token) ||
            string.IsNullOrEmpty(ws.Endpoint) || string.IsNullOrEmpty(_voiceSessionId)) return;
        ws.GwStarted = true;
        ws.Seq = -1;
        ws.DaveTransitions.Clear();
        try { ws.Cts?.Cancel(); } catch { }
        try { ws.Cts?.Dispose(); } catch { }
        try { ws.Gw?.Dispose(); } catch { }
        ws.Cts = new CancellationTokenSource();
        var ct = ws.Cts.Token;

        _ = Task.Run(async () =>
        {
            int attempt = 0;
            while (!ct.IsCancellationRequested && attempt < 3)
            {
                attempt++;
                bool hbAcked = true;
                try
                {
                    try { ws.Gw?.Dispose(); } catch { }
                    ws.Gw = new ClientWebSocket();
                    await ws.Gw.ConnectAsync(new Uri($"wss://{ws.Endpoint}/?v=8"), ct);
                    string identify = $"{{\"op\":0,\"d\":{{\"server_id\":\"{ws.RtcServerId}\",\"user_id\":\"{Me.Id}\",\"session_id\":\"{_voiceSessionId}\",\"token\":\"{ws.Token}\",\"video\":true,\"streams\":[{{\"type\":\"screen\",\"rid\":\"100\",\"quality\":100}}],\"max_dave_protocol_version\":1}}}}";
                    await SendWsTextAsync(ws.Gw, identify, ct);
                    SettingsStore.Log($"WatchGW[{ws.Slot}] connecting to {ws.Endpoint} ({ws.StreamerName}) attempt={attempt}");

                    uint mySsrc = 0;
                    var buf = new byte[65536];
                    using var ms = new MemoryStream();
                    bool retryLoop = false;
                    while (!ct.IsCancellationRequested && ws.Gw.State == WebSocketState.Open)
                    {
                        ms.SetLength(0);
                        WebSocketReceiveResult res;
                        do
                        {
                            res = await ws.Gw.ReceiveAsync(buf, ct);
                            if (res.MessageType == WebSocketMessageType.Close)
                            {
                                int ccode = (int)(res.CloseStatus ?? 0);
                                SettingsStore.Log($"WatchGW[{ws.Slot}] closed: {ccode} ({res.CloseStatus}) {res.CloseStatusDescription}");
                                if (ccode is 4004 or 4006 or 4014) return;
                                retryLoop = true;
                                break;
                            }
                            ms.Write(buf, 0, res.Count);
                        } while (!res.EndOfMessage);
                        if (retryLoop) break;

                        if (res.MessageType == WebSocketMessageType.Binary)
                        {
                            await HandleWatchBinaryAsync(ws, ms.GetBuffer(), (int)ms.Length, ct);
                            continue;
                        }

                        using var doc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                        var root = doc.RootElement;
                        if (root.TryGetProperty("seq", out var sq) && sq.ValueKind == JsonValueKind.Number) ws.Seq = sq.GetInt32();
                        int op = root.GetProperty("op").GetInt32();
                        var data = root.TryGetProperty("d", out var dEl) ? dEl : default;

                        if (op == 8)
                        {
                            int hb = Math.Clamp((int)data.GetProperty("heartbeat_interval").GetDouble(), 2000, 60000);
                            var hbGw = ws.Gw;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    while (!ct.IsCancellationRequested && hbGw?.State == WebSocketState.Open)
                                    {
                                        if (!hbAcked) { hbGw.Abort(); return; }
                                        hbAcked = false;
                                        await SendWsTextAsync(hbGw, $"{{\"op\":3,\"d\":{{\"t\":{Environment.TickCount64},\"seq_ack\":{ws.Seq}}}}}", ct);
                                        await Task.Delay(hb, ct);
                                    }
                                }
                                catch { }
                            }, ct);
                        }
                        else if (op == 6)
                        {
                            hbAcked = true;
                        }
                        else if (op == 2)
                        {
                            mySsrc = data.GetProperty("ssrc").GetUInt32();
                            ws.LocalSsrc = mySsrc;
                            string ip = data.GetProperty("ip").GetString() ?? "";
                            ushort port = (ushort)data.GetProperty("port").GetInt32();
                            string chosenMode = SelectSupportedVoiceCipher(data);
                            if (DiscoverWatchEndpoint(ws.Slot, ip, port, mySsrc, App.StreamHwnds[ws.Slot], out string extIp, out ushort extPort) &&
                                System.Net.IPAddress.TryParse(extIp, out _))
                            {
                                string sel = $"{{\"op\":1,\"d\":{{\"protocol\":\"udp\",\"data\":{{\"address\":\"{extIp}\",\"port\":{extPort},\"mode\":\"{chosenMode}\"}},\"codecs\":[{{\"name\":\"opus\",\"type\":\"audio\",\"priority\":1000,\"payload_type\":120}},{{\"name\":\"H264\",\"type\":\"video\",\"priority\":1000,\"payload_type\":101,\"rtx_payload_type\":102}}]}}}}";
                                await SendWsTextAsync(ws.Gw, sel, ct);
                            }
                            else
                            {
                                SettingsStore.Log($"WatchGW[{ws.Slot}] UDP discovery FAILED for {ip}:{port}");
                                ws.Status = "Ошибка UDP стрима";
                                Notify();
                            }
                        }
                        else if (op == 4)
                        {
                            var keyArr = data.GetProperty("secret_key");
                            byte[] key = new byte[32];
                            int i = 0;
                            foreach (var el in keyArr.EnumerateArray()) if (i < 32) key[i++] = (byte)el.GetInt32();
                            string mode = data.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "";
                            bool useX = mode.Contains("xchacha", StringComparison.OrdinalIgnoreCase);
                            ushort dv = data.TryGetProperty("dave_protocol_version", out var dve) ? (ushort)dve.GetInt32() : (ushort)0;
                            ws.DaveVer = dv;

                            SetWatchKey(ws.Slot, key, useX);
                            ulong ch64 = ulong.TryParse(ws.RtcChannelId, out var c64) ? c64 : (ulong.TryParse(_voiceChannelId, out var c2) ? c2 : 0);
                            MediaNative.fc_watch_dave_init_slot(ws.Slot, dv, ch64, Me.Id, mySsrc);
                            SeedWatchDaveUsers(ws);
                            // Pre-bind streamer_user_id BEFORE MLS Welcome arrives so DAVE ratchet attaches immediately!
                            MediaNative.fc_watch_set_remote_slot(ws.Slot, 0, 0, ws.StreamerUserId);
                            MediaNative.fc_watch_set_volume_slot(ws.Slot, ws.Muted ? 0f : ws.Volume);

                            if (dv > 0)
                            {
                                var kp = GetWatchDaveKeyPackage(ws.Slot);
                                if (kp.Length > 0) await SendWsBinaryAsync(ws.Gw, 26, kp, ct);
                            }
                            else
                            {
                                // No DAVE -> request full quality immediately
                                await SendWsTextAsync(ws.Gw, "{\"op\":15,\"d\":{\"any\":100}}", ct);
                            }

                            ws.ConnectedTick = Environment.TickCount64;
                            ws.Status = $"Стрим {ws.StreamerName}";
                            SettingsStore.Log($"WatchGW[{ws.Slot}] connected! dave_ver={dv} streamer={ws.StreamerUserId}");
                            Notify();
                        }
                        else if (op == 12)
                        {
                            string suid = data.TryGetProperty("user_id", out var ue) && ue.ValueKind == JsonValueKind.String
                                ? ue.GetString() ?? ws.StreamerUserId : ws.StreamerUserId;
                            if (!string.IsNullOrEmpty(suid) && suid != ws.StreamerUserId) continue;

                            uint vssrc = data.TryGetProperty("video_ssrc", out var vsEl) ? vsEl.GetUInt32() : 0;
                            uint assrc = data.TryGetProperty("audio_ssrc", out var asr) ? asr.GetUInt32() : 0;
                            if (data.TryGetProperty("streams", out var strs) && strs.ValueKind == JsonValueKind.Array)
                            {
                                uint bestSsrc = 0;
                                int bestQ = -1;
                                foreach (var stEl in strs.EnumerateArray())
                                {
                                    bool act = !stEl.TryGetProperty("active", out var actEl) || actEl.ValueKind != JsonValueKind.False;
                                    uint ss = stEl.TryGetProperty("ssrc", out var ssEl) && ssEl.ValueKind == JsonValueKind.Number ? ssEl.GetUInt32() : 0;
                                    int q = stEl.TryGetProperty("quality", out var qEl) && qEl.ValueKind == JsonValueKind.Number ? qEl.GetInt32() : 100;
                                    if (ss != 0 && act && q >= bestQ) { bestSsrc = ss; bestQ = q; }
                                }
                                if (bestSsrc != 0) vssrc = bestSsrc;
                            }
                            if (vssrc != 0 || assrc != 0)
                            {
                                if (vssrc != 0) ws.VideoSsrc = vssrc;
                                SettingsStore.Log($"WatchGW[{ws.Slot}] OP12 video_ssrc={vssrc} audio_ssrc={assrc} user={suid}");
                                MediaNative.fc_watch_set_remote_slot(ws.Slot, vssrc, assrc, suid);
                            }
                        }
                        else if (op == 24)
                        {
                            ushort pv = data.TryGetProperty("protocol_version", out var pve) ? (ushort)pve.GetInt32() : (ushort)1;
                            int epoch = data.TryGetProperty("epoch", out var ep) ? ep.GetInt32() : 0;
                            ws.DaveVer = pv;
                            if (epoch == 1)
                            {
                                ulong ch64 = ulong.TryParse(ws.RtcChannelId, out var c64) ? c64 : (ulong.TryParse(_voiceChannelId, out var c2) ? c2 : 0);
                                MediaNative.fc_watch_dave_init_slot(ws.Slot, pv, ch64, Me.Id, mySsrc);
                                SeedWatchDaveUsers(ws);
                                var kp = GetWatchDaveKeyPackage(ws.Slot);
                                if (kp.Length > 0) await SendWsBinaryAsync(ws.Gw, 26, kp, ct);
                            }
                        }
                        else if (op == 11 && data.TryGetProperty("user_ids", out var uids) && uids.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var u in uids.EnumerateArray())
                                if (u.ValueKind == JsonValueKind.String) MediaNative.fc_watch_dave_add_user_slot(ws.Slot, u.GetString()!);
                        }
                        else if (op == 13 && data.TryGetProperty("user_id", out var du) && du.ValueKind == JsonValueKind.String)
                        {
                            string leftUid = du.GetString()!;
                            if (leftUid != ws.StreamerUserId)
                                MediaNative.fc_watch_dave_remove_user_slot(ws.Slot, leftUid);
                        }
                        else if (op == 21)
                        {
                            ushort pv = data.TryGetProperty("protocol_version", out var pvEl) ? (ushort)pvEl.GetInt32() : (ushort)1;
                            int tid = data.TryGetProperty("transition_id", out var tEl) ? tEl.GetInt32() : 0;
                            ws.DaveVer = pv;
                            if (tid == 0)
                            {
                                MediaNative.fc_watch_dave_execute_transition_slot(ws.Slot, pv);
                                string onMsg = ws.VideoSsrc != 0 ? $"{{\"op\":15,\"d\":{{\"{ws.VideoSsrc}\":100,\"any\":100}}}}" : "{\"op\":15,\"d\":{\"any\":100}}";
                                await SendWsTextAsync(ws.Gw, onMsg, ct);
                            }
                            else
                            {
                                ws.DaveTransitions[tid] = pv;
                                await SendWsTextAsync(ws.Gw, $"{{\"op\":23,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                            }
                        }
                        else if (op == 22)
                        {
                            int tid = data.TryGetProperty("transition_id", out var tEl) ? tEl.GetInt32() : 0;
                            ushort ver = ws.DaveTransitions.Remove(tid, out var v) ? v : ws.DaveVer;
                            MediaNative.fc_watch_dave_execute_transition_slot(ws.Slot, ver);
                            // Request full quality right AFTER DAVE key ratchet is active so the SFU's fresh IDR keyframe decrypts cleanly!
                            string onMsg = ws.VideoSsrc != 0 ? $"{{\"op\":15,\"d\":{{\"{ws.VideoSsrc}\":100,\"any\":100}}}}" : "{\"op\":15,\"d\":{\"any\":100}}";
                            await SendWsTextAsync(ws.Gw, onMsg, ct);
                        }
                    }
                    if (!ct.IsCancellationRequested) await Task.Delay(750 * attempt, ct);
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        ws.Status = "Переподключение стрима...";
                        SettingsStore.Log($"WatchGW[{ws.Slot}] error (attempt={attempt}): " + ex.Message);
                        Notify();
                        try { await Task.Delay(900 * attempt, ct); } catch { break; }
                    }
                }
            }
            if (!ct.IsCancellationRequested) ws.GwStarted = false;
        }, ct);
    }

    private async Task HandleWatchBinaryAsync(WatchSession ws, byte[] buf, int len, CancellationToken ct)
    {
        if (len < 3) return;
        ws.Seq = (buf[0] << 8) | buf[1];
        byte op = buf[2];
        int off = 3, plen = len - 3;
        switch (op)
        {
            case 25: SetWatchDaveExtSender(ws.Slot, buf, off, plen); break;
            case 27:
            {
                var cw = ProcessWatchDaveProposals(ws.Slot, buf, off, plen);
                if (cw.Length > 0) await SendWsBinaryAsync(ws.Gw, 28, cw, ct);
                break;
            }
            case 29:
            {
                if (plen < 2) break;
                int tid = (buf[off] << 8) | buf[off + 1];
                int res = ProcessWatchDaveCommit(ws.Slot, buf, off + 2, plen - 2);
                if (res == 2) break; // Ignored commit before Welcome
                if (res == 1)
                {
                    if (tid == 0) MediaNative.fc_watch_dave_execute_transition_slot(ws.Slot, 1);
                    else if (ws.Gw != null) await SendWsTextAsync(ws.Gw, $"{{\"op\":23,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                }
                else if (ws.Gw != null)
                {
                    SettingsStore.Log($"WatchGW[{ws.Slot}] DAVE Commit failed tid={tid} -> OP31 recovery");
                    await SendWsTextAsync(ws.Gw, $"{{\"op\":31,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                    ulong ch64 = ulong.TryParse(ws.RtcChannelId, out var c64) ? c64 : (ulong.TryParse(_voiceChannelId, out var c2) ? c2 : 0);
                    MediaNative.fc_watch_dave_init_slot(ws.Slot, 1, ch64, Me.Id, ws.LocalSsrc);
                    SeedWatchDaveUsers(ws);
                    var kp = GetWatchDaveKeyPackage(ws.Slot);
                    if (kp.Length > 0) await SendWsBinaryAsync(ws.Gw, 26, kp, ct);
                }
                break;
            }
            case 30:
            {
                if (plen < 2) break;
                int tid = (buf[off] << 8) | buf[off + 1];
                bool ok = ProcessWatchDaveWelcome(ws.Slot, buf, off + 2, plen - 2);
                SettingsStore.Log($"WatchGW[{ws.Slot}] DAVE Welcome tid={tid} ok={ok}");
                if (ok)
                {
                    if (tid == 0)
                    {
                        MediaNative.fc_watch_dave_execute_transition_slot(ws.Slot, 1);
                        if (ws.Gw != null)
                        {
                            string onMsg = ws.VideoSsrc != 0 ? $"{{\"op\":15,\"d\":{{\"{ws.VideoSsrc}\":100,\"any\":100}}}}" : "{\"op\":15,\"d\":{\"any\":100}}";
                            await SendWsTextAsync(ws.Gw, onMsg, ct);
                        }
                    }
                    else if (ws.Gw != null) await SendWsTextAsync(ws.Gw, $"{{\"op\":23,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                }
                else if (ws.Gw != null)
                {
                    SettingsStore.Log($"WatchGW[{ws.Slot}] DAVE Welcome failed tid={tid} -> OP31 recovery");
                    await SendWsTextAsync(ws.Gw, $"{{\"op\":31,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                    ulong ch64 = ulong.TryParse(ws.RtcChannelId, out var c64) ? c64 : (ulong.TryParse(_voiceChannelId, out var c2) ? c2 : 0);
                    MediaNative.fc_watch_dave_init_slot(ws.Slot, 1, ch64, Me.Id, ws.LocalSsrc);
                    SeedWatchDaveUsers(ws);
                    var kp = GetWatchDaveKeyPackage(ws.Slot);
                    if (kp.Length > 0) await SendWsBinaryAsync(ws.Gw, 26, kp, ct);
                }
                break;
            }
        }
    }

    private void TryStartPublishGateway()
    {
        if (_pubGwStarted || string.IsNullOrEmpty(_pubRtcServerId) || string.IsNullOrEmpty(_pubToken) ||
            string.IsNullOrEmpty(_pubEndpoint) || string.IsNullOrEmpty(_voiceSessionId)) return;
        _pubGwStarted = true;
        _pubSeq = -1;
        _pubDaveTransitions.Clear();
        try { _pubCts?.Cancel(); } catch { }
        try { _pubCts?.Dispose(); } catch { }
        try { _pubGw?.Dispose(); } catch { }
        _pubCts = new CancellationTokenSource();
        var ct = _pubCts.Token;

        _ = Task.Run(async () =>
        {
            int attempt = 0;
            while (!ct.IsCancellationRequested && attempt < 3)
            {
                attempt++;
                bool hbAcked = true;
                try
                {
                    try { _pubGw?.Dispose(); } catch { }
                    _pubGw = new ClientWebSocket();
                    await _pubGw.ConnectAsync(new Uri($"wss://{_pubEndpoint}/?v=8"), ct);
                    string identify = $"{{\"op\":0,\"d\":{{\"server_id\":\"{_pubRtcServerId}\",\"user_id\":\"{Me.Id}\",\"session_id\":\"{_voiceSessionId}\",\"token\":\"{_pubToken}\",\"video\":true,\"streams\":[{{\"type\":\"screen\",\"rid\":\"100\",\"quality\":100}}],\"max_dave_protocol_version\":1}}}}";
                    await SendWsTextAsync(_pubGw, identify, ct);
                    SettingsStore.Log($"PubGW connecting to {_pubEndpoint} attempt={attempt}");

                    uint mySsrc = 0, videoSsrc = 0, rtxSsrc = 0;
                    var buf = new byte[65536];
                    using var ms = new MemoryStream();
                    bool retryLoop = false;
                    while (!ct.IsCancellationRequested && _pubGw.State == WebSocketState.Open)
                    {
                        ms.SetLength(0);
                        WebSocketReceiveResult res;
                        do
                        {
                            res = await _pubGw.ReceiveAsync(buf, ct);
                            if (res.MessageType == WebSocketMessageType.Close)
                            {
                                int ccode = (int)(res.CloseStatus ?? 0);
                                SettingsStore.Log($"PubGW closed: {ccode} ({res.CloseStatus}) {res.CloseStatusDescription}");
                                if (ccode is 4004 or 4006 or 4014) return;
                                retryLoop = true;
                                break;
                            }
                            ms.Write(buf, 0, res.Count);
                        } while (!res.EndOfMessage);
                        if (retryLoop) break;

                        if (res.MessageType == WebSocketMessageType.Binary)
                        {
                            await HandlePubBinaryAsync(ms.GetBuffer(), (int)ms.Length, ct);
                            continue;
                        }

                        using var doc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                        var root = doc.RootElement;
                        if (root.TryGetProperty("seq", out var sq) && sq.ValueKind == JsonValueKind.Number) _pubSeq = sq.GetInt32();
                        int op = root.GetProperty("op").GetInt32();
                        var data = root.TryGetProperty("d", out var dEl) ? dEl : default;

                        if (op == 8)
                        {
                            int hb = Math.Clamp((int)data.GetProperty("heartbeat_interval").GetDouble(), 2000, 60000);
                            var hbGw = _pubGw;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    while (!ct.IsCancellationRequested && hbGw?.State == WebSocketState.Open)
                                    {
                                        if (!hbAcked) { hbGw.Abort(); return; }
                                        hbAcked = false;
                                        await SendWsTextAsync(hbGw, $"{{\"op\":3,\"d\":{{\"t\":{Environment.TickCount64},\"seq_ack\":{_pubSeq}}}}}", ct);
                                        await Task.Delay(hb, ct);
                                    }
                                }
                                catch { }
                            }, ct);
                        }
                        else if (op == 6)
                        {
                            hbAcked = true;
                        }
                        else if (op == 2)
                        {
                            mySsrc = data.GetProperty("ssrc").GetUInt32();
                            _pubMySsrc = mySsrc;
                            videoSsrc = mySsrc + 1;
                            _pubVideoSsrc = videoSsrc;
                            rtxSsrc = mySsrc + 2;
                            string ip = data.GetProperty("ip").GetString() ?? "";
                            ushort port = (ushort)data.GetProperty("port").GetInt32();
                            string chosenMode = SelectSupportedVoiceCipher(data);
                            if (DiscoverPubEndpoint(ip, port, mySsrc, out string extIp, out ushort extPort) &&
                                System.Net.IPAddress.TryParse(extIp, out _))
                            {
                                MediaNative.fc_pub_set_video_ssrc(videoSsrc);
                                string sel = $"{{\"op\":1,\"d\":{{\"protocol\":\"udp\",\"data\":{{\"address\":\"{extIp}\",\"port\":{extPort},\"mode\":\"{chosenMode}\"}},\"codecs\":[{{\"name\":\"opus\",\"type\":\"audio\",\"priority\":1000,\"payload_type\":120}},{{\"name\":\"H264\",\"type\":\"video\",\"priority\":1000,\"payload_type\":101,\"rtx_payload_type\":102,\"encode\":true,\"decode\":true}}]}}}}";
                                await SendWsTextAsync(_pubGw, sel, ct);
                            }
                            else
                            {
                                SettingsStore.Log($"PubGW UDP discovery FAILED for {ip}:{port}");
                                PublishStatus = "Ошибка UDP Go Live";
                                Notify();
                            }
                        }
                        else if (op == 4)
                        {
                            var keyArr = data.GetProperty("secret_key");
                            byte[] key = new byte[32];
                            int i = 0;
                            foreach (var el in keyArr.EnumerateArray()) if (i < 32) key[i++] = (byte)el.GetInt32();
                            string mode = data.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "";
                            bool useX = mode.Contains("xchacha", StringComparison.OrdinalIgnoreCase);
                            ushort dv = data.TryGetProperty("dave_protocol_version", out var dve) ? (ushort)dve.GetInt32() : (ushort)0;
                            _pubDaveVer = dv;

                            SetPubKey(key, useX);
                            ulong ch64 = ulong.TryParse(_pubRtcChannelId, out var c64) ? c64 : (ulong.TryParse(_voiceChannelId, out var c2) ? c2 : 0);
                            MediaNative.fc_pub_dave_init(dv, ch64, Me.Id, mySsrc, videoSsrc);
                            SeedPubDaveUsers();
                            if (dv > 0)
                            {
                                var kp = GetPubDaveKeyPackage();
                                if (kp.Length > 0) await SendWsBinaryAsync(_pubGw, 26, kp, ct);
                            }

                            // CRITICAL FOR STREAM SYSTEM AUDIO: Announce OP 5 SPEAKING on Stream GW so Discord SFU routes our audio_ssrc (PT 120) to every viewer!
                            await SendWsTextAsync(_pubGw, $"{{\"op\":5,\"d\":{{\"speaking\":1,\"delay\":0,\"ssrc\":{mySsrc}}}}}", ct);

                            // Announce our H.264 stream SSRC + resolution/fps via OP 12 VIDEO (type="video" per Discord RTC spec!)
                            int cfgBr = Math.Clamp(SettingsStore.Current.StreamBitrateKbps * 1000, 2000000, 25000000);
                            int minBr = _pubIsDualScreen ? 12000000 : (_pubHeight >= 2160 ? 12000000 : _pubHeight >= 1440 ? 9500000 : 7500000);
                            int maxBr = Math.Max(cfgBr, minBr);
                            string op12 = $"{{\"op\":12,\"d\":{{\"audio_ssrc\":{mySsrc},\"video_ssrc\":{videoSsrc},\"rtx_ssrc\":{rtxSsrc},\"streams\":[{{\"type\":\"video\",\"rid\":\"100\",\"ssrc\":{videoSsrc},\"active\":true,\"quality\":100,\"rtx_ssrc\":{rtxSsrc},\"max_bitrate\":{maxBr},\"max_framerate\":{_pubFps},\"max_resolution\":{{\"type\":\"fixed\",\"width\":{_pubWidth},\"height\":{_pubHeight}}}}}]}}}}";
                            await SendWsTextAsync(_pubGw, op12, ct);
                            MediaNative.fc_pub_request_keyframe();

                            PubEndpointHost = _pubEndpoint.Split(':')[0].Replace(".discord.media", "");
                            PublishStatus = _pubIsDualScreen
                                ? $"2 Экрана {_pubWidth}×{_pubHeight} {_pubFps}fps"
                                : $"В эфире {_pubHeight}p {_pubFps}fps";
                            SettingsStore.Log($"PubGW LIVE! host={PubEndpointHost} audio_ssrc={mySsrc} video_ssrc={videoSsrc} dave_ver={dv} max_br={maxBr} dual={_pubIsDualScreen}");
                            Notify();
                        }
                        else if (op == 11 && data.TryGetProperty("user_ids", out var uids) && uids.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var u in uids.EnumerateArray())
                                if (u.ValueKind == JsonValueKind.String) MediaNative.fc_pub_dave_add_user(u.GetString()!);
                            if (mySsrc != 0) await SendWsTextAsync(_pubGw, $"{{\"op\":5,\"d\":{{\"speaking\":1,\"delay\":0,\"ssrc\":{mySsrc}}}}}", ct);
                            MediaNative.fc_pub_request_keyframe();
                            SettingsStore.Log("PubGW viewer joined (OP 11) -> OP5 audio + forced IDR keyframe");
                        }
                        else if (op == 13 && data.TryGetProperty("user_id", out var du) && du.ValueKind == JsonValueKind.String)
                            MediaNative.fc_pub_dave_remove_user(du.GetString()!);
                        else if (op == 21)
                        {
                            ushort pv = data.TryGetProperty("protocol_version", out var pvEl) ? (ushort)pvEl.GetInt32() : (ushort)1;
                            int tid = data.TryGetProperty("transition_id", out var tEl) ? tEl.GetInt32() : 0;
                            _pubDaveVer = pv;
                            if (tid == 0)
                            {
                                MediaNative.fc_pub_dave_execute_transition(pv);
                                if (mySsrc != 0) await SendWsTextAsync(_pubGw, $"{{\"op\":5,\"d\":{{\"speaking\":1,\"delay\":0,\"ssrc\":{mySsrc}}}}}", ct);
                                MediaNative.fc_pub_request_keyframe();
                            }
                            else
                            {
                                _pubDaveTransitions[tid] = pv;
                                await SendWsTextAsync(_pubGw, $"{{\"op\":23,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                            }
                        }
                        else if (op == 22)
                        {
                            int tid = data.TryGetProperty("transition_id", out var tEl) ? tEl.GetInt32() : 0;
                            ushort ver = _pubDaveTransitions.Remove(tid, out var v) ? v : _pubDaveVer;
                            MediaNative.fc_pub_dave_execute_transition(ver);
                            if (mySsrc != 0) await SendWsTextAsync(_pubGw, $"{{\"op\":5,\"d\":{{\"speaking\":1,\"delay\":0,\"ssrc\":{mySsrc}}}}}", ct);
                            MediaNative.fc_pub_request_keyframe();
                            SettingsStore.Log($"PubGW DAVE ExecuteTransition ver={ver} -> OP5 audio + forced IDR keyframe");
                        }
                        else if (op == 24)
                        {
                            ushort pv = data.TryGetProperty("protocol_version", out var pve) ? (ushort)pve.GetInt32() : (ushort)1;
                            int epoch = data.TryGetProperty("epoch", out var ep) ? ep.GetInt32() : 0;
                            _pubDaveVer = pv;
                            if (epoch == 1)
                            {
                                ulong ch64 = ulong.TryParse(_pubRtcChannelId, out var c64) ? c64 : (ulong.TryParse(_voiceChannelId, out var c2) ? c2 : 0);
                                MediaNative.fc_pub_dave_init(pv, ch64, Me.Id, mySsrc, videoSsrc);
                                SeedPubDaveUsers();
                                var kp = GetPubDaveKeyPackage();
                                if (kp.Length > 0) await SendWsBinaryAsync(_pubGw, 26, kp, ct);
                            }
                        }
                    }
                    if (!ct.IsCancellationRequested) await Task.Delay(750 * attempt, ct);
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        PublishStatus = "Переподключение Go Live...";
                        SettingsStore.Log($"PubGW error (attempt={attempt}): " + ex.Message);
                        Notify();
                        try { await Task.Delay(900 * attempt, ct); } catch { break; }
                    }
                }
            }
            if (!ct.IsCancellationRequested) _pubGwStarted = false;
        }, ct);
    }

    private async Task HandlePubBinaryAsync(byte[] buf, int len, CancellationToken ct)
    {
        if (len < 3) return;
        _pubSeq = (buf[0] << 8) | buf[1];
        byte op = buf[2];
        int off = 3, plen = len - 3;
        switch (op)
        {
            case 25: SetPubDaveExtSender(buf, off, plen); break;
            case 27:
            {
                var cw = ProcessPubDaveProposals(buf, off, plen);
                if (cw.Length > 0) await SendWsBinaryAsync(_pubGw, 28, cw, ct);
                break;
            }
            case 29:
            {
                if (plen < 2) break;
                int tid = (buf[off] << 8) | buf[off + 1];
                int res = ProcessPubDaveCommit(buf, off + 2, plen - 2);
                SettingsStore.Log($"PubGW DAVE Commit tid={tid} res={res}");
                if (res == 2) break; // Ignored commit before Welcome
                if (res == 1)
                {
                    if (tid == 0)
                    {
                        MediaNative.fc_pub_dave_execute_transition(1);
                        if (_pubMySsrc != 0 && _pubGw != null) await SendWsTextAsync(_pubGw, $"{{\"op\":5,\"d\":{{\"speaking\":1,\"delay\":0,\"ssrc\":{_pubMySsrc}}}}}", ct);
                        MediaNative.fc_pub_request_keyframe();
                    }
                    else if (_pubGw != null) await SendWsTextAsync(_pubGw, $"{{\"op\":23,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                }
                else if (_pubGw != null)
                {
                    SettingsStore.Log($"PubGW DAVE Commit failed tid={tid} -> OP31 recovery");
                    await SendWsTextAsync(_pubGw, $"{{\"op\":31,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                    ulong ch64 = ulong.TryParse(_pubRtcChannelId, out var c64) ? c64 : (ulong.TryParse(_voiceChannelId, out var c2) ? c2 : 0);
                    MediaNative.fc_pub_dave_init(1, ch64, Me.Id, _pubMySsrc, _pubVideoSsrc);
                    SeedPubDaveUsers();
                    var kp = GetPubDaveKeyPackage();
                    if (kp.Length > 0) await SendWsBinaryAsync(_pubGw, 26, kp, ct);
                }
                break;
            }
            case 30:
            {
                if (plen < 2) break;
                int tid = (buf[off] << 8) | buf[off + 1];
                bool ok = ProcessPubDaveWelcome(buf, off + 2, plen - 2);
                SettingsStore.Log($"PubGW DAVE Welcome tid={tid} ok={ok}");
                if (ok)
                {
                    if (tid == 0)
                    {
                        MediaNative.fc_pub_dave_execute_transition(1);
                        if (_pubMySsrc != 0 && _pubGw != null) await SendWsTextAsync(_pubGw, $"{{\"op\":5,\"d\":{{\"speaking\":1,\"delay\":0,\"ssrc\":{_pubMySsrc}}}}}", ct);
                        MediaNative.fc_pub_request_keyframe();
                    }
                    else if (_pubGw != null) await SendWsTextAsync(_pubGw, $"{{\"op\":23,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                }
                else if (_pubGw != null)
                {
                    SettingsStore.Log($"PubGW DAVE Welcome failed tid={tid} -> OP31 recovery");
                    await SendWsTextAsync(_pubGw, $"{{\"op\":31,\"d\":{{\"transition_id\":{tid}}}}}", ct);
                    ulong ch64 = ulong.TryParse(_pubRtcChannelId, out var c64) ? c64 : (ulong.TryParse(_voiceChannelId, out var c2) ? c2 : 0);
                    MediaNative.fc_pub_dave_init(1, ch64, Me.Id, _pubMySsrc, _pubVideoSsrc);
                    SeedPubDaveUsers();
                    var kp = GetPubDaveKeyPackage();
                    if (kp.Length > 0) await SendWsBinaryAsync(_pubGw, 26, kp, ct);
                }
                break;
            }
        }
    }

    // ---------------- Native Unsafe Wrappers ----------------

    private static async Task SendWsBinaryAsync(ClientWebSocket? ws, byte op, byte[] payload, CancellationToken ct)
    {
        if (ws == null || ws.State != WebSocketState.Open) return;
        var frame = new byte[1 + payload.Length];
        frame[0] = op;
        Array.Copy(payload, 0, frame, 1, payload.Length);
        var sem = _wsSendLocks.GetValue(ws, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
        }
        finally { sem.Release(); }
    }

    private static async Task SendWsTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        var sem = _wsSendLocks.GetValue(ws, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
        }
        finally { sem.Release(); }
    }

    private static unsafe bool DiscoverUdpEndpoint(string ip, ushort port, uint ssrc, out string extIp, out ushort extPort)
    {
        byte* buf = stackalloc byte[65]; ushort p = 0;
        if (MediaNative.fc_transport_connect(ip, port, ssrc, buf, 65, &p) != 0)
        { extIp = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buf) ?? ""; extPort = p; return true; }
        extIp = ""; extPort = 0; return false;
    }
    private static unsafe void SetTransportKey(byte[] k, bool x) { fixed (byte* p = k) MediaNative.fc_transport_set_key(p, x ? 1 : 0); }
    private static unsafe byte[] GetDaveKeyPackage() { var t = new byte[4096]; int n; fixed (byte* p = t) n = MediaNative.fc_dave_get_key_package(p, t.Length); return n > 0 ? t[..n] : []; }
    private static unsafe void SetDaveExternalSender(byte[] b, int o, int l) { fixed (byte* p = b) MediaNative.fc_dave_set_external_sender(p + o, l); }
    private static unsafe byte[] ProcessDaveProposals(byte[] b, int o, int l) { var t = new byte[65536]; int n; fixed (byte* pi = b) fixed (byte* po = t) n = MediaNative.fc_dave_process_proposals(pi + o, l, po, t.Length); return n > 0 ? t[..n] : []; }
    private static unsafe int ProcessDaveCommit(byte[] b, int o, int l) { fixed (byte* p = b) return MediaNative.fc_dave_process_commit(p + o, l); }
    private static unsafe bool ProcessDaveWelcome(byte[] b, int o, int l) { fixed (byte* p = b) return MediaNative.fc_dave_process_welcome(p + o, l) != 0; }

    private static unsafe bool DiscoverWatchEndpoint(int slot, string ip, ushort port, uint ssrc, nint hwnd, out string extIp, out ushort extPort)
    {
        byte* buf = stackalloc byte[65]; ushort p = 0;
        if (MediaNative.fc_watch_connect_slot(slot, ip, port, ssrc, hwnd, buf, 65, &p) != 0)
        { extIp = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buf) ?? ""; extPort = p; return true; }
        extIp = ""; extPort = 0; return false;
    }
    private static unsafe void SetWatchKey(int slot, byte[] k, bool x) { fixed (byte* p = k) MediaNative.fc_watch_set_key_slot(slot, p, x ? 1 : 0); }
    private static unsafe byte[] GetWatchDaveKeyPackage(int slot) { var b = new byte[4096]; int n; fixed (byte* p = b) n = MediaNative.fc_watch_dave_get_key_package_slot(slot, p, b.Length); return n > 0 ? b[..n] : []; }
    private static unsafe void SetWatchDaveExtSender(int slot, byte[] b, int o, int l) { fixed (byte* p = b) MediaNative.fc_watch_dave_set_external_sender_slot(slot, p + o, l); }
    private static unsafe byte[] ProcessWatchDaveProposals(int slot, byte[] b, int o, int l) { var t = new byte[65536]; int n; fixed (byte* pi = b) fixed (byte* po = t) n = MediaNative.fc_watch_dave_process_proposals_slot(slot, pi + o, l, po, t.Length); return n > 0 ? t[..n] : []; }
    private static unsafe int ProcessWatchDaveCommit(int slot, byte[] b, int o, int l) { fixed (byte* p = b) return MediaNative.fc_watch_dave_process_commit_slot(slot, p + o, l); }
    private static unsafe bool ProcessWatchDaveWelcome(int slot, byte[] b, int o, int l) { fixed (byte* p = b) return MediaNative.fc_watch_dave_process_welcome_slot(slot, p + o, l) != 0; }

    private static unsafe bool DiscoverPubEndpoint(string ip, ushort port, uint ssrc, out string extIp, out ushort extPort)
    {
        byte* buf = stackalloc byte[65]; ushort p = 0;
        if (MediaNative.fc_pub_connect(ip, port, ssrc, buf, 65, &p) != 0)
        { extIp = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buf) ?? ""; extPort = p; return true; }
        extIp = ""; extPort = 0; return false;
    }
    private static unsafe void SetPubKey(byte[] k, bool x) { fixed (byte* p = k) MediaNative.fc_pub_set_key(p, x ? 1 : 0); }
    private static unsafe byte[] GetPubDaveKeyPackage() { var b = new byte[4096]; int n; fixed (byte* p = b) n = MediaNative.fc_pub_dave_get_key_package(p, b.Length); return n > 0 ? b[..n] : []; }
    private static unsafe void SetPubDaveExtSender(byte[] b, int o, int l) { fixed (byte* p = b) MediaNative.fc_pub_dave_set_external_sender(p + o, l); }
    private static unsafe byte[] ProcessPubDaveProposals(byte[] b, int o, int l) { var t = new byte[65536]; int n; fixed (byte* pi = b) fixed (byte* po = t) n = MediaNative.fc_pub_dave_process_proposals(pi + o, l, po, t.Length); return n > 0 ? t[..n] : []; }
    private static unsafe int ProcessPubDaveCommit(byte[] b, int o, int l) { fixed (byte* p = b) return MediaNative.fc_pub_dave_process_commit(p + o, l); }
    private static unsafe bool ProcessPubDaveWelcome(byte[] b, int o, int l) { fixed (byte* p = b) return MediaNative.fc_pub_dave_process_welcome(p + o, l) != 0; }
}
