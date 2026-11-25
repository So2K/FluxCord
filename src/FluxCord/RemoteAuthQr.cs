using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluxCord;

/// <summary>
/// Discord Remote Auth Gateway v2 (QR Code Login).
/// Connects to wss://remote-auth-gateway.discord.gg/?v=2, performs RSA-2048 OAEP-SHA256 handshake,
/// generates https://discord.com/ra/&lt;fingerprint&gt; QR matrix, and exchanges ticket for Discord token.
/// </summary>
public sealed class RemoteAuthQr
{
    public static readonly RemoteAuthQr Instance = new();

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private CancellationTokenSource? _cts;
    private RSA? _rsa;
    private ClientWebSocket? _ws;

    public bool[,]? Matrix { get; private set; }
    public string Status { get; private set; } = "Генерация QR-кода...";
    public string ScannedUser { get; private set; } = "";
    public bool IsExpired { get; private set; }
    public bool IsRunning { get; private set; }
    public long ExpiresAtTick { get; private set; }

    public event Action? Changed;

    private RemoteAuthQr()
    {
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://discord.com");
    }

    private void Notify() => Changed?.Invoke();

    public void Start()
    {
        Stop();
        IsRunning = true;
        IsExpired = false;
        ScannedUser = "";
        Matrix = null;
        Status = "Подключение к Discord...";
        ExpiresAtTick = 0;
        Notify();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _ = Task.Run(() => RunAsync(ct), ct);
    }

    public void Stop()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        try { _rsa?.Dispose(); } catch { }
        _ws = null;
        _rsa = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            _rsa = RSA.Create(2048);
            string pubKeyB64 = Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());

            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("Origin", "https://discord.com");
            _ws.Options.SetRequestHeader("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            await _ws.ConnectAsync(new Uri("wss://remote-auth-gateway.discord.gg/?v=2"), ct);

            var buf = new byte[16384];
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var res = await _ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    if (!DiscordClient.Instance.IsConnected)
                    {
                        IsExpired = true;
                        Status = "QR-код истёк — нажми «Обновить»";
                        Notify();
                    }
                    break;
                }

                using var doc = JsonDocument.Parse(buf.AsMemory(0, res.Count));
                var root = doc.RootElement;
                string op = root.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "" : "";

                switch (op)
                {
                    case "hello":
                    {
                        int hbMs = root.TryGetProperty("heartbeat_interval", out var hb) ? hb.GetInt32() : 41250;
                        int timeoutMs = root.TryGetProperty("timeout_ms", out var tm) ? tm.GetInt32() : 120000;
                        ExpiresAtTick = Environment.TickCount64 + timeoutMs;
                        _ = HeartbeatLoopAsync(hbMs, ct);
                        _ = CountdownTickAsync(ct);
                        await SendJsonAsync($"{{\"op\":\"init\",\"encoded_public_key\":\"{pubKeyB64}\"}}", ct);
                        break;
                    }
                    case "nonce_proof":
                    {
                        string encNonce = root.GetProperty("encrypted_nonce").GetString() ?? "";
                        byte[] dec = _rsa.Decrypt(Convert.FromBase64String(encNonce), RSAEncryptionPadding.OaepSHA256);
                        byte[] hash = SHA256.HashData(dec);
                        string proof = Base64Url(hash);
                        await SendJsonAsync($"{{\"op\":\"nonce_proof\",\"proof\":\"{proof}\"}}", ct);
                        break;
                    }
                    case "pending_remote_init":
                    {
                        string fp = root.GetProperty("fingerprint").GetString() ?? "";
                        string url = "https://discord.com/ra/" + fp;
                        Matrix = QrCode.Encode(url);
                        Status = "Отсканируй в Discord на телефоне";
                        SettingsStore.Log("QR ready: fingerprint=" + fp);
                        Notify();
                        break;
                    }
                    case "pending_ticket":
                    case "pending_finish":
                    {
                        if (root.TryGetProperty("encrypted_user_payload", out var eup) && eup.ValueKind == JsonValueKind.String)
                        {
                            byte[] dec = _rsa.Decrypt(Convert.FromBase64String(eup.GetString()!), RSAEncryptionPadding.OaepSHA256);
                            string payload = Encoding.UTF8.GetString(dec); // user_id:discrim:avatar:username
                            var parts = payload.Split(':');
                            ScannedUser = parts.Length >= 4 ? parts[3] : "аккаунт";
                        }
                        Status = $"Отсканировано: {ScannedUser} — подтверди на телефоне!";
                        Notify();
                        break;
                    }
                    case "pending_login":
                    {
                        string ticket = root.GetProperty("ticket").GetString() ?? "";
                        Status = "Получение токена...";
                        Notify();
                        string token = await ExchangeTicketAsync(ticket, ct);
                        if (!string.IsNullOrEmpty(token))
                        {
                            Status = "Успешно! Подключаемся...";
                            Notify();
                            Stop();
                            _ = DiscordClient.Instance.ConnectAsync(token);
                            return;
                        }
                        IsExpired = true;
                        Status = "Не удалось завершить вход — обнови QR";
                        Notify();
                        return;
                    }
                    case "finish":
                    {
                        string encTok = root.GetProperty("encrypted_token").GetString() ?? "";
                        byte[] dec = _rsa.Decrypt(Convert.FromBase64String(encTok), RSAEncryptionPadding.OaepSHA256);
                        string token = Encoding.UTF8.GetString(dec);
                        Status = "Успешно! Подключаемся...";
                        Notify();
                        Stop();
                        _ = DiscordClient.Instance.ConnectAsync(token);
                        return;
                    }
                    case "cancel":
                    {
                        IsExpired = true;
                        ScannedUser = "";
                        Status = "Вход отменён — нажми «Обновить»";
                        Notify();
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                IsExpired = true;
                Status = "Ошибка QR: " + ex.Message;
                SettingsStore.Log("QR error: " + ex);
                Notify();
            }
        }
    }

    private async Task<string> ExchangeTicketAsync(string ticket, CancellationToken ct)
    {
        if (_rsa == null) return "";
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://discord.com/api/v9/users/@me/remote-auth/login");
        req.Content = new StringContent($"{{\"ticket\":\"{ticket}\"}}", Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            SettingsStore.Log($"QR ticket exchange HTTP {(int)resp.StatusCode}");
            return "";
        }
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.TryGetProperty("encrypted_token", out var et)) return "";
        byte[] dec = _rsa.Decrypt(Convert.FromBase64String(et.GetString()!), RSAEncryptionPadding.OaepSHA256);
        return Encoding.UTF8.GetString(dec);
    }

    private async Task CountdownTickAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && IsRunning && !IsExpired)
            {
                await Task.Delay(1000, ct);
                Notify();
            }
        }
        catch { }
    }

    private async Task HeartbeatLoopAsync(int intervalMs, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                await Task.Delay(intervalMs, ct);
                await SendJsonAsync("{\"op\":\"heartbeat\"}", ct);
            }
        }
        catch { }
    }

    private Task SendJsonAsync(string json, CancellationToken ct)
    {
        if (_ws == null || _ws.State != WebSocketState.Open) return Task.CompletedTask;
        return _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
