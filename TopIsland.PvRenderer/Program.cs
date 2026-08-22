using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TopIsland.Controls;
using TopIsland.Models;

const int Width = 1920;
const int Height = 1080;
const int Fps = 60;
const double Duration = 11.8;
const int FrameCount = (int)(Duration * Fps);
const double CameraAnchorX = Width / 2.0;
const double CameraAnchorY = 180.0;

var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("docs/TopIsland-PV.mp4");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var ffmpeg = ResolveFfmpeg();
var psi = new ProcessStartInfo
{
    FileName = ffmpeg,
    UseShellExecute = false,
    RedirectStandardInput = true,
    RedirectStandardError = true,
    CreateNoWindow = true
};
psi.ArgumentList.Add("-y");
psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
psi.ArgumentList.Add("-pixel_format"); psi.ArgumentList.Add("bgra");
psi.ArgumentList.Add("-video_size"); psi.ArgumentList.Add($"{Width}x{Height}");
psi.ArgumentList.Add("-framerate"); psi.ArgumentList.Add(Fps.ToString(CultureInfo.InvariantCulture));
psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("-");
psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
psi.ArgumentList.Add("-i"); psi.ArgumentList.Add($"sine=frequency=118:sample_rate=48000:duration={Duration.ToString(CultureInfo.InvariantCulture)}");
psi.ArgumentList.Add("-filter_complex");
psi.ArgumentList.Add("[1:a]volume=0.010,lowpass=f=850,afade=t=in:st=0:d=0.4,afade=t=out:st=11.2:d=0.55[a]");
psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v");
psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[a]");
psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("h264_nvenc");
psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("p6");
psi.ArgumentList.Add("-cq"); psi.ArgumentList.Add("16");
psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("128k");
psi.ArgumentList.Add("-shortest");
psi.ArgumentList.Add(output);

using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
var stderrTask = process.StandardError.ReadToEndAsync();
using var stdin = process.StandardInput.BaseStream;
var pixels = new byte[Width * Height * 4];
var stride = Width * 4;
const double dpi = 96.0;

for (var frame = 0; frame < FrameCount; frame++)
{
    var t = frame / (double)Fps;
    var visual = BuildFrame(t, dpi);
    var bitmap = new RenderTargetBitmap(Width, Height, dpi, dpi, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    bitmap.CopyPixels(pixels, stride, 0);
    stdin.Write(pixels, 0, pixels.Length);
    if (frame % 60 == 0) Console.WriteLine($"frame {frame}/{FrameCount}");
}

stdin.Flush();
stdin.Close();
process.WaitForExit();
var fferr = await stderrTask;
if (process.ExitCode != 0) throw new Exception(fferr);
Console.WriteLine(output);

static DrawingVisual BuildFrame(double t, double dpi)
{
    var v = new DrawingVisual();
    using var dc = v.RenderOpen();

    DrawBackground(dc, t);

    var phase = PhaseFor(t);
    var surfaceW = Lerp(592.0, 1212.0, phase.Expand);
    var surfaceH = Lerp(86.0, 382.0, phase.Expand);
    var x = Width / 2.0 - surfaceW / 2.0;
    const double y = 66.0;
    var content = CreateContentLayout(x, y, surfaceW, surfaceH);
    var camera = CameraFor(t, content);

    dc.PushTransform(new TranslateTransform(CameraAnchorX + camera.PanX, CameraAnchorY + camera.PanY));
    dc.PushTransform(new ScaleTransform(camera.Zoom, camera.Zoom));
    dc.PushTransform(new TranslateTransform(-CameraAnchorX, -CameraAnchorY));

    var geometry = IslandGeometryFactory.Create(IslandStyle.Notch, new Size(surfaceW, surfaceH), phase.Expand, phase.Reveal);
    var translated = geometry.Clone();
    translated.Transform = new TranslateTransform(x, y);

    var shadow = translated.GetWidenedPathGeometry(new Pen(new SolidColorBrush(Color.FromArgb(54, 28, 76, 145)), 22));
    dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(25, 24, 68, 140)), null, shadow);

    var glass = new LinearGradientBrush(
        Color.FromArgb(242, 5, 9, 15),
        Color.FromArgb(236, 12, 18, 29),
        new Point(0, 0), new Point(0, 1));
    dc.DrawGeometry(glass, new Pen(new SolidColorBrush(Color.FromArgb(58, 174, 208, 255)), 1.1), translated);

    if (phase.Reveal > 0.015)
    {
        DrawContent(dc, content, phase.Expand, phase.Reveal, dpi);
        DrawHighlight(dc, t, content, dpi);
    }

    dc.Pop();
    dc.Pop();
    dc.Pop();

    DrawCaption(dc, t, dpi);
    return v;
}

