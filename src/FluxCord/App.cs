using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FluxCord;

public static unsafe class App
{
    public const int MaxWatchSlots = 4;
    public static nint Hwnd { get; private set; }
    public static readonly nint[] StreamHwnds = new nint[MaxWatchSlots];
    public static nint SelfStreamHwnd { get; private set; }
    public static nint StreamHwnd => StreamHwnds[0];
    public static bool DemoArg { get; private set; }
    public static bool BenchArg { get; private set; }
    public static bool ResumedUpdateArg { get; private set; }

    private static nint _chatEdit, _tokenEdit, _searchEdit, _editFont, _editBrush, _blackBrush;
    private static nint _origChatProc, _origTokenProc, _origSearchProc;
    private static bool _searchWasVisible;
    private static nint _arrow, _hand;
    private static bool _wakePending, _tracking;
    private const uint WM_WAKE = W.WM_APP + 1;

    // Pixel bounds of each native video child HWND (0..3 = watch slots, 4 = self preview) for mouse forwarding & cutout regions
    private static readonly (int X, int Y, int W, int H, bool Visible, bool HadCutout)[] _videoRects = new (int, int, int, int, bool, bool)[MaxWatchSlots + 1];

    public static void WakeUi()
    {
        if (Hwnd == 0) return;
        if (Interlocked.Exchange(ref _wakePending, true)) return;
        W.PostMessageW(Hwnd, WM_WAKE, 0, 0);
    }

    public static void Invalidate() { if (Hwnd != 0) W.InvalidateRect(Hwnd, null, 0); }

    [STAThread]
    private static int Main(string[] args)
    {
        DemoArg = args.Contains("--demo");
        BenchArg = args.Contains("--bench");
        ResumedUpdateArg = args.Contains("--resumed-update");
        SettingsStore.Load();
        Updater.InitOnStartup(ResumedUpdateArg);

        try { return Run(); }
        catch (Exception ex) { SettingsStore.Log("FATAL: " + ex); return 1; }
        finally { MediaNative.fc_shutdown(); LocalCache.Close(); }
    }

    private static int Run()
    {
        W.SetPreferredAppMode(2);
        var inst = W.GetModuleHandleW(0);
        _arrow = W.LoadCursorW(0, 32512);
        _hand = W.LoadCursorW(0, 32649);
        nint iconBig = W.LoadImageW(inst, 32512, W.IMAGE_ICON, W.GetSystemMetrics(W.SM_CXICON), W.GetSystemMetrics(W.SM_CYICON), 0);
        nint iconSm = W.LoadImageW(inst, 32512, W.IMAGE_ICON, W.GetSystemMetrics(W.SM_CXSMICON), W.GetSystemMetrics(W.SM_CYSMICON), 0);

        fixed (char* cls = "FluxCordMain")
        fixed (char* scls = "FluxCordStreamHost")
        {
            var wc = new W.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(W.WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = inst,
                hIcon = iconBig,
                hIconSm = iconSm,
                hCursor = _arrow,
                lpszClassName = cls
            };
            W.RegisterClassExW(&wc);

            _blackBrush = W.CreateSolidBrush(0);
            var sc = new W.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(W.WNDCLASSEXW),
                lpfnWndProc = &StreamProc,
                hInstance = inst,
                hCursor = _hand,
                hbrBackground = _blackBrush,
                lpszClassName = scls
            };
            W.RegisterClassExW(&sc);
        }

        var s = SettingsStore.Current;
        var sys = W.GetDpiForSystem() / 96f;
        Hwnd = W.CreateWindowExW(0, "FluxCordMain", "FluxCord", W.WS_OVERLAPPEDWINDOW | W.WS_CLIPCHILDREN,
            unchecked((int)0x80000000), unchecked((int)0x80000000),
            (int)(s.WindowWidth * sys), (int)(s.WindowHeight * sys), 0, 0, inst, 0);

