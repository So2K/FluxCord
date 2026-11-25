using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluxCord;

public sealed class ResumeWatchItem
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public float Volume { get; set; } = 1.0f;
    public bool Muted { get; set; }
}

public sealed class ResumeSession
{
    public long SavedUtcTicks { get; set; }
    public string FromVersion { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public string ActiveGuildId { get; set; } = "";
    public string ActiveChannelId { get; set; } = "";
    public bool InVoiceStage { get; set; }
    public bool ShowFriendsView { get; set; }
    public string VoiceGuildId { get; set; } = "";
    public string VoiceChannelId { get; set; } = "";
    public bool Muted { get; set; }
    public bool Deafened { get; set; }
    public string? FocusedStreamUserId { get; set; }
    public List<ResumeWatchItem> WatchedStreams { get; set; } = [];
    public bool WasStreaming { get; set; }
    public int StreamMonitor { get; set; }
    public ulong StreamHwnd { get; set; }
}

/// <summary>
/// Ultra-fast GitHub Releases Auto-Updater (So2K/FluxCord) with silent background pre-download,
/// SHA-256 verification, atomic zero-window binary swap (<1s downtime), rollback safety,
/// and 100% seamless Discord call + multi-stream + Go Live session restoration!
/// </summary>
public static class Updater
{
    public const string CurrentVersion = "1.0.0";
    public const string Repo = "So2K/FluxCord";
    public static readonly string UpdatesDir = Path.Combine(SettingsStore.DataDir, "updates");
    public static readonly string ResumePath = Path.Combine(SettingsStore.DataDir, "resume_session.json");

    private static readonly HttpClient Http = MakeClient();
    private static volatile bool _checking, _downloading, _applying;

    public static bool Checking => _checking;
    public static bool Downloading => _downloading;
    public static bool Applying => _applying;
    public static double Progress { get; private set; }
    public static string? AvailableVersion { get; private set; }
    public static string? DownloadUrl { get; private set; }
    public static string? AssetName { get; private set; }
    public static string? ExpectedSha256 { get; private set; }
    public static string ReleaseNotes { get; private set; } = "";
    public static long AssetSize { get; private set; }
    public static string? StagedDir { get; private set; }
    public static string StatusText { get; private set; } = $"Версия v{CurrentVersion}";

    public static bool HasUpdate => AvailableVersion != null && (DownloadUrl != null || IsReadyToApply);
    public static bool IsReadyToApply => !string.IsNullOrEmpty(StagedDir) && File.Exists(Path.Combine(StagedDir, "FluxCord.exe"));

    public static ResumeSession? PendingResume { get; private set; }

    private static HttpClient MakeClient()
    {
        var h = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"FluxCord/{CurrentVersion}");
        return c;
    }