static void DrawBackground(DrawingContext dc, double t)
{
    var bg = new LinearGradientBrush(
        Color.FromRgb(3, 9, 19),
        Color.FromRgb(6, 24, 46),
        new Point(0, 0), new Point(1, 1));
    dc.DrawRectangle(bg, null, new Rect(0, 0, Width, Height));

    var drift = Math.Sin(t * 0.22) * 22;
    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(25, 30, 82, 160)), null, new Point(1640 + drift, 560), 500, 500);
    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(15, 43, 99, 198)), null, new Point(1230 - drift * 0.4, 145), 360, 210);
}

static MotionPhase PhaseFor(double t)
{
    double reveal;
    double expand;

    if (t < 0.78)
    {
        reveal = EaseOutQuint(t / 0.78);
        expand = 0;
    }
    else if (t < 2.45)
    {
        reveal = 1;
        expand = 0;
    }
    else if (t < 3.30)
    {
        reveal = 1;
        expand = EaseInOutCubic((t - 2.45) / 0.85);
    }
    else if (t < 8.75)
    {
        reveal = 1;
        expand = 1;
    }
    else if (t < 9.42)
    {
        reveal = 1;
        expand = 1 - EaseInOutCubic((t - 8.75) / 0.67);
    }
    else if (t < 10.02)
    {
        reveal = 1 - EaseInCubic((t - 9.42) / 0.60);
        expand = 0;
    }
    else
    {
        reveal = 0;
        expand = 0;
    }

    return new MotionPhase(reveal, expand);
}

static CameraState CameraFor(double t, ContentLayout layout)
{
    CameraState compact = new(1.23, 0, 28);
    CameraState full = new(0.93, 0, 72);

    if (t < 2.10)
    {
        return LerpCamera(new CameraState(1.30, 0, 18), compact, EaseOutCubic(t / 2.10));
    }

    if (t < 3.55)
    {
        return LerpCamera(compact, full, EaseInOutSine((t - 2.10) / 1.45));
    }

    if (t < 4.55)
    {
        return full;
    }

    if (t < 5.60)
    {
        var meters = FocusCamera(layout, layout.ExpandedMeters, 1.10, new Point(1120, 225));
        return LerpCamera(full, meters, EaseInOutSine((t - 4.55) / 1.05));
    }

    if (t < 5.90)
    {
        return FocusCamera(layout, layout.ExpandedMeters, 1.10, new Point(1120, 225));
    }

    if (t < 7.00)
    {
        var from = FocusCamera(layout, layout.ExpandedMeters, 1.10, new Point(1120, 225));
        var system = FocusCamera(layout, layout.System, 1.12, new Point(1120, 240));
        return LerpCamera(from, system, EaseInOutSine((t - 5.90) / 1.10));
    }

    if (t < 7.25)
    {
        return FocusCamera(layout, layout.System, 1.12, new Point(1120, 240));
    }

    if (t < 8.15)
    {
        var from = FocusCamera(layout, layout.System, 1.12, new Point(1120, 240));
        var combined = Rect.Union(layout.Timers, layout.Session);
        var timers = FocusCamera(layout, combined, 1.08, new Point(880, 245));
        return LerpCamera(from, timers, EaseInOutSine((t - 7.25) / 0.90));
    }

    if (t < 8.55)
    {
        var combined = Rect.Union(layout.Timers, layout.Session);
        return FocusCamera(layout, combined, 1.08, new Point(880, 245));
    }

    if (t < 9.15)
    {
        var combined = Rect.Union(layout.Timers, layout.Session);
        var from = FocusCamera(layout, combined, 1.08, new Point(880, 245));
        return LerpCamera(from, full, EaseInOutSine((t - 8.55) / 0.60));
    }

    return full;
}