        int dark = 1;
        W.DwmSetWindowAttribute(Hwnd, 20, &dark, 4);
        if (iconBig != 0) W.SendMessageW(Hwnd, W.WM_SETICON, 1, iconBig);
        if (iconSm != 0) W.SendMessageW(Hwnd, W.WM_SETICON, 0, iconSm);
        W.DragAcceptFiles(Hwnd, true);
        W.SetWindowPos(Hwnd, 0, 0, 0, 0, 0, W.SWP_NOMOVE | W.SWP_NOSIZE | W.SWP_NOZORDER | W.SWP_NOACTIVATE | W.SWP_FRAMECHANGED);

        for (int i = 0; i < MaxWatchSlots; i++)
            StreamHwnds[i] = W.CreateWindowExW(0, "FluxCordStreamHost", null, W.WS_CHILD | W.WS_CLIPSIBLINGS, 0, 0, 16, 9, Hwnd, 0, inst, 0);
        SelfStreamHwnd = W.CreateWindowExW(0, "FluxCordStreamHost", null, W.WS_CHILD | W.WS_CLIPSIBLINGS, 0, 0, 16, 9, Hwnd, 0, inst, 0);

        _editFont = W.CreateFontW(-(int)(15 * sys), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _editBrush = W.CreateSolidBrush(W.Rgb(0x17, 0x19, 0x20));

        _chatEdit = W.CreateWindowExW(0, "EDIT", "", W.WS_CHILD | W.ES_AUTOHSCROLL, 0, 0, 100, 30, Hwnd, 0, inst, 0);
        W.SendMessageW(_chatEdit, W.WM_SETFONT, (nuint)_editFont, 1);
        _origChatProc = W.SetWindowLongPtrW(_chatEdit, W.GWLP_WNDPROC, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&ChatEditProc);

        _tokenEdit = W.CreateWindowExW(0, "EDIT", "", W.WS_CHILD | W.ES_AUTOHSCROLL | W.ES_PASSWORD, 0, 0, 100, 30, Hwnd, 0, inst, 0);
        W.SendMessageW(_tokenEdit, W.WM_SETFONT, (nuint)_editFont, 1);
        _origTokenProc = W.SetWindowLongPtrW(_tokenEdit, W.GWLP_WNDPROC, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&TokenEditProc);

        _searchEdit = W.CreateWindowExW(0, "EDIT", "", W.WS_CHILD | W.ES_AUTOHSCROLL, 0, 0, 100, 28, Hwnd, 0, inst, 0);
        W.SendMessageW(_searchEdit, W.WM_SETFONT, (nuint)_editFont, 1);
        _origSearchProc = W.SetWindowLongPtrW(_searchEdit, W.GWLP_WNDPROC, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&SearchEditProc);

        Gfx.Init(Hwnd);
        Shell.Init();

        W.ShowWindow(Hwnd, W.SW_SHOWNORMAL);

        // Low-overhead 100ms background poll timer (only wakes D2D when speaking/telemetry/clock actually changes -> <1% idle CPU!)
        W.SetTimer(Hwnd, 2, 100, 0);
        if (BenchArg) W.SetTimer(Hwnd, 99, 2500, 0);

        W.MSG msg;
        while (W.GetMessageW(&msg, 0, 0, 0) > 0)
        {
            W.TranslateMessage(&msg);
            W.DispatchMessageW(&msg);
        }

        Gfx.DisposeDevice();
        return 0;
    }

    private static string ReadEdit(nint h)
    {
        if (h == 0) return "";
        int len = W.GetWindowTextLengthW(h);
        if (len <= 0) return "";
        var buf = new char[len + 1];
        fixed (char* p = buf) W.GetWindowTextW(h, p, len + 1);
        return new string(buf, 0, len);
    }

    public static string TakeChatInputText()
    {
        string s = ReadEdit(_chatEdit);
        if (_chatEdit != 0) W.SetWindowTextW(_chatEdit, "");
        return s;
    }

