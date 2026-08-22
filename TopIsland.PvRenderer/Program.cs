using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TopIsland.Controls;
using TopIsland.Models;
using TopIsland.Services;

const int Width = 1920;
const int Height = 1080;
const int Fps = 60;
const double Duration = 11.5;
const int FrameCount = (int)(Duration * Fps);

var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("docs/TopIsland-PV-Rendered.mp4");
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
psi.ArgumentList.Add("-i"); psi.ArgumentList.Add($"sine=frequency=120:sample_rate=48000:duration={Duration.ToString(CultureInfo.InvariantCulture)}");
psi.ArgumentList.Add("-filter_complex");
psi.ArgumentList.Add("[1:a]volume=0.012,lowpass=f=900,afade=t=in:st=0:d=0.35,afade=t=out:st=10.9:d=0.6[a]");
psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v");
psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[a]");
psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("h264_nvenc");
psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("p6");
psi.ArgumentList.Add("-cq"); psi.ArgumentList.Add("17");
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
var dpi = 96.0;

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

    var bg = new LinearGradientBrush(
        Color.FromRgb(4, 11, 23),
        Color.FromRgb(7, 25, 48),
        new Point(0, 0), new Point(1, 1));
    dc.DrawRectangle(bg, null, new Rect(0, 0, Width, Height));
    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(28, 34, 88, 160)), null, new Point(1660, 580), 500, 500);
    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(16, 48, 105, 200)), null, new Point(1240, 150), 360, 210);

    var phase = PhaseFor(t);
    var layout = InterpolateLayout(phase);
    var expand = phase.Expand;
    var reveal = phase.Reveal;
    var camera = CameraFor(t);

    var surfaceW = layout.Width;
    var surfaceH = layout.Height;
    var x = Width / 2.0 - surfaceW / 2.0;
    var y = 64.0;

    dc.PushTransform(new TranslateTransform(Width / 2.0 + camera.PanX, 140 + camera.PanY));
    dc.PushTransform(new ScaleTransform(camera.Zoom, camera.Zoom));
    dc.PushTransform(new TranslateTransform(-Width / 2.0, -140));

    var geometry = IslandGeometryFactory.Create(IslandStyle.Notch, new Size(surfaceW, surfaceH), expand, reveal);
    var translated = geometry.Clone();
    translated.Transform = new TranslateTransform(x, y);

    var shadow = translated.GetWidenedPathGeometry(new Pen(new SolidColorBrush(Color.FromArgb(58, 30, 80, 150)), 22));
    dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(28, 26, 78, 150)), null, shadow);

    var glass = new LinearGradientBrush(
        Color.FromArgb(238, 7, 10, 16),
        Color.FromArgb(232, 12, 17, 28),
        new Point(0, 0), new Point(0, 1));
    dc.DrawGeometry(glass, new Pen(new SolidColorBrush(Color.FromArgb(54, 188, 214, 255)), 1.1), translated);

    if (reveal > 0.02)
    {
        DrawContent(dc, x, y, surfaceW, surfaceH, expand, reveal, dpi, t);
        DrawHighlight(dc, t, x, y, surfaceW, surfaceH);
    }

    dc.Pop(); dc.Pop(); dc.Pop();
    DrawCaption(dc, t, dpi);
    return v;
}

static (double Reveal, double Expand, double Width, double Height) PhaseFor(double t)
{
    var compactW = 592.0;
    var compactH = 86.0;
    var expandedW = 1212.0;
    var expandedH = 382.0;
    double r, e;
    if (t < 0.9) { r = Smooth(t / 0.9); e = 0; }
    else if (t < 2.6) { r = 1; e = 0; }
    else if (t < 3.45) { r = 1; e = Smooth((t - 2.6) / 0.85); }
    else if (t < 8.7) { r = 1; e = 1; }
    else if (t < 9.45) { r = 1; e = 1 - Smooth((t - 8.7) / 0.75); }
    else if (t < 10.2) { r = 1 - Smooth((t - 9.45) / 0.75); e = 0; }
    else { r = 0; e = 0; }
    return (r, e, Lerp(compactW, expandedW, e), Lerp(compactH, expandedH, e));
}

static (double Reveal, double Expand, double Width, double Height) InterpolateLayout((double Reveal, double Expand, double Width, double Height) p) => p;

static (double Zoom, double PanX, double PanY) CameraFor(double t)
{
    if (t < 2.2) return (1.32 - 0.12 * Smooth(t / 2.2), 0, 28);
    if (t < 3.6) return (Lerp(1.2, 0.94, Smooth((t - 2.2) / 1.4)), 0, Lerp(28, 70, Smooth((t - 2.2) / 1.4)));
    if (t < 5.0) return (0.94, 0, 70);
    if (t < 6.3) return (Lerp(0.94, 1.14, Smooth((t - 5.0) / 1.3)), -235 * Smooth((t - 5.0) / 1.3), 82);
    if (t < 7.6) return (1.14, -235, 82);
    if (t < 8.7) return (Lerp(1.14, 0.94, Smooth((t - 7.6) / 1.1)), Lerp(-235, 0, Smooth((t - 7.6) / 1.1)), 70);
    return (0.94, 0, 70);
}

