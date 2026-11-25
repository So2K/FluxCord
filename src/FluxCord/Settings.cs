using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxCord;

public sealed class FluxSettings
{
    public int WindowWidth { get; set; } = 1360;
    public int WindowHeight { get; set; } = 840;
    public float UiScale { get; set; } = 1.0f;
    public string Theme { get; set; } = "Obsidian";
    public bool MigratedFromDorion { get; set; }

    // Migrated from Dorion / Discord
    public bool PushToTalk { get; set; }
    public string PushToTalkKey { get; set; } = "RControl";
    public bool DesktopNotifications { get; set; }
    public bool StreamerMode { get; set; }
    public bool UnreadBadge { get; set; } = true;
    public bool HardwareAccel { get; set; } = true;
    public bool UiSoundsEnabled { get; set; } = true;
    public float UiSoundsVolume { get; set; } = 0.75f;
    public bool ShowMemberList { get; set; } = true;
    public string PresenceStatus { get; set; } = "online"; // online, idle, dnd, invisible
    public string PreferredRtcRegion { get; set; } = ""; // empty = Auto-Fastest benchmarked region
    public bool TurboLowLatency { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoDownloadUpdates { get; set; } = true;

    // Voice & Multi-Backend Noise Suppression
    public string CaptureDeviceId { get; set; } = "";
    public string RenderDeviceId { get; set; } = "";
    public int NoiseMode { get; set; } = (int)FluxCord.NoiseMode.AutoBest;
    public float NoiseStrength { get; set; } = 0.88f;
    public bool HpfEnabled { get; set; } = true;
    public bool AgcEnabled { get; set; } = true;
    public float GateDb { get; set; } = -46.0f;
    public float InputGain { get; set; } = 1.0f;
    public float OutputVolume { get; set; } = 1.0f;

    // 4K Zero-Copy Streaming (NVENC)
    public int StreamMonitor { get; set; } = 0;
    public int StreamWidth { get; set; } = 1920;
    public int StreamHeight { get; set; } = 1080;
    public int StreamFps { get; set; } = 60;
    public int StreamBitrateKbps { get; set; } = 16000;
    public int StreamCodec { get; set; } = (int)VideoCodec.H264;
    public bool StreamOptimizeDetail { get; set; } = true;
    public bool StreamCaptureAudio { get; set; } = true;

    // DPAPI-encrypted Discord token (base64)
    public string EncryptedTokenB64 { get; set; } = "";
    public string LastGuildId { get; set; } = "";
    public string LastChannelId { get; set; } = "";
}

[JsonSerializable(typeof(FluxSettings))]
[JsonSerializable(typeof(ResumeSession))]
internal partial class FluxJsonContext : JsonSerializerContext { }

public static class SettingsStore
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxCord");
    public static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");
    public static readonly string LogPath = Path.Combine(DataDir, "fluxcord.log");

    public static FluxSettings Current { get; private set; } = new();
    public static string MigrationSummary { get; private set; } = "";

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    public static void Load()
    {
        Directory.CreateDirectory(DataDir);
        if (File.Exists(SettingsPath))
        {
            try
            {
                var json = File.ReadAllText(SettingsPath);
                Current = JsonSerializer.Deserialize(json, FluxJsonContext.Default.FluxSettings) ?? new();
            }
            catch { Current = new(); }
        }

        if (!Current.MigratedFromDorion)
        {
            MigrateFromExistingClients();
            Save();
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(Current, FluxJsonContext.Default.FluxSettings);
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    public static string GetToken()
    {
        if (string.IsNullOrEmpty(Current.EncryptedTokenB64)) return "";
        try
        {
            var enc = Convert.FromBase64String(Current.EncryptedTokenB64);
            var plain = W.UnprotectDpapi(enc);
            return plain.Length > 0 ? Encoding.UTF8.GetString(plain) : "";
        }
        catch { return ""; }
    }

    public static void SetToken(string token)
    {
        token = token.Trim().Trim('"');
        if (string.IsNullOrEmpty(token))
        {
            Current.EncryptedTokenB64 = "";
        }
        else
        {
            var enc = W.ProtectDpapi(Encoding.UTF8.GetBytes(token));
            Current.EncryptedTokenB64 = Convert.ToBase64String(enc);
        }
        Save();
    }

    public static void MigrateFromExistingClients()
    {
        var imported = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // 1. Migrate Dorion config.json
        var dorionCfg = Path.Combine(appData, "dorion", "config.json");
        if (File.Exists(dorionCfg))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(dorionCfg));
                var r = doc.RootElement;
                if (r.TryGetProperty("push_to_talk", out var ptt) && ptt.ValueKind == JsonValueKind.True) Current.PushToTalk = true;
                if (r.TryGetProperty("push_to_talk_keys", out var pttk) && pttk.ValueKind == JsonValueKind.Array && pttk.GetArrayLength() > 0)
                    Current.PushToTalkKey = pttk[0].GetString() ?? "RControl";
                if (r.TryGetProperty("desktop_notifications", out var dn)) Current.DesktopNotifications = dn.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("streamer_mode_detection", out var sm)) Current.StreamerMode = sm.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("unread_badge", out var ub)) Current.UnreadBadge = ub.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("zoom", out var zm) && float.TryParse(zm.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var z))
                    Current.UiScale = Math.Clamp(z, 0.8f, 1.5f);
                imported.Add("Dorion (PTT, уведомления, масштаб, значки)");
            }
            catch { }
        }

        // 2. Migrate official Discord settings.json if present
        var discordCfg = Path.Combine(appData, "discord", "settings.json");
        if (File.Exists(discordCfg))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(discordCfg));
                var r = doc.RootElement;
                if (r.TryGetProperty("WINDOW_BOUNDS", out var wb))
                {
                    if (wb.TryGetProperty("width", out var w)) Current.WindowWidth = Math.Clamp(w.GetInt32(), 960, 2560);
                    if (wb.TryGetProperty("height", out var h)) Current.WindowHeight = Math.Clamp(h.GetInt32(), 640, 1440);
                }
                imported.Add("Discord Desktop (геометрия окна)");
            }
            catch { }
        }

        Current.MigratedFromDorion = true;
        MigrationSummary = imported.Count > 0
            ? "Перенесено: " + string.Join(", ", imported)
            : "Создан чистый профиль FluxCord";
        Log("Settings migration: " + MigrationSummary);
    }
}