    public static void SetChatInputText(string text)
    {
        if (_chatEdit == 0) return;
        W.SetWindowTextW(_chatEdit, text);
        W.SetFocus(_chatEdit);
        W.SendMessageW(_chatEdit, 0x00B1 /*EM_SETSEL*/, (nuint)text.Length, text.Length);
    }

    public static void AppendChatInputText(string suffix)
    {
        string cur = ReadEdit(_chatEdit);
        SetChatInputText(cur + suffix);
    }

    public static string GetTokenInputText() => ReadEdit(_tokenEdit);
    public static string GetSearchInputText() => ReadEdit(_searchEdit);
    public static void SetSearchInputText(string text)
    {
        if (_searchEdit == 0) return;
        W.SetWindowTextW(_searchEdit, text);
    }

    public static void SyncNativeChildren(
        bool showChat, R chatR,
        bool showToken, R tokenR,
        bool showSearch, R searchR,
        ReadOnlySpan<(bool Show, R Rect)> watchSlots,
        bool showSelf, R selfR,
        ReadOnlySpan<R> overlayCutouts)
    {
        float sc = Gfx.Scale;
        SyncChild(_chatEdit, showChat, chatR.Inset(6, 9), sc);
        SyncChild(_tokenEdit, showToken, tokenR.Inset(10, 9), sc);
        SyncChild(_searchEdit, showSearch, searchR.Inset(8, 6), sc);
        if (showSearch && !_searchWasVisible) W.SetFocus(_searchEdit);
        _searchWasVisible = showSearch;

        for (int i = 0; i < MaxWatchSlots; i++)
        {
            bool sh = i < watchSlots.Length && watchSlots[i].Show;
            R sr = sh ? watchSlots[i].Rect : default;
            SyncVideoHost(i, StreamHwnds[i], sh, sr, sc, overlayCutouts, isSelf: false);
        }
        SyncVideoHost(MaxWatchSlots, SelfStreamHwnd, showSelf, selfR, sc, overlayCutouts, isSelf: true);
    }

    private static void SyncVideoHost(int idx, nint hwnd, bool show, R r, float sc, ReadOnlySpan<R> cutouts, bool isSelf)
    {
        if (hwnd == 0) return;
        ref var prev = ref _videoRects[idx];
        if (!show || r.W < 16 || r.H < 16)
        {
            if (prev.Visible)
            {
                W.ShowWindow(hwnd, W.SW_HIDE);
                prev.Visible = false;
            }
            return;
        }

        int px = (int)MathF.Round(r.X * sc);
        int py = (int)MathF.Round(r.Y * sc);
        int pw = Math.Max(16, (int)MathF.Round(r.W * sc));
        int ph = Math.Max(16, (int)MathF.Round(r.H * sc));

        bool movedOrResized = !prev.Visible || prev.X != px || prev.Y != py || prev.W != pw || prev.H != ph;
        if (movedOrResized)
        {
            W.SetWindowPos(hwnd, 0, px, py, pw, ph, W.SWP_NOZORDER | W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW);
            if (isSelf) MediaNative.fc_screen_resize_preview(pw, ph);
            else MediaNative.fc_watch_resize_slot(idx, pw, ph);
            prev.X = px; prev.Y = py; prev.W = pw; prev.H = ph; prev.Visible = true;
        }

        bool hasAnyCutout = false;
        nint rgn = 0;
        for (int c = 0; c < cutouts.Length; c++)
        {
            var cr = cutouts[c];
            int cx1 = (int)MathF.Floor(cr.X * sc) - px;
            int cy1 = (int)MathF.Floor(cr.Y * sc) - py;
            int cx2 = (int)MathF.Ceiling(cr.Right * sc) - px;
            int cy2 = (int)MathF.Ceiling(cr.Bottom * sc) - py;
            if (cx2 <= 0 || cy2 <= 0 || cx1 >= pw || cy1 >= ph) continue;

            if (rgn == 0) rgn = W.CreateRectRgn(0, 0, pw, ph);
            nint hole = W.CreateRectRgn(Math.Max(0, cx1), Math.Max(0, cy1), Math.Min(pw, cx2), Math.Min(ph, cy2));
            W.CombineRgn(rgn, rgn, hole, 4); // RGN_DIFF = 4
            W.DeleteObject(hole);
            hasAnyCutout = true;
        }

        if (hasAnyCutout)
        {
            W.SetWindowRgn(hwnd, rgn, true);
            prev.HadCutout = true;
        }
        else if (prev.HadCutout)
        {
            W.SetWindowRgn(hwnd, 0, true);
            prev.HadCutout = false;
        }
    }