static void DrawContent(DrawingContext dc, double x, double y, double w, double h, double expand, double reveal, double dpi, double t)
{
    var alpha = (byte)Math.Round(255 * Math.Clamp(reveal, 0, 1));
    var white = new SolidColorBrush(Color.FromArgb(alpha, 245, 247, 250));
    var dim = new SolidColorBrush(Color.FromArgb((byte)(alpha * 0.62), 170, 178, 192));
    if (expand < 0.4)
    {
        var cy = y + Math.Max(22, (h - 16) * 0.54);
        DrawRoundedRect(dc, new Rect(x + 50, cy - 17, 34, 34), 7, new SolidColorBrush(Color.FromArgb(alpha, 24, 103, 210)));
        DrawText(dc, ">_", 17, white, x + 57, cy - 12, dpi, FontWeights.Bold);
        DrawText(dc, "TopIsland Showcase", 17, white, x + 100, cy - 14, dpi, FontWeights.SemiBold);
        DrawText(dc, "powershell", 10, dim, x + 100, cy + 7, dpi);
        DrawLine(dc, x + w - 245, cy - 22, x + w - 245, cy + 22, 1, 55);
        DrawText(dc, "22:47", 17, white, x + w - 220, cy - 13, dpi, FontWeights.SemiBold);
        DrawMiniMeter(dc, x + w - 138, cy, 0.42, "CPU", "42%", dpi, white, dim);
        DrawMiniMeter(dc, x + w - 76, cy, 0.69, "RAM", "69%", dpi, white, dim);
        return;
    }

    var p = Smooth(Math.Clamp((expand - 0.35) / 0.65, 0, 1));
    var contentAlpha = (byte)Math.Round(alpha * p);
    white = new SolidColorBrush(Color.FromArgb(contentAlpha, 245, 247, 250));
    dim = new SolidColorBrush(Color.FromArgb((byte)(contentAlpha * 0.62), 170, 178, 192));
    var left = x + 68;
    var top = y + 52;
    DrawText(dc, "ACTIVE APP", 9, dim, left + 85, top + 3, dpi);
    DrawRoundedRect(dc, new Rect(left, top, 60, 60), 12, new SolidColorBrush(Color.FromArgb(contentAlpha, 26, 108, 220)));
    DrawText(dc, ">_", 27, white, left + 10, top + 13, dpi, FontWeights.Bold);
    DrawText(dc, "TopIsland Showcase", 18, white, left + 84, top + 21, dpi, FontWeights.SemiBold);
    DrawText(dc, "powershell", 11, dim, left + 84, top + 44, dpi);

    var clockX = x + w * 0.46;
    DrawText(dc, "22:47", 42, white, clockX, top + 7, dpi, FontWeights.SemiBold);
    DrawText(dc, "SATURDAY · AUG 22", 9, dim, clockX + 4, top + 58, dpi);

    DrawMeter(dc, x + w * 0.66, top + 36, 0.42, "CPU", "42%", dpi, white, dim);
    DrawMeter(dc, x + w * 0.74, top + 36, 0.31, "GPU", "31%", dpi, white, dim);
    DrawMeter(dc, x + w * 0.82, top + 36, 0.69, "RAM", "69%", dpi, white, dim);

    DrawLine(dc, x + 48, y + 150, x + w - 48, y + 150, 1, 42);
    DrawText(dc, "TIMERS", 9, dim, x + 64, y + 182, dpi);
    DrawText(dc, "Focus", 13, white, x + 64, y + 211, dpi);
    DrawText(dc, "25:00", 13, white, x + 275, y + 211, dpi, FontWeights.SemiBold);
    DrawText(dc, "SESSION", 9, dim, x + w * 0.40, y + 182, dpi);
    DrawText(dc, "powershell", 13, white, x + w * 0.40, y + 211, dpi, FontWeights.SemiBold);
    DrawText(dc, "CPU 0.1% · 112 MB · Up 4m", 10, dim, x + w * 0.40, y + 233, dpi);
    DrawText(dc, "SYSTEM", 9, dim, x + w * 0.67, y + 182, dpi);
    DrawText(dc, "Storage", 11, dim, x + w * 0.67, y + 211, dpi);
    DrawText(dc, "61 GB free / 1 TB", 12, white, x + w * 0.67, y + 232, dpi, FontWeights.SemiBold);
    DrawText(dc, "Network", 11, dim, x + w * 0.82, y + 211, dpi);
    DrawText(dc, "↓ 0.4   ↑ 7.1 Mbps", 12, white, x + w * 0.82, y + 232, dpi, FontWeights.SemiBold);
}

