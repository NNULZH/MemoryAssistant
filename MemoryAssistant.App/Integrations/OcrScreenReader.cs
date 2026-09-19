using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace MemoryAssistant.App.Integrations;

/// <summary>
/// 微信窗口 OCR 只读通道（V3.3）：微信 4.x 为 Qt 自绘，UIAutomation 不暴露文本节点，
/// 故改为「抓窗口位图 → Windows.Media.Ocr 识别」。
/// 全程只读：不注入、不点击、不发送、不联网（使用系统内置离线 OCR）。
/// </summary>
internal static class OcrScreenReader
{
    private const int PW_RENDERFULLCONTENT = 0x02;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    /// <summary>聊天面板左边界兜底比例（正常路径由 OCR 标题位置推断，见 EstimateChatPanelLeft）。</summary>
    private const double ChatPanelLeftRatio = 0.53;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint Size; public int Width; public int Height;
        public ushort Planes; public ushort BitCount; public uint Compression;
        public uint SizeImage; public int XPelsPerMeter; public int YPelsPerMeter;
        public uint ClrUsed; public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER Header; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    /// <summary>当前系统可用的 OCR 识别语言（用于验收日志与排障）。</summary>
    public static IReadOnlyList<string> AvailableLanguages()
    {
        try { return OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList(); }
        catch { return []; }
    }

    /// <summary>一行 OCR 文本及其在窗口内的位置（物理像素，相对窗口左上角）。</summary>
    public sealed record OcrLineBox(string Text, int X, int Y, int Width, int Height)
    {
        public int CenterX => X + Width / 2;
        public int CenterY => Y + Height / 2;
    }

    /// <summary>抓取窗口位图并 OCR，返回识别到的文本行（按阅读顺序）。</summary>
    public static async Task<(bool Ok, string Method, List<string> Lines, string? Error)> ReadAsync(IntPtr hwnd, CancellationToken ct)
    {
        var (ok, method, boxes, error) = await ReadBoxedAsync(hwnd, chatPanelOnly: true, ct);
        return (ok, method, boxes.Select(b => b.Text).ToList(), error);
    }

    /// <summary>
    /// 抓取窗口位图并 OCR，返回带位置的文本行。
    /// chatPanelOnly=true 时只识别右侧聊天区（"当前聊天"内容）；false 时识别整窗（用于定位搜索框/会话列表）。
    /// </summary>
    public static async Task<(bool Ok, string Method, List<OcrLineBox> Lines, string? Error)> ReadBoxedAsync(
        IntPtr hwnd, bool chatPanelOnly, CancellationToken ct)
    {
        if (!GetWindowRect(hwnd, out var rect))
            return (false, "", [], "无法获取窗口位置");
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w < 80 || h < 80)
            return (false, "", [], "窗口尺寸过小");

        var engine = CreateEngine();
        if (engine is null)
            return (false, "", [], "系统未安装可用 OCR 语言包");

        var captured = Capture(hwnd, rect, w, h);
        if (captured.Bytes is null)
            return (false, "", [], "窗口截图失败（窗口可能被遮挡或不可渲染）");
        ct.ThrowIfCancellationRequested();

        var all = await RecognizeBoxedAsync(engine, captured.Bytes, w, h, 0, ct);
        if (!chatPanelOnly)
            return (true, captured.Method, all, null);