    private static void SyncChild(nint h, bool show, R r, float sc)
    {
        if (h == 0) return;
        if (show)
            W.SetWindowPos(h, 0, (int)(r.X * sc), (int)(r.Y * sc), Math.Max(10, (int)(r.W * sc)), Math.Max(16, (int)(r.H * sc)), W.SWP_NOZORDER | W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW);
        else
            W.ShowWindow(h, W.SW_HIDE);
    }

    private static void Render()
    {
        UI.BeginFrame();
        Gfx.BeginDraw(Th.Bg);
        Shell.Draw();
        Gfx.EndDraw();
        UI.EndFrame();

        if (UI.TakeAnimate()) W.SetTimer(Hwnd, 1, 16, 0);
        else W.KillTimer(Hwnd, 1);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint StreamProc(nint hwnd, uint msg, nuint w, nint l)
    {
        if (msg is W.WM_MOUSEMOVE or W.WM_LBUTTONDOWN or W.WM_LBUTTONUP or W.WM_RBUTTONUP)
        {
            int idx = -1;
            if (hwnd == SelfStreamHwnd) idx = MaxWatchSlots;
            else
            {
                for (int i = 0; i < MaxWatchSlots; i++)
                    if (StreamHwnds[i] == hwnd) { idx = i; break; }
            }
            if (idx >= 0)
            {
                int px = W.LoWord(l) + _videoRects[idx].X;
                int py = W.HiWord(l) + _videoRects[idx].Y;
                nint mapped = (nint)(((py & 0xFFFF) << 16) | (px & 0xFFFF));
                W.PostMessageW(Hwnd, msg, w, mapped);
                return 0;
            }
        }
        if (msg == W.WM_MOUSEWHEEL)
        {
            W.PostMessageW(Hwnd, W.WM_MOUSEWHEEL, w, l);
            return 0;
        }
        return W.DefWindowProcW(hwnd, msg, w, l);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint ChatEditProc(nint hwnd, uint msg, nuint w, nint l)
    {
        if (msg == W.WM_MOUSEWHEEL)
        {
            W.PostMessageW(Hwnd, W.WM_MOUSEWHEEL, w, l);
            return 0;
        }
        if (msg == W.WM_KEYDOWN && (int)w == W.VK_K && W.KeyDown(W.VK_CONTROL))
        {
            Shell.ToggleQuickSwitch();
            return 0;
        }
        if (msg == W.WM_KEYDOWN && (int)w == W.VK_F && W.KeyDown(W.VK_CONTROL))
        {
            Shell.ToggleChannelSearch();
            return 0;
        }
        if (msg == W.WM_KEYDOWN && (int)w == W.VK_V && W.KeyDown(W.VK_CONTROL))
        {
            if (W.IsClipboardFormatAvailable(8) && !W.IsClipboardFormatAvailable(13))
            {
                Shell.OnClipboardImagePaste();
                return 0;
            }
        }
        if (msg == W.WM_KEYDOWN && (int)w == W.VK_ESCAPE)
        {
            Shell.OnEscape();
            return 0;
        }
        if (msg == W.WM_KEYDOWN && (int)w == W.VK_M && W.KeyDown(W.VK_CONTROL) && W.KeyDown(W.VK_SHIFT))
        {
            Shell.ToggleMute();
            return 0;
        }
        if (msg == W.WM_KEYDOWN && (int)w == W.VK_D && W.KeyDown(W.VK_CONTROL) && W.KeyDown(W.VK_SHIFT))
        {
            Shell.ToggleDeafen();
            return 0;
        }
        if (msg == W.WM_KEYDOWN && (int)w == W.VK_RETURN && !W.KeyDown(W.VK_SHIFT))
        {
            Shell.OnChatEnter(TakeChatInputText());
            return 0;
        }
        if (msg == W.WM_CHAR && (int)w >= 32)
        {
            Shell.OnChatTyping();
        }
        return W.CallWindowProcW(_origChatProc, hwnd, msg, w, l);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint SearchEditProc(nint hwnd, uint msg, nuint w, nint l)
    {
        if (msg == W.WM_KEYDOWN && (int)w == W.VK_ESCAPE)
        {
            Shell.OnEscape();
            return 0;
        }
        if (msg == W.WM_KEYDOWN && (int)w == W.VK_RETURN)
        {
            Shell.OnSearchEnter(GetSearchInputText());
            return 0;
        }
        if (msg == W.WM_KEYDOWN && (int)w == W.VK_K && W.KeyDown(W.VK_CONTROL))
        {
            Shell.ToggleQuickSwitch();
            return 0;
        }
        return W.CallWindowProcW(_origSearchProc, hwnd, msg, w, l);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint TokenEditProc(nint hwnd, uint msg, nuint w, nint l)
    {
        if (msg == W.WM_KEYDOWN && (int)w == W.VK_RETURN)
        {
            Shell.OnTokenSubmit(GetTokenInputText());
            return 0;
        }
        return W.CallWindowProcW(_origTokenProc, hwnd, msg, w, l);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nuint w, nint l)
    {
        switch (msg)
        {
            case W.WM_NCCALCSIZE when w != 0:
                return 0;
            case W.WM_NCHITTEST:
            {
                W.RECT wr;
                W.GetWindowRect(hwnd, &wr);
                int sx = W.LoWord(l), sy = W.HiWord(l);
                const int b = 6;
                bool left = sx < wr.L + b, right = sx >= wr.R - b, top = sy < wr.T + b, bot = sy >= wr.B - b;
                if (top && left) return 13;
                if (top && right) return 14;
                if (bot && left) return 16;
                if (bot && right) return 17;
                if (left) return 10;
                if (right) return 11;
                if (top) return 12;
                if (bot) return 15;
                float cy = (sy - wr.T) / Gfx.Scale;
                float cx = (sx - wr.L) / Gfx.Scale;
                float rightZone = (Updater.HasUpdate || Updater.Downloading || Updater.Applying) ? 380f : 140f;
                if (cy < Shell.TopH && cx < Gfx.W - rightZone) return 2;
                return 1;
            }
            case W.WM_ERASEBKGND:
                return 1;
            case W.WM_CTLCOLOREDIT:
                W.SetTextColor((nint)w, W.Rgb(0xF3, 0xF4, 0xF6));
                W.SetBkColor((nint)w, W.Rgb(0x17, 0x19, 0x20));
                return _editBrush;
            case W.WM_COMMAND:
                if (l == _searchEdit && (((uint)w >> 16) & 0xFFFF) == W.EN_CHANGE)
                {
                    Shell.OnSearchChanged(GetSearchInputText());
                    return 0;
                }
                break;
            case W.WM_DROPFILES:
                Shell.OnFilesDropped(W.ReadDroppedFiles((nint)w));
                return 0;
            case W.WM_SIZE when Gfx.DC != null:
                Gfx.Resize(W.LoWord(l), W.HiWord(l));
                Render();
                return 0;
            case W.WM_PAINT:
                W.ValidateRect(hwnd, null);
                if (Gfx.DC != null) Render();
                return 0;
            case WM_WAKE:
                _wakePending = false;
                Render();
                return 0;
            case W.WM_TIMER:
                if (w == 99)
                {
                    long ramMb = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
                    SettingsStore.Log($"BENCH_OK RAM={ramMb}MB FlushMs={Gfx.LastFlushMs:F2}");
                    W.PostQuitMessage(0);
                    return 0;
                }
                if (w == 2)
                {
                    if (Shell.PollBackgroundTick()) Render();
                    return 0;
                }
                Render();
                return 0;
            case W.WM_MOUSEMOVE:
                if (!_tracking)
                {
                    var tme = new W.TRACKMOUSEEVENT { cbSize = (uint)sizeof(W.TRACKMOUSEEVENT), dwFlags = 2, hwndTrack = hwnd };
                    W.TrackMouseEvent(&tme);
                    _tracking = true;
                }
                UI.MX = W.LoWord(l) / Gfx.Scale;
                UI.MY = W.HiWord(l) / Gfx.Scale;
                Render();
                return 0;
            case W.WM_MOUSELEAVE:
                _tracking = false;
                UI.MX = UI.MY = -1;
                Render();
                return 0;
            case W.WM_LBUTTONDOWN:
                UI.MX = W.LoWord(l) / Gfx.Scale;
                UI.MY = W.HiWord(l) / Gfx.Scale;
                UI.Down = UI.Pressed = true;
                Render();
                return 0;
            case W.WM_LBUTTONUP:
                UI.MX = W.LoWord(l) / Gfx.Scale;
                UI.MY = W.HiWord(l) / Gfx.Scale;
                UI.Down = false;
                UI.Released = true;
                Render();
                return 0;
            case W.WM_RBUTTONUP:
                UI.MX = W.LoWord(l) / Gfx.Scale;
                UI.MY = W.HiWord(l) / Gfx.Scale;
                UI.RightClicked = true;
                Render();
                return 0;
            case W.WM_MOUSEWHEEL:
                UI.Wheel = W.HiWord((nint)w) / 120f;
                Render();
                return 0;
            case W.WM_SETCURSOR when (l.ToInt64() & 0xFFFF) == 1:
                W.SetCursor(UI.WantHand ? _hand : _arrow);
                return 1;
            case W.WM_GETMINMAXINFO:
            {
                var mmi = (W.MINMAXINFO*)l;
                mmi->minTrackSize.X = 860;
                mmi->minTrackSize.Y = 560;
                return 0;
            }
            case W.WM_KEYDOWN when (int)w == W.VK_K && W.KeyDown(W.VK_CONTROL):
                Shell.ToggleQuickSwitch();
                return 0;
            case W.WM_KEYDOWN when (int)w == W.VK_F && W.KeyDown(W.VK_CONTROL):
                Shell.ToggleChannelSearch();
                return 0;
            case W.WM_KEYDOWN when (int)w == W.VK_M && W.KeyDown(W.VK_CONTROL) && W.KeyDown(W.VK_SHIFT):
                Shell.ToggleMute();
                return 0;
            case W.WM_KEYDOWN when (int)w == W.VK_D && W.KeyDown(W.VK_CONTROL) && W.KeyDown(W.VK_SHIFT):
                Shell.ToggleDeafen();
                return 0;
            case W.WM_KEYDOWN when ((int)w == W.VK_UP || (int)w == W.VK_DOWN) && W.KeyDown(W.VK_MENU):
                Shell.StepChannel((int)w == W.VK_UP ? -1 : 1);
                return 0;
            case W.WM_KEYDOWN when (int)w == W.VK_ESCAPE:
                Shell.OnEscape();
                return 0;
            case W.WM_CLOSE:
                W.DestroyWindow(hwnd);
                return 0;
            case W.WM_DESTROY:
                W.PostQuitMessage(0);
                return 0;
        }
        return W.DefWindowProcW(hwnd, msg, w, l);
    }
}