static void DrawHighlight(DrawingContext dc, double t, double x, double y, double w, double h)
{
    Rect? rect = null;
    if (t is >= 1.35 and < 2.35) rect = new Rect(x + w - 170, y + 20, 145, Math.Min(62, h - 8));
    else if (t is >= 4.15 and < 5.05) rect = new Rect(x + w * 0.62, y + 30, w * 0.26, 100);
    else if (t is >= 5.2 and < 6.35) rect = new Rect(x + w * 0.62, y + 165, w * 0.33, 105);
    if (rect is null) return;
    var pulse = 0.62 + 0.22 * Math.Sin(t * Math.PI * 4);
    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(24 * pulse), 94, 160, 255)), new Pen(new SolidColorBrush(Color.FromArgb((byte)(180 * pulse), 112, 175, 255)), 2), rect.Value, 18, 18);
}

static void DrawCaption(DrawingContext dc, double t, double dpi)
{
    string title; string sub;
    if (t < 1.7) { title = "TopIsland"; sub = "Windows status. One surface."; }
    else if (t < 3.6) { title = "Status at a glance"; sub = "Media · time · CPU · RAM"; }
    else if (t < 5.2) { title = "One click. Full context."; sub = "The same surface expands without changing focus."; }
    else if (t < 7.6) { title = "Details when they matter"; sub = "Timers · session · system · network"; }
    else if (t < 9.2) { title = "Centered. Fluid. Contextual."; sub = "Camera and UI motion rendered together at native 60 fps."; }
    else { title = "TopIsland for Windows"; sub = "Fast. Contextual. Out of the way."; }
    var fadeIn = Smooth(Math.Clamp((t % 1.7) / 0.25, 0, 1));
    var brush = new SolidColorBrush(Color.FromArgb((byte)(245 * fadeIn), 245, 247, 250));
    var subBrush = new SolidColorBrush(Color.FromArgb((byte)(165 * fadeIn), 185, 192, 205));
    DrawText(dc, title, t < 1.7 ? 54 : 38, brush, 96, 820, dpi, FontWeights.SemiBold);
    DrawText(dc, sub, 18, subBrush, 100, 892, dpi);
}

static void DrawMiniMeter(DrawingContext dc, double cx, double cy, double value, string label, string valueText, double dpi, Brush white, Brush dim)
{
    var pen = new Pen(new SolidColorBrush(Color.FromArgb(72, 255, 255, 255)), 2.2);
    var active = new Pen(white, 2.8);
    dc.DrawEllipse(null, pen, new Point(cx, cy), 18, 18);
    dc.DrawGeometry(null, active, Arc(new Point(cx, cy), 18, -90, 360 * value));
    DrawText(dc, label, 7, dim, cx - 11, cy - 13, dpi);
    DrawText(dc, valueText, 9, white, cx - 12, cy - 2, dpi, FontWeights.SemiBold);
}

static void DrawMeter(DrawingContext dc, double cx, double cy, double value, string label, string valueText, double dpi, Brush white, Brush dim)
{
    var pen = new Pen(new SolidColorBrush(Color.FromArgb(62, 255, 255, 255)), 3.2);
    var active = new Pen(white, 3.5);
    dc.DrawEllipse(null, pen, new Point(cx, cy), 26, 26);
    dc.DrawGeometry(null, active, Arc(new Point(cx, cy), 26, -90, 360 * value));
    DrawText(dc, valueText, 12, white, cx - 18, cy - 8, dpi, FontWeights.SemiBold);
    DrawText(dc, label, 9, dim, cx - 13, cy + 34, dpi);
}

static Geometry Arc(Point center, double radius, double start, double sweep)
{
    sweep = Math.Clamp(sweep, 0, 359.99);
    var s = Circle(center, radius, start); var e = Circle(center, radius, start + sweep);
    var f = new PathFigure { StartPoint = s };
    f.Segments.Add(new ArcSegment(e, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true));
    return new PathGeometry([f]);
}
static Point Circle(Point c, double r, double deg) { var a = deg * Math.PI / 180; return new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)); }
static void DrawText(DrawingContext dc, string text, double size, Brush brush, double x, double y, double dpi, FontWeight? weight = null)
{
    var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI Variable"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, brush, dpi);
    dc.DrawText(ft, new Point(x, y));
}
static void DrawLine(DrawingContext dc, double x1, double y1, double x2, double y2, double width, byte alpha) => dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)), width), new Point(x1, y1), new Point(x2, y2));
static void DrawRoundedRect(DrawingContext dc, Rect rect, double radius, Brush fill) => dc.DrawRoundedRectangle(fill, null, rect, radius, radius);
static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
static double Lerp(double a, double b, double t) => a + (b - a) * t;
static string ResolveFfmpeg()
{
    var candidates = new[] { "ffmpeg.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe") };
    foreach (var c in candidates) if (c == "ffmpeg.exe" || File.Exists(c)) return c;
    return "ffmpeg.exe";
}