        // 只保留聊天面板内容：用 OCR 位置推断面板左边界，排除左侧图标栏/会话列表。
        // （不用固定比例裁剪：微信改版/缩放变化会让面板位置整体漂移）
        int panelLeft = EstimateChatPanelLeft(all, w, h);
        var chat = all.Where(b => b.X < 0 || b.X >= panelLeft).ToList();
        return (true, captured.Method, chat.Count > 0 ? chat : all, null);
    }

    /// <summary>
    /// 只识别左上区域（会话搜索框与搜索结果下拉所在处）。
    /// 大窗口（全屏）整窗 OCR 要一两秒，而下拉菜单会自动收起，等太久就点空了；裁剪后快很多。
    /// 坐标原点仍是窗口左上角（从左上裁剪，无需偏移）。
    /// </summary>
    public static async Task<(bool Ok, string Method, List<OcrLineBox> Lines, string? Error)> ReadLeftPanelAsync(
        IntPtr hwnd, CancellationToken ct)
    {
        if (!GetWindowRect(hwnd, out var rect))
            return (false, "", [], "无法获取窗口位置");
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w < 80 || h < 80)
            return (false, "", [], "窗口尺寸过小");

        var engine = CreateEngine();
        if (engine is null)
            return (false, "", [], "系统未安装可用 OCR 语言包");

        var captured = Capture(hwnd, rect, w, h);
        if (captured.Bytes is null)
            return (false, "", [], "窗口截图失败（窗口可能被遮挡或不可渲染）");
        ct.ThrowIfCancellationRequested();

        int cw = Math.Max(120, (int)(w * 0.55));
        int ch = Math.Max(120, (int)(h * 0.70));
        var cropped = CropTopLeft(captured.Bytes, w, cw, ch);
        var lines = await RecognizeBoxedAsync(engine, cropped, cw, ch, 0, ct);
        return (true, captured.Method, lines, null);
    }

    /// <summary>裁出左上角 cw×ch 区域（BGRA，自上而下）。</summary>
    private static byte[] CropTopLeft(byte[] src, int srcWidth, int cw, int ch)
    {
        var dst = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++)
            Buffer.BlockCopy(src, y * srcWidth * 4, dst, y * cw * 4, cw * 4);
        return dst;
    }

    /// <summary>推断聊天面板左边界：取顶部标题带里字高最大的那行，其左侧留出内边距即为面板起点。</summary>
    public static int EstimateChatPanelLeft(IReadOnlyList<OcrLineBox> all, int w, int h)
    {
        var header = all
            .Where(b => b.X > w * 0.25 && b.Y >= 0 && b.Y < h * 0.22 && b.Height > 0)
            .OrderByDescending(b => b.Height)
            .FirstOrDefault();
        return header is null ? (int)(w * ChatPanelLeftRatio) : Math.Max(0, header.X - 40);
    }

    /// <summary>执行一次 OCR，返回带位置的行（坐标已加上 offsetX）。</summary>
    private static async Task<List<OcrLineBox>> RecognizeBoxedAsync(
        OcrEngine engine, byte[] bgra, int w, int h, int offsetX, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var buffer = CryptographicBuffer.CreateFromByteArray(bgra);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            buffer, BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);

        ct.ThrowIfCancellationRequested();
        var list = new List<OcrLineBox>();
        foreach (var line in result.Lines)
        {
            var text = NormalizeOcrLine((line.Text ?? "").Trim());
            if (text.Length == 0) continue;

            // 中文 OCR 会按字切词，取所有词的包围盒并集；拿不到词时退化为"位置未知"
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                if (r.X < minX) minX = r.X;
                if (r.Y < minY) minY = r.Y;
                if (r.X + r.Width > maxX) maxX = r.X + r.Width;
                if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
            }

            if (minX == double.MaxValue)
                list.Add(new OcrLineBox(text, -1, -1, 0, 0));
            else
                list.Add(new OcrLineBox(
                    text,
                    (int)minX + offsetX, (int)minY,
                    (int)(maxX - minX), (int)(maxY - minY)));
        }
        return list;
    }

    /// <summary>执行一次 OCR 并做中文空格归一化（仅文本）。</summary>
    private static async Task<List<string>> RecognizeAsync(OcrEngine engine, byte[] bgra, int w, int h, CancellationToken ct)
        => (await RecognizeBoxedAsync(engine, bgra, w, h, 0, ct)).Select(b => b.Text).ToList();

    /// <summary>去掉中文汉字之间被 OCR 误插的空格（"语 音 通 话" → "语音通话"）。</summary>
    private static string NormalizeOcrLine(string text)
    {
        if (text.Length == 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if ((c is ' ' or '\u3000') && sb.Length > 0 && i + 1 < text.Length
                && IsCjk(sb[^1]) && IsCjk(text[i + 1]))
                continue; // 两侧都是中文，判断为 OCR 误插的空格
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||   // 基本汉字
        (c >= 0x3400 && c <= 0x4DBF) ||   // 扩展 A
        (c >= 0xF900 && c <= 0xFAFF) ||   // 兼容汉字
        (c >= 0x3000 && c <= 0x303F) ||   // 中文标点
        (c >= 0xFF01 && c <= 0xFF60);     // 全角符号/字母

    /// <summary>优先中文引擎，退化到用户配置语言。</summary>
    private static OcrEngine? CreateEngine()
    {
        try
        {
            foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
                if (lang.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                    return OcrEngine.TryCreateFromLanguage(lang);
        }
        catch { /* 忽略，走用户语言兜底 */ }
        try { return OcrEngine.TryCreateFromUserProfileLanguages(); }
        catch { return null; }
    }

    /// <summary>
    /// 诊断/图标判定用：抓整窗位图并返回**原始 BGRA 像素**（自上而下，stride = w*4）。
    /// 与 ReadBoxedAsync 用的是同一套坐标系（窗口物理像素），所以 OCR 行坐标可直接索引像素。
    /// </summary>
    public static (byte[]? Bgra, int Width, int Height) CaptureRaw(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return (null, 0, 0);
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w < 80 || h < 80) return (null, 0, 0);
        var captured = Capture(hwnd, rect, w, h);
        return captured.Bytes is null ? (null, 0, 0) : (captured.Bytes, w, h);
    }

    /// <summary>
    /// 诊断用（维护者核对 OCR 判据）：抓整窗位图并编码成 PNG 落盘。只读，不注入不点击。
    /// 返回 PNG 字节；抓不到返回 null。
    /// </summary>
    public static byte[]? CapturePng(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return null;
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w < 80 || h < 80) return null;
        var captured = Capture(hwnd, rect, w, h);
        return captured.Bytes is null ? null : BgraToPng(captured.Bytes, w, h);
    }

    private static byte[] BgraToPng(byte[] bgra, int w, int h)
    {
        var source = System.Windows.Media.Imaging.BitmapSource.Create(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bgra, w * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static (byte[]? Bytes, string Method) Capture(IntPtr hwnd, RECT rect, int w, int h)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bmi = new BITMAPINFO
        {
            Header = new BITMAPINFOHEADER
            {
                Size = 40, Width = w, Height = -h, // 负高度 = 自上而下
                Planes = 1, BitCount = 32, Compression = 0,
            },
        };
        var dib = CreateDIBSection(memDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero)
        {
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return (null, "");
        }

        var old = SelectObject(memDc, dib);
        try
        {
            // 1) 优先 BitBlt 屏幕对应区域：只读屏幕像素，不向目标窗口发送任何消息。
            //    （PrintWindow 会让目标窗口参与渲染，微信 4.x 在这种自绘架构下可能被卡死 UI 线程）
            //    注意：最大化窗口的左上角可能是负坐标（含不可见边框），直接取屏幕外像素会让整幅图错位，
            //    因此把源区域钳到屏幕内、再按同一偏移贴回位图，保证位图坐标与窗口坐标一一对应。
            var method = "ScreenCapture";
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
            int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
            if (vw <= 0 || vh <= 0) { vx = 0; vy = 0; vw = GetSystemMetrics(0); vh = GetSystemMetrics(1); }

            int srcX = Math.Clamp(rect.Left, vx, Math.Max(vx, vx + vw - 1));
            int srcY = Math.Clamp(rect.Top, vy, Math.Max(vy, vy + vh - 1));
            int offX = srcX - rect.Left, offY = srcY - rect.Top;
            int cw = Math.Min(w - offX, vx + vw - srcX);
            int ch = Math.Min(h - offY, vy + vh - srcY);

            var ok = cw > 0 && ch > 0 && BitBlt(memDc, offX, offY, cw, ch, screenDc, srcX, srcY, SRCCOPY | CAPTUREBLT);
            if (!ok || IsBlank(bits, w, h))
            {
                // 2) 兜底：窗口被遮挡/不可见时用 PrintWindow 抓窗口自身内容
                method = "PrintWindow";
                ok = PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT);
            }
            if (!ok || IsBlank(bits, w, h)) return (null, "");

            var bytes = new byte[w * h * 4];
            Marshal.Copy(bits, bytes, 0, bytes.Length);
            // GDI 位图 alpha 恒为 0；补满 0xFF 以避免 Premultiplied 下被当成全透明
            for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 0xFF;
            return (bytes, method);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>采样若干像素，判断是否为全同色（PrintWindow 失败常返回纯黑）。</summary>
    private static bool IsBlank(IntPtr bits, int w, int h)
    {
        int stride = w * 4;
        var px = new byte[3];
        int first = -1, stepX = Math.Max(1, w / 10), stepY = Math.Max(1, h / 10);
        for (int y = stepY; y < h; y += stepY)
        {
            for (int x = stepX; x < w; x += stepX)
            {
                Marshal.Copy(bits + y * stride + x * 4, px, 0, 3);
                int v = (px[0] << 16) | (px[1] << 8) | px[2];
                if (first < 0) first = v;
                else if (v != first) return false;
            }
        }
        return true;
    }
}