static CameraState FocusCamera(ContentLayout layout, Rect focus, double zoom, Point desired)
{
    var rawPanX = desired.X - CameraAnchorX - zoom * (focus.X + focus.Width / 2.0 - CameraAnchorX);
    var rawPanY = desired.Y - CameraAnchorY - zoom * (focus.Y + focus.Height / 2.0 - CameraAnchorY);

    // Intentional focus shots may pan, but never allow the notch itself to be
    // accidentally clipped by the video frame.
    const double safeX = 40;
    const double safeTop = 28;
    var leftWithoutPan = CameraAnchorX + zoom * (layout.Surface.Left - CameraAnchorX);
    var rightWithoutPan = CameraAnchorX + zoom * (layout.Surface.Right - CameraAnchorX);
    var topWithoutPan = CameraAnchorY + zoom * (layout.Surface.Top - CameraAnchorY);
    var bottomWithoutPan = CameraAnchorY + zoom * (layout.Surface.Bottom - CameraAnchorY);

    var minPanX = safeX - leftWithoutPan;
    var maxPanX = Width - safeX - rightWithoutPan;
    var minPanY = safeTop - topWithoutPan;
    var maxPanY = 520 - bottomWithoutPan;

    return new CameraState(
        zoom,
        Math.Clamp(rawPanX, minPanX, maxPanX),
        Math.Clamp(rawPanY, minPanY, maxPanY));
}

static ContentLayout CreateContentLayout(double x, double y, double w, double h)
{
    var compactCy = y + Math.Max(22, (h - 16) * 0.54);
    var compactCpu = new Point(x + w - 138, compactCy);
    var compactRam = new Point(x + w - 76, compactCy);
    var compactMeters = RectFromPoints(compactCpu, compactRam, 27, 26);

    var top = y + 52;
    var expandedCpu = new Point(x + w * 0.66, top + 36);
    var expandedGpu = new Point(x + w * 0.74, top + 36);
    var expandedRam = new Point(x + w * 0.82, top + 36);
    var expandedMeters = new Rect(
        expandedCpu.X - 40,
        expandedCpu.Y - 35,
        expandedRam.X - expandedCpu.X + 80,
        88);

    var timers = new Rect(x + 52, y + 170, 272, 92);
    var session = new Rect(x + w * 0.40 - 18, y + 170, 315, 92);
    var system = new Rect(x + w * 0.67 - 18, y + 170, w * 0.28 + 24, 105);

    return new ContentLayout(
        new Rect(x, y, w, h),
        compactMeters,
        compactCpu,
        compactRam,
        expandedMeters,
        expandedCpu,
        expandedGpu,
        expandedRam,
        timers,
        session,
        system);
}

