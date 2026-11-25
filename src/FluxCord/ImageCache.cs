using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.WIC;
using D2DAlpha = Vortice.DCommon.AlphaMode;

namespace FluxCord;

public enum ImgMask { None = 0, Circle = 1, Squircle = 2 }

/// <summary>
/// Fast native WIC image decoder + disk cache + Direct2D VRAM bitmap cache (FluxTube-style).
/// Downloads Discord server icons, user avatars, and chat images asynchronously without blocking UI.
/// Bakes anti-aliased circular / squircle alpha masks once on decode so rendering is a single GPU blit.
/// </summary>
public static class ImageCache
{
    private sealed class DecodedImg
    {
        public byte[] Pixels = [];
        public int W, H;
    }

    private static readonly string CacheDir = Path.Combine(SettingsStore.DataDir, "cache", "img");
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 12,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        EnableMultipleHttp2Connections = true,
    })
    { Timeout = TimeSpan.FromSeconds(12) };

    private static readonly ConcurrentDictionary<string, ID2D1Bitmap1> _gpu = new();
    private static readonly ConcurrentDictionary<string, DecodedImg> _ready = new();
    private static readonly ConcurrentDictionary<string, byte> _inflight = new();
    private static readonly ConcurrentDictionary<string, uint> _dominant = new();
    private static readonly SemaphoreSlim _sem = new(6);

    public static uint GetCardColor(string? url, string fallbackKey)
    {
        if (!string.IsNullOrEmpty(url) && _dominant.TryGetValue(url, out var col)) return col;
        uint h = (uint)fallbackKey.GetHashCode();
        uint[] palette = [0xFF15171E, 0xFF1B191C, 0xFF161A22, 0xFF1D1820, 0xFF161D1A];
        return palette[h % (uint)palette.Length];
    }

    static ImageCache()
    {
        Directory.CreateDirectory(CacheDir);
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public static unsafe ID2D1Bitmap1? Get(string? url, int maxDim = 96, ImgMask mask = ImgMask.None)
    {
        if (string.IsNullOrEmpty(url)) return null;
        string key = $"{url}|{maxDim}|{(int)mask}";

        if (_gpu.TryGetValue(key, out var bmp)) return bmp;

        if (_ready.TryRemove(key, out var dec))
        {
            fixed (byte* p = dec.Pixels)
            {
                var created = Gfx.DC.CreateBitmap(
                    new Vortice.Mathematics.SizeI(dec.W, dec.H),
                    (nint)p,
                    (uint)(dec.W * 4),
                    new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
                _gpu[key] = created;
                return created;
            }
        }

        if (_inflight.TryAdd(key, 1))
        {
            _ = Task.Run(() => FetchAndDecodeAsync(url, key, maxDim, mask));
        }
        return null;
    }

    /// <summary>
    /// Synchronously decodes a local bundled asset (e.g. Assets\fluxcord.png) via WIC directly into GPU VRAM.
    /// </summary>
    public static unsafe ID2D1Bitmap1? GetLocal(string filePath, int maxDim = 96, ImgMask mask = ImgMask.None)
    {
        if (string.IsNullOrEmpty(filePath) || Gfx.DC == null) return null;
        string key = $"local:{filePath}|{maxDim}|{(int)mask}";
        if (_gpu.TryGetValue(key, out var bmp)) return bmp;
        if (!File.Exists(filePath)) return null;
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var dec = DecodeWic(bytes, maxDim, mask, key);
            if (dec == null) return null;
            fixed (byte* p = dec.Pixels)
            {
                var created = Gfx.DC.CreateBitmap(
                    new Vortice.Mathematics.SizeI(dec.W, dec.H),
                    (nint)p,
                    (uint)(dec.W * 4),
                    new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
                _gpu[key] = created;
                return created;
            }
        }
        catch { return null; }
    }

    private static async Task FetchAndDecodeAsync(string url, string key, int maxDim, ImgMask mask)
    {
        await _sem.WaitAsync();
        try
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24];
            string diskPath = Path.Combine(CacheDir, hash + ".bin");

            byte[]? bytes = null;
            if (File.Exists(diskPath))
            {
                bytes = await File.ReadAllBytesAsync(diskPath);
            }
            else
            {
                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return;
                bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length > 0 && bytes.Length < 8 * 1024 * 1024)
                    try { await File.WriteAllBytesAsync(diskPath, bytes); } catch { }
            }

            if (bytes == null || bytes.Length == 0) return;

            var dec = DecodeWic(bytes, maxDim, mask, url);
            if (dec != null)
            {
                _ready[key] = dec;
                App.WakeUi();
            }
        }
        catch { }
        finally
        {
            _sem.Release();
        }
    }

    private static DecodedImg? DecodeWic(byte[] raw, int maxDim, ImgMask mask, string url)
    {
        try
        {
            using var fac = new IWICImagingFactory();
            using var stream = fac.CreateStream(raw);
            using var decoder = fac.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
            using var frame = decoder.GetFrame(0);

            int srcW = (int)frame.Size.Width;
            int srcH = (int)frame.Size.Height;
            if (srcW <= 0 || srcH <= 0) return null;

            int dstW = srcW, dstH = srcH;
            if (maxDim > 0 && (srcW > maxDim || srcH > maxDim))
            {
                float scale = MathF.Min((float)maxDim / srcW, (float)maxDim / srcH);
                dstW = Math.Max(1, (int)MathF.Round(srcW * scale));
                dstH = Math.Max(1, (int)MathF.Round(srcH * scale));
            }

            IWICBitmapSource src = frame;
            IWICBitmapScaler? scaler = null;
            if (dstW != srcW || dstH != srcH)
            {
                scaler = fac.CreateBitmapScaler();
                scaler.Initialize(frame, (uint)dstW, (uint)dstH, Vortice.WIC.BitmapInterpolationMode.Fant);
                src = scaler;
            }

            using var conv = fac.CreateFormatConverter();
            conv.Initialize(src, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);

            byte[] pixels = new byte[dstW * dstH * 4];
            conv.CopyPixels((uint)(dstW * 4), pixels);
            scaler?.Dispose();

            // Sample soft Discord-style card tint from avatar pixels before circular masking
            if (mask == ImgMask.Circle && !_dominant.ContainsKey(url))
            {
                long sr = 0, sg = 0, sb = 0, cnt = 0;
                int step = Math.Max(1, (dstW * dstH) / 256);
                for (int i = 0; i < dstW * dstH; i += step)
                {
                    int o = i * 4;
                    if (pixels[o + 3] < 128) continue;
                    sb += pixels[o]; sg += pixels[o + 1]; sr += pixels[o + 2]; cnt++;
                }
                if (cnt > 0)
                {
                    // Rich muted tone (dark avatars stay crisp dark #111216, warm/pastel avatars get soft GoofCord card tint!)
                    int r = (int)Math.Clamp((sr / cnt) * 0.58 + 10, 14, 158);
                    int g = (int)Math.Clamp((sg / cnt) * 0.58 + 10, 14, 148);
                    int b = (int)Math.Clamp((sb / cnt) * 0.58 + 12, 16, 158);
                    _dominant[url] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
                }
            }

            if (mask != ImgMask.None)
                ApplyRoundedMask(pixels, dstW, dstH, mask == ImgMask.Circle ? MathF.Min(dstW, dstH) * 0.5f : MathF.Min(dstW, dstH) * 0.30f);

            return new DecodedImg { Pixels = pixels, W = dstW, H = dstH };
        }
        catch { return null; }
    }

    private static void ApplyRoundedMask(byte[] px, int w, int h, float radius)
    {
        float cx = w * 0.5f, cy = h * 0.5f;
        float bx = cx - radius, by = cy - radius;
        for (int y = 0; y < h; y++)
        {
            float dy = MathF.Max(MathF.Abs(y + 0.5f - cy) - by, 0f);
            for (int x = 0; x < w; x++)
            {
                float dx = MathF.Max(MathF.Abs(x + 0.5f - cx) - bx, 0f);
                float dist = MathF.Sqrt(dx * dx + dy * dy) - radius;
                if (dist > 0.5f)
                {
                    int o = (y * w + x) * 4;
                    px[o] = px[o + 1] = px[o + 2] = px[o + 3] = 0;
                }
                else if (dist > -0.5f)
                {
                    float a = Math.Clamp(0.5f - dist, 0f, 1f);
                    int o = (y * w + x) * 4;
                    px[o] = (byte)(px[o] * a);
                    px[o + 1] = (byte)(px[o + 1] * a);
                    px[o + 2] = (byte)(px[o + 2] * a);
                    px[o + 3] = (byte)(px[o + 3] * a);
                }
            }
        }
    }

    public static unsafe ID2D1Bitmap1? CreateBitmapFromBgra(byte[] bgra, int w, int h)
    {
        if (bgra.Length < w * h * 4 || w <= 0 || h <= 0 || Gfx.DC == null) return null;
        fixed (byte* p = bgra)
        {
            return Gfx.DC.CreateBitmap(
                new Vortice.Mathematics.SizeI(w, h),
                (nint)p,
                (uint)(w * 4),
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
        }
    }

    public static void ClearGpu()
    {
        foreach (var b in _gpu.Values) b.Dispose();
        _gpu.Clear();
    }
}
