using System;
using System.IO;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace Klang_WinUI.Controls;

/// <summary>
/// 彩蛋：坐在专辑封面上边缘的小人。播放时两条腿跟着节拍晃，偶尔眨眼，点一下会跳起来。
/// 默认画的是 Klang 自己的原创 Q 版形象（淡紫色短发 + 大耳机）；
/// 如果 Assets\EasterEgg\miku.png 存在（透明背景、坐姿、腿自然下垂），就改为显示这张图，
/// 图片的 65% 高度处对齐封面上边缘。
/// 控件的底部 <see cref="LegRoom"/> 是“垂在封面前面”的腿，上面是坐在边缘上的身体；SeatY 处就是封面上边缘。
/// </summary>
public sealed class EasterEggSitter : UserControl
{
    public const double ControlWidth = 152, ControlHeight = 200;
    /// <summary>控件里封面上边缘所在的高度（从顶部算）。</summary>
    public const double SeatY = 140;
    /// <summary>图片模式：图片宽度，以及图片里“座位”（坐着的横条上沿）所在的高度比例。</summary>
    private const double ImageWidth = 128, ImageSeatFraction = 0.71;
    public const double LegRoom = ControlHeight - SeatY;

    private readonly CanvasControl _canvas = new();
    private CanvasBitmap? _image;
    private bool _imageTried;

    private bool _playing;
    private double _beatPeriod = 1.9;  // 腿晃一个来回的时间（秒）
    private double _phase, _swing;       // 晃腿相位、当前幅度（播放时渐强，暂停时渐弱）
    private double _hopT = -1;           // 点击后跳起的进度（秒），-1 = 没在跳
    private double _blinkAt = 2, _blinkT = -1;
    private double _tilt, _tiltTarget, _tiltAt = 5;
    private readonly Random _rng = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _last;
    private bool _hooked;

    public EasterEggSitter()
    {
        Width = ControlWidth;
        Height = ControlHeight;
        IsTabStop = false;
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.CreateResources += (s, e) =>
        {
            _image = null; _imageTried = false;
            _sheet?.Dispose(); _sheetBlink?.Dispose(); _sheet = _sheetBlink = null;
        };
        Tapped += (s, e) => { if (_hopT < 0) _hopT = 0; _heartT = 0; Hook(); };
        Loaded += (s, e) => Hook();
        Unloaded += (s, e) => Unhook();
        ToolTipService.SetToolTip(this, "♪");
    }

    /// <summary>音乐是否在播放（晃腿）。bpm &gt; 0 时腿按两拍一个来回晃。</summary>
    public void SetPlaying(bool playing, double bpm)
    {
        _playing = playing;
        if (bpm is > 40 and < 260) _beatPeriod = Math.Clamp(240.0 / bpm, 1.2, 3.2);   // 四拍晃一个来回
        Hook();
    }

    private void Hook()
    {
        if (_hooked || !IsLoaded) return;
        _hooked = true;
        _last = _clock.Elapsed.TotalSeconds;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
    }

    private double _frameAcc;