static void DrawContent(DrawingContext dc, ContentLayout l, double expand, double reveal, double dpi)
{
    var x = l.Surface.X;
    var y = l.Surface.Y;
    var w = l.Surface.Width;
    var h = l.Surface.Height;
    var alpha = (byte)Math.Round(255 * Math.Clamp(reveal, 0, 1));
    var white = new SolidColorBrush(Color.FromArgb(alpha, 245, 247, 250));
    var dim = new SolidColorBrush(Color.FromArgb((byte)(alpha * 0.62), 170, 178, 192));

    if (expand < 0.40)
    {
        var cy = l.CompactCpu.Y;
        DrawRoundedRect(dc, new Rect(x + 50, cy - 17, 34, 34), 7, new SolidColorBrush(Color.FromArgb(alpha, 24, 103, 210)));
        DrawText(dc, ">_", 17, white, x + 57, cy - 12, dpi, FontWeights.Bold);
        DrawText(dc, "TopIsland Showcase", 17, white, x + 100, cy - 14, dpi, FontWeights.SemiBold);
        DrawText(dc, "powershell", 10, dim, x + 100, cy + 7, dpi);
        DrawLine(dc, x + w - 245, cy - 22, x + w - 245, cy + 22, 1, 55);
        DrawText(dc, "22:47", 17, white, x + w - 220, cy - 13, dpi, FontWeights.SemiBold);
        DrawMiniMeter(dc, l.CompactCpu, 0.42, "CPU", "42%", dpi, white, dim);
        DrawMiniMeter(dc, l.CompactRam, 0.69, "RAM", "69%", dpi, white, dim);
        return;
    }

    var contentProgress = EaseOutCubic(Math.Clamp((expand - 0.35) / 0.65, 0, 1));
    var contentAlpha = (byte)Math.Round(alpha * contentProgress);
    white = new SolidColorBrush(Color.FromArgb(contentAlpha, 245, 247, 250));
    dim = new SolidColorBrush(Color.FromArgb((byte)(contentAlpha * 0.62), 170, 178, 192));

    var left = x + 68;
    var top = y + 52;
    DrawText(dc, "アクティブアプリ", 9, dim, left + 85, top + 3, dpi, fontFamily: "Yu Gothic UI");
    DrawRoundedRect(dc, new Rect(left, top, 60, 60), 12, new SolidColorBrush(Color.FromArgb(contentAlpha, 26, 108, 220)));
    DrawText(dc, ">_", 27, white, left + 10, top + 13, dpi, FontWeights.Bold);
    DrawText(dc, "TopIsland Showcase", 18, white, left + 84, top + 21, dpi, FontWeights.SemiBold);
    DrawText(dc, "powershell", 11, dim, left + 84, top + 44, dpi);

    var clockX = x + w * 0.46;
    DrawText(dc, "22:47", 42, white, clockX, top + 7, dpi, FontWeights.SemiBold);
    DrawText(dc, "8月22日・土曜日", 9, dim, clockX + 4, top + 58, dpi, fontFamily: "Yu Gothic UI");

    DrawMeter(dc, l.ExpandedCpu, 0.42, "CPU", "42%", dpi, white, dim);
    DrawMeter(dc, l.ExpandedGpu, 0.31, "GPU", "31%", dpi, white, dim);
    DrawMeter(dc, l.ExpandedRam, 0.69, "RAM", "69%", dpi, white, dim);

    DrawLine(dc, x + 48, y + 150, x + w - 48, y + 150, 1, 42);

    DrawText(dc, "タイマー", 9, dim, l.Timers.Left + 12, l.Timers.Top + 12, dpi, fontFamily: "Yu Gothic UI");
    DrawText(dc, "集中", 13, white, l.Timers.Left + 12, l.Timers.Top + 41, dpi, fontFamily: "Yu Gothic UI");
    DrawText(dc, "25:00", 13, white, l.Timers.Right - 82, l.Timers.Top + 41, dpi, FontWeights.SemiBold);

    DrawText(dc, "セッション", 9, dim, l.Session.Left + 12, l.Session.Top + 12, dpi, fontFamily: "Yu Gothic UI");
    DrawText(dc, "powershell", 13, white, l.Session.Left + 12, l.Session.Top + 41, dpi, FontWeights.SemiBold);
    DrawText(dc, "CPU 0.1%・112 MB・稼働 4分", 10, dim, l.Session.Left + 12, l.Session.Top + 63, dpi, fontFamily: "Yu Gothic UI");

    DrawText(dc, "システム", 9, dim, l.System.Left + 12, l.System.Top + 12, dpi, fontFamily: "Yu Gothic UI");
    DrawText(dc, "ストレージ", 11, dim, l.System.Left + 12, l.System.Top + 41, dpi, fontFamily: "Yu Gothic UI");
    DrawText(dc, "空き 61 GB / 1 TB", 12, white, l.System.Left + 12, l.System.Top + 62, dpi, FontWeights.SemiBold, "Yu Gothic UI");
    var networkX = l.System.Left + l.System.Width * 0.55;
    DrawText(dc, "ネットワーク", 11, dim, networkX, l.System.Top + 41, dpi, fontFamily: "Yu Gothic UI");
    DrawText(dc, "↓ 0.4   ↑ 7.1 Mbps", 12, white, networkX, l.System.Top + 62, dpi, FontWeights.SemiBold);
}