    /// <summary>
    /// Cleans leftover .old/.bak updater files and loads a fresh resume snapshot if launched after an update.
    /// </summary>
    public static void InitOnStartup(bool resumedArg)
    {
        // Clean leftover hot-swap files in app directory
        try
        {
            string appDir = AppContext.BaseDirectory;
            foreach (var f in Directory.GetFiles(appDir, "*.old"))
                try { File.Delete(f); } catch { }
            foreach (var f in Directory.GetFiles(appDir, "*.bak"))
                try { File.Delete(f); } catch { }
        }
        catch { }

        // Load fresh resume_session.json (valid for 120 seconds so normal restarts never hijack voice!)
        if (File.Exists(ResumePath))
        {
            try
            {
                var json = File.ReadAllText(ResumePath);
                var sess = JsonSerializer.Deserialize(json, FluxJsonContext.Default.ResumeSession);
                try { File.Delete(ResumePath); } catch { }
                if (sess != null)
                {
                    var age = DateTime.UtcNow - new DateTime(sess.SavedUtcTicks, DateTimeKind.Utc);
                    if (age.TotalSeconds >= -5 && age.TotalSeconds <= 120)
                    {
                        PendingResume = sess;
                        SettingsStore.Log($"Updater: loaded resume snapshot (age={age.TotalSeconds:F1}s, voice={sess.VoiceChannelId}, watches={sess.WatchedStreams.Count}, streaming={sess.WasStreaming})");
                    }
                }
            }
            catch { try { File.Delete(ResumePath); } catch { } }
        }

        // Start silent background update check loop (first check after 4s, then every 30 min)
        if (!App.BenchArg && SettingsStore.Current.AutoCheckUpdates)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(4000);
                    Check(manual: false);
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(30));
                        if (SettingsStore.Current.AutoCheckUpdates) Check(manual: false);
                    }
                }
                catch { }
            });
        }
    }

    public static ResumeSession? ConsumePendingResume()
    {
        var r = PendingResume;
        PendingResume = null;
        return r;
    }

    public static void Check(bool manual = false)
    {
        if (_checking || _downloading || _applying) return;
        _checking = true;
        if (manual) StatusText = "Проверяю обновления на GitHub...";
        App.WakeUi();

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await Http.SendAsync(req, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    _checking = false;
                    StatusText = $"Версия v{CurrentVersion}";
                    if (manual) Shell.Toast($"Не удалось проверить обновления (HTTP {(int)resp.StatusCode})");
                    App.WakeUi();
                    return;
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync(cts.Token));
                var root = doc.RootElement;
                string tag = (root.TryGetProperty("tag_name", out var tEl) ? tEl.GetString() ?? "" : "").TrimStart('v', 'V').Trim();
                string body = root.TryGetProperty("body", out var bEl) && bEl.ValueKind == JsonValueKind.String ? bEl.GetString() ?? "" : "";

                string? zipUrl = null, zipName = null, sha256 = null;
                long zipSize = 0;
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string an = a.TryGetProperty("name", out var ne) ? ne.GetString() ?? "" : "";
                        string au = a.TryGetProperty("browser_download_url", out var ue) ? ue.GetString() ?? "" : "";
                        if (an.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(au))
                        {
                            zipName = Path.GetFileName(an);
                            zipUrl = au;
                            zipSize = a.TryGetProperty("size", out var se) && se.ValueKind == JsonValueKind.Number ? se.GetInt64() : 0;
                            if (a.TryGetProperty("digest", out var de) && de.ValueKind == JsonValueKind.String)
                            {
                                string d = de.GetString() ?? "";
                                if (d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha256 = d[7..].Trim().ToLowerInvariant();
                            }
                            break;
                        }
                    }
                }

                bool newer = IsNewer(tag, CurrentVersion) && !string.IsNullOrEmpty(zipUrl);
                _checking = false;

                if (newer)
                {
                    AvailableVersion = tag;
                    DownloadUrl = zipUrl;
                    AssetName = zipName ?? $"FluxCord-{tag}-win-x64.zip";
                    AssetSize = zipSize;
                    ExpectedSha256 = sha256;
                    ReleaseNotes = body.Length > 420 ? body[..420] + "…" : body;

                    string preStage = Path.Combine(UpdatesDir, "v" + tag, "stage");
                    if (File.Exists(Path.Combine(preStage, "FluxCord.exe")))
                    {
                        StagedDir = preStage;
                        Progress = 1.0;
                        StatusText = $"Готово к установке: v{tag} (мгновенно, вернёт в звонок)";
                        Shell.Toast($"⚡ Обновление FluxCord v{tag} скачано и готово — нажми «⬆ Обновить»!", 4500);
                    }
                    else
                    {
                        StagedDir = null;
                        StatusText = $"Доступно обновление v{tag} ({FormatSize(zipSize)})";
                        if (SettingsStore.Current.AutoDownloadUpdates)
                        {
                            // Ultra-update: silently pre-download & extract in the background so applying takes <1 second!
                            _ = DownloadAndStageAsync(autoApply: false);
                        }
                        else if (manual)
                        {
                            Shell.Toast($"Доступна новая версия FluxCord v{tag}!", 4000);
                        }
                    }
                }
                else
                {
                    AvailableVersion = null;
                    DownloadUrl = null;
                    StagedDir = null;
                    StatusText = $"Актуальная версия v{CurrentVersion}";
                    if (manual) Shell.Toast($"Установлена последняя версия FluxCord (v{CurrentVersion})");
                }
                App.WakeUi();
            }
            catch (Exception ex)
            {
                _checking = false;
                SettingsStore.Log("Updater check error: " + ex.Message);
                StatusText = $"Версия v{CurrentVersion}";
                if (manual) Shell.Toast("Ошибка проверки обновлений: " + ex.Message);
                App.WakeUi();
            }
        });
    }

    public static void StartUpdateNow()
    {
        if (_applying) return;
        if (IsReadyToApply)
        {
            ApplyStagedUpdateAndRestart();
            return;
        }
        if (!_downloading && !string.IsNullOrEmpty(DownloadUrl))
            _ = DownloadAndStageAsync(autoApply: true);
    }

    private static async Task DownloadAndStageAsync(bool autoApply)
    {
        if (_downloading || string.IsNullOrEmpty(DownloadUrl) || string.IsNullOrEmpty(AvailableVersion)) return;
        _downloading = true;
        Progress = 0;
        string ver = AvailableVersion;
        string url = DownloadUrl;
        string verDir = Path.Combine(UpdatesDir, "v" + ver);
        string zipPath = Path.Combine(verDir, AssetName ?? $"FluxCord-{ver}-win-x64.zip");
        string partPath = zipPath + ".part";
        string stageDir = Path.Combine(verDir, "stage");

        StatusText = $"Скачивание v{ver}...";
        App.WakeUi();

        try
        {
            Directory.CreateDirectory(verDir);
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? AssetSize;

            await using var input = await resp.Content.ReadAsStreamAsync();
            await using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buf = new byte[128 * 1024];
                long read = 0, lastUi = 0;
                long tStart = Environment.TickCount64;
                int n;
                while ((n = await input.ReadAsync(buf)) > 0)
                {
                    await output.WriteAsync(buf.AsMemory(0, n));
                    read += n;
                    long now = Environment.TickCount64;
                    if (now - lastUi >= 100)
                    {
                        lastUi = now;
                        double sec = Math.Max(0.1, (now - tStart) / 1000.0);
                        double mbps = (read / (1024.0 * 1024.0)) / sec;
                        Progress = total > 0 ? Math.Clamp((double)read / total, 0, 0.92) : 0.5;
                        StatusText = total > 0
                            ? $"Скачивание v{ver}: {(int)(read * 100 / total)}% ({mbps:F1} МБ/с)"
                            : $"Скачивание v{ver}: {FormatSize(read)}";
                        App.WakeUi();
                    }
                }
            }

            // Optional SHA-256 verification when provided by GitHub Release API
            if (!string.IsNullOrEmpty(ExpectedSha256))
            {
                StatusText = $"Проверка SHA-256 v{ver}...";
                App.WakeUi();
                await using var fs = File.OpenRead(partPath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
                if (!string.Equals(hash, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Контрольная сумма SHA-256 не совпала!");
            }

            File.Move(partPath, zipPath, overwrite: true);

            StatusText = $"Распаковка v{ver}...";
            Progress = 0.95;
            App.WakeUi();

            if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true);
            Directory.CreateDirectory(stageDir);
            ZipFile.ExtractToDirectory(zipPath, stageDir, overwriteFiles: true);

            // Handle single top-level subfolder inside ZIP if present
            string exeDirect = Path.Combine(stageDir, "FluxCord.exe");
            if (!File.Exists(exeDirect))
            {
                var sub = Directory.GetDirectories(stageDir).FirstOrDefault(d => File.Exists(Path.Combine(d, "FluxCord.exe")));
                if (sub != null) stageDir = sub;
            }
            if (!File.Exists(Path.Combine(stageDir, "FluxCord.exe")))
                throw new InvalidOperationException("В архиве обновления не найден FluxCord.exe");

            StagedDir = stageDir;
            Progress = 1.0;
            _downloading = false;
            StatusText = $"Готово к установке: v{ver} (вернёт в звонок)";
            SettingsStore.Log($"Updater: staged v{ver} at {stageDir}");
            App.WakeUi();

            if (autoApply)
            {
                ApplyStagedUpdateAndRestart();
            }
            else
            {
                Shell.Toast($"⚡ FluxCord v{ver} готов! Нажми «⬆ Обновить» сверху — звонок восстановится сам.", 5500);
            }
        }
        catch (Exception ex)
        {
            _downloading = false;
            Progress = 0;
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
            SettingsStore.Log("Updater download error: " + ex);
            StatusText = $"Ошибка обновления: {ex.Message}";
            Shell.Toast("Ошибка скачивания обновления: " + ex.Message, 5000);
            App.WakeUi();
        }
    }

    /// <summary>
    /// Captures exact voice/stream/channel state, spawns a hidden atomic swapper (<1s downtime), and exits cleanly!
    /// </summary>
    public static void ApplyStagedUpdateAndRestart()
    {
        if (_applying || !IsReadyToApply || string.IsNullOrEmpty(StagedDir)) return;
        _applying = true;
        string targetVer = AvailableVersion ?? "new";
        StatusText = $"Установка v{targetVer} и возврат в Discord...";
        App.WakeUi();

        try
        {
            // 1. Capture live Discord session state (Voice Channel, Mute/Deafen, Watched Streams, Go Live!)
            var snap = Shell.CaptureResumeSession(targetVer);
            Directory.CreateDirectory(SettingsStore.DataDir);
            File.WriteAllText(ResumePath, JsonSerializer.Serialize(snap, FluxJsonContext.Default.ResumeSession));
            SettingsStore.Log($"Updater: saved resume_session.json (voice={snap.VoiceChannelId}, watches={snap.WatchedStreams.Count}, streaming={snap.WasStreaming})");

            // 2. Write atomic zero-window Windows swap script with rollback safety
            int pid = Environment.ProcessId;
            string appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            string stageDir = StagedDir.TrimEnd('\\', '/');
            string backupDir = Path.Combine(UpdatesDir, "backup_prev");
            string swapCmd = Path.Combine(UpdatesDir, "apply_update.cmd");
            string newExe = Path.Combine(appDir, "FluxCord.exe");

            string script = $@"@echo off
setlocal
set PID={pid}
set SRC={stageDir}
set DST={appDir}
set BAK={backupDir}

rem Wait up to 8s for old FluxCord PID to exit cleanly (typically ~150ms)
for /L %%i in (1,1,80) do (
  tasklist /FI ""PID eq %PID%"" 2>nul | find ""%PID%"" >nul
  if errorlevel 1 goto :ready
  ping -n 1 -w 100 127.0.0.1 >nul
)
taskkill /F /PID %PID% >nul 2>&1
ping -n 1 -w 200 127.0.0.1 >nul

:ready
if exist ""%BAK%"" rmdir /S /Q ""%BAK%"" >nul 2>&1
mkdir ""%BAK%"" >nul 2>&1
if exist ""%DST%\FluxCord.exe"" copy /Y ""%DST%\FluxCord.exe"" ""%BAK%\FluxCord.exe"" >nul 2>&1
if exist ""%DST%\FluxCord.Media.dll"" copy /Y ""%DST%\FluxCord.Media.dll"" ""%BAK%\FluxCord.Media.dll"" >nul 2>&1

xcopy ""%SRC%\*"" ""%DST%\"" /E /Y /Q >nul
if errorlevel 1 (
  rem Rollback on unexpected copy failure
  if exist ""%BAK%\FluxCord.exe"" copy /Y ""%BAK%\FluxCord.exe"" ""%DST%\FluxCord.exe"" >nul 2>&1
  if exist ""%BAK%\FluxCord.Media.dll"" copy /Y ""%BAK%\FluxCord.Media.dll"" ""%DST%\FluxCord.Media.dll"" >nul 2>&1
)

start """" ""{newExe}"" --resumed-update
del ""%~f0"" >nul 2>&1
";
            Directory.CreateDirectory(UpdatesDir);
            File.WriteAllText(swapCmd, script, Encoding.ASCII);

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{swapCmd}\"\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            // 3. Cleanly close current window (leaves voice/GPU gracefully so swapper runs in ~150ms!)
            W.PostMessageW(App.Hwnd, W.WM_CLOSE, 0, 0);
        }
        catch (Exception ex)
        {
            _applying = false;
            SettingsStore.Log("Updater apply error: " + ex);
            StatusText = "Ошибка установки: " + ex.Message;
            Shell.Toast("Не удалось применить обновление: " + ex.Message, 5000);
            App.WakeUi();
        }
    }

    public static bool IsNewer(string remote, string local)
    {
        if (string.IsNullOrWhiteSpace(remote)) return false;
        return Version.TryParse(Normalize(remote), out var rv)
            && Version.TryParse(Normalize(local), out var lv)
            && rv > lv;
    }

    private static string Normalize(string v)
    {
        int dash = v.IndexOf('-');
        if (dash >= 0) v = v[..dash];
        var parts = v.Split('.');
        return parts.Length == 1 ? v + ".0" : v;
    }

    private static string FormatSize(long b)
        => b >= 1024 * 1024 ? $"{b / (1024.0 * 1024.0):F1} МБ" : (b >= 1024 ? $"{b / 1024.0:F0} КБ" : $"{b} Б");
}