    private void OnFrame(object? sender, object e)
    {
        double now = _clock.Elapsed.TotalSeconds, dt = Math.Min(0.1, now - _last);
        _frameAcc += dt;
        _last = now;
        if (_frameAcc < 1.0 / 40) return;   // 小装饰，40 帧足够
        dt = _frameAcc; _frameAcc = 0;

        double target = _playing ? 1 : 0;
        _swing += (target - _swing) * (1 - Math.Exp(-dt * 3));
        _phase += dt * 2 * Math.PI / _beatPeriod * (0.25 + 0.75 * _swing);
        if (_hopT >= 0) { _hopT += dt; if (_hopT > 0.55) _hopT = -1; }
        if (_heartT >= 0) { _heartT += dt; if (_heartT > 1.3) _heartT = -1; }
        _t += dt;
        _hairPhase += dt * (1.1 + 2 * Math.PI / _beatPeriod * 0.5 * _swing);

        // 眨眼：每 2~6 秒一次
        _blinkAt -= dt;
        if (_blinkAt <= 0 && _blinkT < 0) { _blinkT = 0; _blinkAt = 2 + _rng.NextDouble() * 4; }
        if (_blinkT >= 0) { _blinkT += dt; if (_blinkT > 0.16) _blinkT = -1; }
        // 歪头：暂停时偶尔歪一下，播放时跟着节拍轻轻点头
        _tiltAt -= dt;
        if (_tiltAt <= 0) { _tiltTarget = _rng.NextDouble() < 0.5 ? 0 : (_rng.NextDouble() < 0.5 ? -9 : 9); _tiltAt = 3 + _rng.NextDouble() * 5; }
        _tilt += (_tiltTarget - _tilt) * (1 - Math.Exp(-dt * 4));

        _canvas.Invalidate();
        // 暂停、腿已经停稳、没在跳也没在眨眼时，降到每秒几帧（只为眨眼）
        // 图片模式暂停时头发和呼吸还在慢慢动，降到 20 帧左右
        if (!_playing && _swing < 0.01 && _hopT < 0 && _heartT < 0 && _blinkT < 0 && Math.Abs(_tiltTarget - _tilt) < 0.1)
            _frameAcc = _image != null ? -1.0 / 40 : -0.2;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        double hop = _hopT >= 0 ? Math.Sin(_hopT / 0.55 * Math.PI) * 16 : 0;
        double bob = Math.Abs(Math.Sin(_phase)) * 1.5 * _swing;   // 跟着节拍轻轻上下

        if (!_imageTried)
        {
            _imageTried = true;
            _ = LoadImageAsync(sender);
        }
        if (_image != null) { DrawImage(sender, ds, hop); return; }
        DrawMascot(ds, (float)(hop + bob));
    }