static void DrawHighlight(DrawingContext dc, double t, ContentLayout l, double dpi)
{
    Rect target;
    string label;
    double start;
    double end;

    if (t is >= 1.18 and < 2.30)
    {
        target = Inflate(l.CompactMeters, 8, 7);
        label = "CPU / RAM";
        start = 1.18;
        end = 2.30;
    }
    else if (t is >= 4.55 and < 5.88)
    {
        target = Inflate(l.ExpandedMeters, 10, 8);
        label = "使用率";
        start = 4.55;
        end = 5.88;
    }
    else if (t is >= 5.95 and < 7.20)
    {
        target = Inflate(l.System, 8, 8);
        label = "システム / ネットワーク";
        start = 5.95;
        end = 7.20;
    }
    else if (t is >= 7.28 and < 8.48)
    {
        target = Inflate(Rect.Union(l.Timers, l.Session), 8, 8);
        label = "タイマー / セッション";
        start = 7.28;
        end = 8.48;
    }
    else
    {
        return;
    }

    var alpha = Envelope(t, start, end, 0.18, 0.20);
    var breathe = 0.96 + 0.04 * Math.Sin((t - start) * Math.PI * 1.5);
    var strokeAlpha = (byte)Math.Round(205 * alpha * breathe);
    var fillAlpha = (byte)Math.Round(22 * alpha);
    var stroke = new SolidColorBrush(Color.FromArgb(strokeAlpha, 112, 175, 255));
    var fill = new SolidColorBrush(Color.FromArgb(fillAlpha, 78, 143, 245));
    dc.DrawRoundedRectangle(fill, new Pen(stroke, 2.1), target, 16, 16);

    var tagBrush = new SolidColorBrush(Color.FromArgb((byte)(220 * alpha), 15, 28, 47));
    var tagText = new SolidColorBrush(Color.FromArgb((byte)(245 * alpha), 228, 238, 255));
    var tagRect = new Rect(target.Left + 12, target.Top - 31, Math.Max(92, label.Length * 14 + 28), 24);
    dc.DrawRoundedRectangle(tagBrush, new Pen(new SolidColorBrush(Color.FromArgb((byte)(90 * alpha), 112, 175, 255)), 1), tagRect, 8, 8);
    DrawText(dc, label, 11, tagText, tagRect.Left + 12, tagRect.Top + 4, dpi, FontWeights.SemiBold, "Yu Gothic UI");
}

static void DrawCaption(DrawingContext dc, double t, double dpi)
{
    var caption = CaptionFor(t);
    var alpha = Envelope(t, caption.Start, caption.End, 0.28, 0.24);
    var enter = EaseOutCubic(Math.Clamp((t - caption.Start) / 0.34, 0, 1));
    var y = 826 + (1 - enter) * 12;

    var titleBrush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(248 * alpha), 245, 247, 250));
    var subBrush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(175 * alpha), 183, 192, 207));

    DrawText(dc, caption.Title, caption.IsOutro ? 48 : 38, titleBrush, 92, y, dpi, FontWeights.SemiBold, "Yu Gothic UI");
    DrawText(dc, caption.Subtitle, 18, subBrush, 96, y + 61, dpi, fontFamily: "Yu Gothic UI");
}

static Caption CaptionFor(double t)
{
    if (t < 1.35) return new Caption(0.00, 1.35, "上端にかざすだけ", "必要なときだけ、ノッチが自然に現れる。", false);
    if (t < 2.75) return new Caption(1.35, 2.75, "ひと目で分かる", "再生中のアプリ・時刻・CPU・RAMを一か所に。", false);
    if (t < 4.45) return new Caption(2.75, 4.45, "クリックで、詳しく", "同じノッチがそのまま広がって、情報を増やす。", false);
    if (t < 5.85) return new Caption(4.45, 5.85, "使用率をすぐ確認", "CPU・GPU・RAMを、視線を大きく動かさず確認。", false);
    if (t < 7.18) return new Caption(5.85, 7.18, "必要なシステム情報だけ", "ストレージや通信量を、必要な瞬間だけ表示。", false);
    if (t < 8.55) return new Caption(7.18, 8.55, "作業中の状態も一画面に", "タイマーとセッションを、同じ場所から確認できる。", false);
    if (t < 9.95) return new Caption(8.55, 9.95, "見終わったら、すぐ戻る", "情報を残しっぱなしにせず、作業の邪魔をしない。", false);
    return new Caption(9.95, Duration, "TopIsland for Windows", "必要な情報を、必要な瞬間だけ。", true);
}

static void DrawMiniMeter(DrawingContext dc, Point center, double value, string label, string valueText, double dpi, Brush white, Brush dim)
{
    var pen = new Pen(new SolidColorBrush(Color.FromArgb(72, 255, 255, 255)), 2.2);
    var active = new Pen(white, 2.8);
    dc.DrawEllipse(null, pen, center, 18, 18);
    dc.DrawGeometry(null, active, Arc(center, 18, -90, 360 * value));
    DrawText(dc, label, 7, dim, center.X - 11, center.Y - 13, dpi);
    DrawText(dc, valueText, 9, white, center.X - 12, center.Y - 2, dpi, FontWeights.SemiBold);
}