    private async System.Threading.Tasks.Task LoadImageAsync(CanvasControl sender)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "EasterEgg", "miku.png");
            if (!File.Exists(path)) return;
            _image = await CanvasBitmap.LoadAsync(sender, path);
            sender.Invalidate();
        }
        catch (Exception ex) { App.LogError("EasterEgg", ex.Message); }
    }

    // ---------------- 图片模式：把图切成小格子分别位移，做出局部动作 ----------------
    // 下面的坐标都是 miku.png 原图（384 x 547）里的像素位置，换图时要跟着改。
    private const double RefW = 384;
    private static readonly Vector2 NeckPivot = new(188, 190);   // 歪头 / 点头绕这里转
    private static readonly Vector2 KneePivot = new(122, 372);   // 小腿绕这里摆
    private const double SeatRefY = 390;                          // 坐着的位置（大腿下沿）
    // 眨眼：盖住两只眼睛的椭圆（中心、半径）和闭眼的弧线
    private static readonly (Vector2 C, Vector2 R, Vector2 A, Vector2 C1, Vector2 C2, Vector2 B)[] Eyes =
    {
        (new(150, 134), new(20, 20), new(132, 134), new(140, 146), new(158, 146), new(166, 133)),
        (new(216, 123), new(23, 23), new(196, 124), new(206, 136), new(227, 136), new(237, 122)),
    };
    private static readonly Color FaceSkin = Color.FromArgb(255, 254, 235, 223);

    private CanvasRenderTarget? _sheet, _sheetBlink;   // 按显示分辨率缩好的图（睁眼 / 闭眼）
    private float _sheetDpi;
    private double _t;                                  // 总时间（头发飘、呼吸）
    private double _hairPhase;
    private double _heartT = -1;                        // 点击后飘起的小爱心
    private Vector2[] _grid = Array.Empty<Vector2>();

    private void EnsureSheets(CanvasControl sender)
    {
        float scale = sender.Dpi / 96f;
        if (_sheet != null && Math.Abs(_sheetDpi - scale) < 0.01f) return;
        _sheet?.Dispose(); _sheetBlink?.Dispose();
        _sheetDpi = scale;
        var img = _image!;
        float iw = (float)img.SizeInPixels.Width, ih = (float)img.SizeInPixels.Height;
        int pw = (int)Math.Round(ImageWidth * scale), ph = (int)Math.Round(ih * pw / iw);

        // 闭眼版：先在原图分辨率上用肤色盖住眼睛、画一道弯弯的闭眼线
        using var closed = new CanvasRenderTarget(sender, iw, ih, 96);
        using (var ds = closed.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
            ds.DrawImage(img, new Windows.Foundation.Rect(0, 0, iw, ih));
            float k = (float)(iw / RefW);
            foreach (var e in Eyes)
            {
                ds.FillEllipse(e.C * k, e.R.X * k, e.R.Y * k, FaceSkin);
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(e.A * k);
                pb.AddCubicBezier(e.C1 * k, e.C2 * k, e.B * k);
                pb.EndFigure(CanvasFigureLoop.Open);
                using var g = CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(g, Color.FromArgb(255, 38, 38, 46), 4.2f * k,
                    new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round });
            }
        }
        _sheet = Shrink(sender, img, pw, ph);
        _sheetBlink = Shrink(sender, closed, pw, ph);
    }

    private static CanvasRenderTarget Shrink(CanvasControl sender, CanvasBitmap src, int pw, int ph)
    {
        var rt = new CanvasRenderTarget(sender, pw, ph, 96);
        using var ds = rt.CreateDrawingSession();
        ds.Clear(Colors.Transparent);
        ds.DrawImage(src, new Windows.Foundation.Rect(0, 0, pw, ph),
            new Windows.Foundation.Rect(0, 0, src.SizeInPixels.Width, src.SizeInPixels.Height), 1f, CanvasImageInterpolation.HighQualityCubic);
        return rt;
    }

    private static double Smooth(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>原图坐标 (x, y) 处的位移（原图像素）：身体左右轻摇和呼吸、头跟着节拍点、双马尾飘、小腿晃。</summary>
    private Vector2 Displace(double x, double y, double lean, double breathe, double headRad, double legRad, double hairAmp)
    {
        double dx = 0, dy = 0;
        // 上半身以座位为轴左右轻摇，并随呼吸微微起伏
        if (y < SeatRefY)
        {
            double up = (SeatRefY - y) / SeatRefY;
            dx += lean * up;
            dy -= breathe * up;
        }
        // 头：绕脖子转，越往下影响越小
        double wh = 1 - Smooth(150, 205, y);
        if (wh > 0)
        {
            double px = x - NeckPivot.X, py = y - NeckPivot.Y, c = Math.Cos(headRad) - 1, s = Math.Sin(headRad);
            dx += (px * c - py * s) * wh;
            dy += (px * s + py * c) * wh;
        }
        // 小腿：绕膝盖摆
        double wl = Smooth(355, 395, y) * Smooth(40, 82, x) * (1 - Smooth(175, 205, x));
        if (wl > 0)
        {
            double px = x - KneePivot.X, py = y - KneePivot.Y, c = Math.Cos(legRad) - 1, s = Math.Sin(legRad);
            dx += (px * c - py * s) * wl;
            dy += (px * s + py * c) * wl;
        }
        // 双马尾：两侧的头发，越往发梢摆得越多，像被风吹着一波一波地动
        double left = 1 - Smooth(72, 100, x), right = Smooth(262, 292, x);
        if ((left > 0 || right > 0) && y > 170)
        {
            double wv = Math.Pow(Math.Min(1, (y - 170) / 377), 1.5) * (1 - wl);
            dx += wv * hairAmp * (left * Math.Sin(_hairPhase - y / 85) + right * Math.Sin(_hairPhase + 0.9 - y / 85));
            dy += wv * hairAmp * 0.25 * (left + right) * Math.Cos(_hairPhase * 2 - y / 70);
        }
        return new Vector2((float)dx, (float)dy);
    }

    /// <summary>
    /// 用户提供的图片：座位对齐封面上边缘。图切成小格子（Win2D sprite batch）逐格位移，
    /// 所以身体能摇、头能点、头发能飘、腿能晃；还会眨眼，踩到节拍时轻轻一压。
    /// </summary>
    private void DrawImage(CanvasControl sender, CanvasDrawingSession ds, double lift)
    {
        EnsureSheets(sender);
        var sheet = _blinkT >= 0 ? _sheetBlink! : _sheet!;
        float pw = sheet.SizeInPixels.Width, ph = sheet.SizeInPixels.Height;
        float w = (float)ImageWidth, h = (float)(ph * ImageWidth / pw);
        float left = (float)((ControlWidth - w) / 2), top = (float)(SeatY - h * ImageSeatFraction - lift);

        // _phase 每四拍转一圈
        var basis = Matrix3x2.CreateScale(w / pw) * Matrix3x2.CreateTranslation(left, top);

        double idle = 1 - _swing;
        double lean = 7 * Math.Sin(_phase) * _swing + 1.6 * Math.Sin(_t * 0.5) * idle;
        double breathe = 1.0 * (0.5 + 0.5 * Math.Sin(_t * 1.3));
        double headDeg = _tilt * 0.45 + 3 * Math.Sin(_phase * 2 - 0.6) * _swing + (_hopT >= 0 ? 4 * Math.Sin(_hopT / 0.55 * Math.PI) : 0);
        double legDeg = 6 * Math.Sin(_phase + 0.8) * _swing + 2.5 * Math.Sin(_t * 1.3) * idle;
        double hairAmp = 6 + 10 * _swing;
        double toRef = RefW / pw;   // 缩好的图 → 原图坐标

        if (!CanvasSpriteBatch.IsSupported(sender.Device))
        {
            ds.Transform = basis;
            ds.DrawImage(sheet);
            ds.Transform = Matrix3x2.Identity;
            return;
        }
        // 先算网格顶点的位移，每格按左上、右上、左下三个顶点做仿射变形，相邻格子的边能对上，不会裂开
        const int cell = 5;
        int nx = (int)Math.Ceiling(pw / cell) + 1, ny = (int)Math.Ceiling(ph / cell) + 1;
        if (_grid.Length < nx * ny) _grid = new Vector2[nx * ny];
        double hr = headDeg * Math.PI / 180, lr = legDeg * Math.PI / 180;
        for (int j = 0; j < ny; j++)
        for (int i = 0; i < nx; i++)
        {
            float gx = i * cell, gy = j * cell;
            _grid[j * nx + i] = new Vector2(gx, gy) + Displace(gx * toRef, gy * toRef, lean, breathe, hr, lr, hairAmp) / (float)toRef;
        }
        using (var batch = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear, CanvasSpriteOptions.None))
        {
            for (int j = 0; j < ny - 1; j++)
            for (int i = 0; i < nx - 1; i++)
            {
                int cx = i * cell, cy = j * cell;
                if (cx >= pw || cy >= ph) continue;
                Vector2 p00 = _grid[j * nx + i], ex = (_grid[j * nx + i + 1] - p00) / cell, ey = (_grid[(j + 1) * nx + i] - p00) / cell;
                var m = new Matrix3x2(ex.X, ex.Y, ey.X, ey.Y, p00.X, p00.Y);
                // 每格向外多取 1 像素，盖住仿射近似留下的细缝
                float x0 = Math.Max(0, cx - 1), y0 = Math.Max(0, cy - 1);
                float x1 = Math.Min(pw, cx + cell + 1), y1 = Math.Min(ph, cy + cell + 1);
                batch.DrawFromSpriteSheet(sheet, Matrix3x2.CreateTranslation(x0 - cx, y0 - cy) * m * basis,
                    new Windows.Foundation.Rect(x0, y0, x1 - x0, y1 - y0));
            }
        }

        // 播放时头顶飘出小音符；点一下飘出小爱心
        var head = Vector2.Transform(new Vector2((float)(250 / toRef), (float)(30 / toRef)), basis);
        if (_swing > 0.5)
        {
            double t = (_phase / (2 * Math.PI)) % 2.0;
            if (t < 1.2) DrawNote(ds, head.X + (float)(t * 8), head.Y - (float)(t * 18), (float)(1 - t / 1.2));
        }
        if (_heartT >= 0)
        {
            float a = (float)Math.Clamp(1.4 - _heartT / 0.9, 0, 1);
            var hp = Vector2.Transform(new Vector2((float)(120 / toRef), (float)(20 / toRef)), basis);
            DrawHeart(ds, hp.X - (float)(_heartT * 6), hp.Y - (float)(_heartT * 26), 1 + (float)Math.Min(1, _heartT * 4) * 0.2f, a);
        }
    }

    private static void DrawNote(CanvasDrawingSession ds, float nx, float ny, float a)
    {
        var c = Color.FromArgb((byte)(230 * a), 57, 197, 187);
        ds.FillEllipse(nx, ny + 7, 3.2f, 2.5f, c);
        ds.DrawLine(nx + 2.8f, ny + 7, nx + 2.8f, ny - 3, c, 1.3f);
        ds.DrawLine(nx + 2.8f, ny - 3, nx + 6.5f, ny - 1, c, 1.3f);
    }

    private static void DrawHeart(CanvasDrawingSession ds, float x, float y, float s, float a)
    {
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(x, y + 5 * s);
        pb.AddCubicBezier(new Vector2(x - 8 * s, y - 1 * s), new Vector2(x - 4 * s, y - 7 * s), new Vector2(x, y - 3 * s));
        pb.AddCubicBezier(new Vector2(x + 4 * s, y - 7 * s), new Vector2(x + 8 * s, y - 1 * s), new Vector2(x, y + 5 * s));
        pb.EndFigure(CanvasFigureLoop.Closed);
        using var g = CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(g, Color.FromArgb((byte)(235 * a), 255, 120, 150));
    }

    // ---------------- 原创 Q 版形象（Klang 吉祥物） ----------------

    private static readonly Color Skin = Color.FromArgb(255, 255, 228, 214);
    private static readonly Color Blush = Color.FromArgb(110, 255, 140, 150);
    private static readonly Color Hair = Color.FromArgb(255, 176, 156, 222);       // 淡紫
    private static readonly Color HairShade = Color.FromArgb(255, 140, 118, 196);
    private static readonly Color Hoodie = Color.FromArgb(255, 250, 244, 232);     // 奶油色卫衣
    private static readonly Color HoodieShade = Color.FromArgb(255, 226, 216, 198);
    private static readonly Color Phones = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color PhonesAccent = Color.FromArgb(255, 255, 138, 101); // 珊瑚色耳机罩
    private static readonly Color Shorts = Color.FromArgb(255, 92, 104, 140);
    private static readonly Color Sock = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color Shoe = Color.FromArgb(255, 255, 138, 101);
    private static readonly Color Ink = Color.FromArgb(255, 70, 52, 90);

    private void DrawMascot(CanvasDrawingSession ds, float lift)
    {
        float cx = (float)(ControlWidth / 2), seat = (float)SeatY - lift;
        float swing = (float)(Math.Sin(_phase) * 22 * _swing);   // 腿摆角（度），左右腿反相

        // 腿：从坐的位置垂下，越过封面边缘，绕膝盖前后摆（侧视的前后摆在正面看是左右小幅摆动加长短变化）
        DrawLeg(ds, cx - 9, seat - 2, swing);
        DrawLeg(ds, cx + 9, seat - 2, -swing);

        // 身体（卫衣）+ 短裤边
        using (var body = new CanvasPathBuilder(ds))
        {
            body.BeginFigure(cx - 17, seat);
            body.AddLine(cx - 14, seat - 26);
            body.AddCubicBezier(new Vector2(cx - 10, seat - 33), new Vector2(cx + 10, seat - 33), new Vector2(cx + 14, seat - 26));
            body.AddLine(cx + 17, seat);
            body.EndFigure(CanvasFigureLoop.Closed);
            using var g = CanvasGeometry.CreatePath(body);
            ds.FillGeometry(g, Hoodie);
            ds.DrawGeometry(g, HoodieShade, 1.2f);
        }
        ds.FillRoundedRectangle(cx - 18, seat - 5, 36, 7, 3, 3, Shorts);
        // 卫衣口袋和抽绳
        ds.DrawLine(cx - 8, seat - 12, cx + 8, seat - 12, HoodieShade, 1.2f);
        ds.DrawLine(cx - 3, seat - 30, cx - 4, seat - 21, HoodieShade, 1.1f);
        ds.DrawLine(cx + 3, seat - 30, cx + 4, seat - 21, HoodieShade, 1.1f);

        // 手：撑在身体两侧的封面边缘上
        ds.FillEllipse(cx - 21, seat - 2, 4.2f, 3.4f, Skin);
        ds.FillEllipse(cx + 21, seat - 2, 4.2f, 3.4f, Skin);
        ds.DrawLine(cx - 15, seat - 22, cx - 20, seat - 4, Hoodie, 6.5f);
        ds.DrawLine(cx + 15, seat - 22, cx + 20, seat - 4, Hoodie, 6.5f);

        // 头（带歪头）
        float headCy = seat - 52;
        float tiltRad = (float)(_tilt * Math.PI / 180);
        var saved = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation(tiltRad, new Vector2(cx, seat - 30)) * saved;

        // 后面的头发
        ds.FillEllipse(cx, headCy + 3, 27, 26, HairShade);
        // 脸
        ds.FillEllipse(cx, headCy + 4, 23, 21, Skin);
        // 刘海（几片圆弧）和头顶一撮呆毛
        using (var bangs = new CanvasPathBuilder(ds))
        {
            bangs.BeginFigure(cx - 25, headCy + 6);
            bangs.AddCubicBezier(new Vector2(cx - 26, headCy - 24), new Vector2(cx + 26, headCy - 24), new Vector2(cx + 25, headCy + 6));
            bangs.AddCubicBezier(new Vector2(cx + 20, headCy - 4), new Vector2(cx + 14, headCy - 6), new Vector2(cx + 9, headCy - 2));
            bangs.AddCubicBezier(new Vector2(cx + 5, headCy - 8), new Vector2(cx - 1, headCy - 8), new Vector2(cx - 4, headCy - 1));
            bangs.AddCubicBezier(new Vector2(cx - 9, headCy - 7), new Vector2(cx - 17, headCy - 5), new Vector2(cx - 25, headCy + 6));
            bangs.EndFigure(CanvasFigureLoop.Closed);
            using var g = CanvasGeometry.CreatePath(bangs);
            ds.FillGeometry(g, Hair);
        }
        using (var tuft = new CanvasPathBuilder(ds))
        {
            tuft.BeginFigure(cx + 1, headCy - 17);
            tuft.AddCubicBezier(new Vector2(cx + 3, headCy - 30), new Vector2(cx + 12, headCy - 30), new Vector2(cx + 9, headCy - 24));
            tuft.EndFigure(CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(tuft);
            ds.DrawGeometry(g, Hair, 2.4f);
        }
        // 两侧的短发
        ds.FillEllipse(cx - 22, headCy + 12, 6, 11, Hair);
        ds.FillEllipse(cx + 22, headCy + 12, 6, 11, Hair);

        // 大耳机：头箍 + 两个耳罩
        using (var band = new CanvasPathBuilder(ds))
        {
            band.BeginFigure(cx - 27, headCy + 6);
            band.AddCubicBezier(new Vector2(cx - 29, headCy - 30), new Vector2(cx + 29, headCy - 30), new Vector2(cx + 27, headCy + 6));
            band.EndFigure(CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(band);
            ds.DrawGeometry(g, Phones, 4.2f);
            ds.DrawGeometry(g, Ink, 0.8f);
        }
        foreach (float side in new[] { -1f, 1f })
        {
            float ex = cx + side * 26;
            ds.FillRoundedRectangle(ex - 6, headCy, 12, 17, 5, 5, Phones);
            ds.FillRoundedRectangle(ex - 4, headCy + 2, 8, 13, 4, 4, PhonesAccent);
            ds.DrawRoundedRectangle(ex - 6, headCy, 12, 17, 5, 5, Ink, 0.8f);
        }

        // 眼睛：眨眼时变成弧线；播放时随节拍开心地眯一下
        bool blink = _blinkT >= 0;
        float eyeY = headCy + 7;
        foreach (float side in new[] { -1f, 1f })
        {
            float ex = cx + side * 9;
            if (blink)
            {
                ds.DrawLine(ex - 3.5f, eyeY, ex + 3.5f, eyeY, Ink, 1.8f);
            }
            else
            {
                ds.FillEllipse(ex, eyeY, 3.6f, 4.6f, Ink);
                ds.FillEllipse(ex + 1.1f, eyeY - 1.6f, 1.3f, 1.4f, Colors.White);
                ds.FillEllipse(ex - 1.2f, eyeY + 1.8f, 0.7f, 0.7f, Color.FromArgb(200, 255, 255, 255));
            }
        }
        // 腮红和嘴
        ds.FillEllipse(cx - 15, headCy + 13, 4, 2.4f, Blush);
        ds.FillEllipse(cx + 15, headCy + 13, 4, 2.4f, Blush);
        using (var mouth = new CanvasPathBuilder(ds))
        {
            float my = headCy + 14;
            mouth.BeginFigure(cx - 3, my);
            mouth.AddCubicBezier(new Vector2(cx - 1.5f, my + 2.6f), new Vector2(cx + 1.5f, my + 2.6f), new Vector2(cx + 3, my));
            mouth.EndFigure(CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(mouth);
            ds.DrawGeometry(g, Ink, 1.2f);
        }
        ds.Transform = saved;

        // 播放时头顶偶尔飘出一个小音符
        if (_swing > 0.5)
        {
            double t = (_phase / (2 * Math.PI)) % 2.0;
            if (t < 1.2)
            {
                float a = (float)(1 - t / 1.2), nx = cx + 30 + (float)(t * 8), ny = headCy - 18 - (float)(t * 18);
                var noteColor = Color.FromArgb((byte)(220 * a), 255, 138, 101);
                ds.FillEllipse(nx, ny + 7, 3.2f, 2.5f, noteColor);
                ds.DrawLine(nx + 2.8f, ny + 7, nx + 2.8f, ny - 3, noteColor, 1.3f);
                ds.DrawLine(nx + 2.8f, ny - 3, nx + 6.5f, ny - 1, noteColor, 1.3f);
            }
        }
    }

    /// <summary>一条腿：大腿从坐处向前（朝向观看者）伸出，正面看很短；小腿垂过封面边缘，绕膝盖摆动。</summary>
    private static void DrawLeg(CanvasDrawingSession ds, float hipX, float hipY, float swingDeg)
    {
        // 正面看：摆到前面时小腿变短（朝向观看者），摆到后面时略变长；同时轻微左右偏
        double rad = swingDeg * Math.PI / 180;
        float shin = (float)(24 - Math.Sin(rad) * 6);
        float sway = (float)(Math.Sin(rad) * 3.5);
        float knee = hipY + 5;
        float footX = hipX + sway, footY = knee + shin;
        ds.DrawLine(hipX, hipY, hipX, knee, Skin, 8f);
        ds.DrawLine(hipX, knee, footX, footY - 6, Skin, 7f);
        ds.DrawLine(footX, footY - 9, footX, footY - 2, Sock, 7.2f);
        ds.FillEllipse(footX + 1, footY, 6.2f, 3.8f, Shoe);
        ds.FillEllipse(footX + 1, footY + 2.4f, 6.2f, 1.6f, Color.FromArgb(255, 255, 255, 255));
    }
}