static void DrawMeter(DrawingContext dc, Point center, double value, string label, string valueText, double dpi, Brush white, Brush dim)
{
    var pen = new Pen(new SolidColorBrush(Color.FromArgb(62, 255, 255, 255)), 3.2);
    var active = new Pen(white, 3.5);
    dc.DrawEllipse(null, pen, center, 26, 26);
    dc.DrawGeometry(null, active, Arc(center, 26, -90, 360 * value));
    DrawText(dc, valueText, 12, white, center.X - 18, center.Y - 8, dpi, FontWeights.SemiBold);
    DrawText(dc, label, 9, dim, center.X - 13, center.Y + 34, dpi);
}

static Geometry Arc(Point center, double radius, double start, double sweep)
{
    sweep = Math.Clamp(sweep, 0, 359.99);
    var s = Circle(center, radius, start);
    var e = Circle(center, radius, start + sweep);
    var f = new PathFigure { StartPoint = s };
    f.Segments.Add(new ArcSegment(e, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true));
    return new PathGeometry([f]);
}

static Point Circle(Point c, double r, double deg)
{
    var a = deg * Math.PI / 180;
    return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
}

static void DrawText(
    DrawingContext dc,
    string text,
    double size,
    Brush brush,
    double x,
    double y,
    double dpi,
    FontWeight? weight = null,
    string fontFamily = "Segoe UI Variable")
{
    var ft = new FormattedText(
        text,
        CultureInfo.GetCultureInfo("ja-JP"),
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily(fontFamily), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
        size,
        brush,
        dpi);
    dc.DrawText(ft, new Point(x, y));
}

static void DrawLine(DrawingContext dc, double x1, double y1, double x2, double y2, double width, byte alpha) =>
    dc.DrawLine(
        new Pen(new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)), width),
        new Point(x1, y1),
        new Point(x2, y2));

static void DrawRoundedRect(DrawingContext dc, Rect rect, double radius, Brush fill) =>
    dc.DrawRoundedRectangle(fill, null, rect, radius, radius);

static Rect RectFromPoints(Point a, Point b, double padX, double padY)
{
    var left = Math.Min(a.X, b.X) - padX;
    var right = Math.Max(a.X, b.X) + padX;
    var top = Math.Min(a.Y, b.Y) - padY;
    var bottom = Math.Max(a.Y, b.Y) + padY;
    return new Rect(left, top, right - left, bottom - top);
}

static Rect Inflate(Rect rect, double x, double y)
{
    rect.Inflate(x, y);
    return rect;
}

static double Envelope(double t, double start, double end, double fadeIn, double fadeOut)
{
    if (t <= start || t >= end) return 0;
    if (t < start + fadeIn) return EaseOutCubic((t - start) / fadeIn);
    if (t > end - fadeOut) return 1 - EaseInCubic((t - (end - fadeOut)) / fadeOut);
    return 1;
}

static CameraState LerpCamera(CameraState a, CameraState b, double t) =>
    new(Lerp(a.Zoom, b.Zoom, t), Lerp(a.PanX, b.PanX, t), Lerp(a.PanY, b.PanY, t));

static double EaseOutQuint(double x)
{
    x = Math.Clamp(x, 0, 1);
    return 1 - Math.Pow(1 - x, 5);
}

static double EaseOutCubic(double x)
{
    x = Math.Clamp(x, 0, 1);
    return 1 - Math.Pow(1 - x, 3);
}

static double EaseInCubic(double x)
{
    x = Math.Clamp(x, 0, 1);
    return x * x * x;
}

static double EaseInOutCubic(double x)
{
    x = Math.Clamp(x, 0, 1);
    return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
}

static double EaseInOutSine(double x)
{
    x = Math.Clamp(x, 0, 1);
    return -(Math.Cos(Math.PI * x) - 1) / 2;
}

static double Lerp(double a, double b, double t) => a + (b - a) * t;

static string ResolveFfmpeg()
{
    var candidates = new[]
    {
        "ffmpeg.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe")
    };
    foreach (var c in candidates)
    {
        if (c == "ffmpeg.exe" || File.Exists(c)) return c;
    }
    return "ffmpeg.exe";
}

readonly record struct MotionPhase(double Reveal, double Expand);
readonly record struct CameraState(double Zoom, double PanX, double PanY);
readonly record struct Caption(double Start, double End, string Title, string Subtitle, bool IsOutro);
readonly record struct ContentLayout(
    Rect Surface,
    Rect CompactMeters,
    Point CompactCpu,
    Point CompactRam,
    Rect ExpandedMeters,
    Point ExpandedCpu,
    Point ExpandedGpu,
    Point ExpandedRam,
    Rect Timers,
    Rect Session,
    Rect System);
