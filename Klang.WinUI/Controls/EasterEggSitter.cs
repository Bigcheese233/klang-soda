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
    public const double ControlWidth = 140, ControlHeight = 200;
    /// <summary>控件里封面上边缘所在的高度（从顶部算）。</summary>
    public const double SeatY = 140;
    /// <summary>图片模式：图片宽度，以及图片里“座位”（坐着的横条上沿）所在的高度比例。</summary>
    private const double ImageWidth = 128, ImageSeatFraction = 0.71;
    public const double LegRoom = ControlHeight - SeatY;

    private readonly CanvasControl _canvas = new();
    private CanvasBitmap? _image;
    private bool _imageTried;

    private bool _playing;
    private double _beatPeriod = 0.9;   // 腿晃一个来回的时间（秒）
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
        _canvas.CreateResources += (s, e) => { _image = null; _imageTried = false; };
        Tapped += (s, e) => { if (_hopT < 0) _hopT = 0; Hook(); };
        Loaded += (s, e) => Hook();
        Unloaded += (s, e) => Unhook();
        ToolTipService.SetToolTip(this, "♪");
    }

    /// <summary>音乐是否在播放（晃腿）。bpm &gt; 0 时腿按两拍一个来回晃。</summary>
    public void SetPlaying(bool playing, double bpm)
    {
        _playing = playing;
        if (bpm is > 40 and < 260) _beatPeriod = Math.Clamp(120.0 / bpm, 0.5, 1.6);
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
        if (!_playing && _swing < 0.01 && _hopT < 0 && _blinkT < 0 && Math.Abs(_tiltTarget - _tilt) < 0.1) _frameAcc = -0.2;
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
        if (_image != null) { DrawImage(ds, hop + bob); return; }
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

    /// <summary>用户提供的图片：座位（横条上沿）对齐封面上边缘，整体以座位为轴随节拍轻轻左右摇。</summary>
    private void DrawImage(CanvasDrawingSession ds, double lift)
    {
        var img = _image!;
        float w = (float)ImageWidth, h = (float)(img.Size.Height * ImageWidth / img.Size.Width);
        float left = (float)((ControlWidth - w) / 2), top = (float)(SeatY - h * ImageSeatFraction - lift);
        float rock = (float)(Math.Sin(_phase) * 3 * _swing * Math.PI / 180);
        ds.Transform = Matrix3x2.CreateRotation(rock, new Vector2((float)(ControlWidth / 2), (float)SeatY));
        ds.DrawImage(img, new Windows.Foundation.Rect(left, top, w, h), new Windows.Foundation.Rect(0, 0, img.Size.Width, img.Size.Height),
            1f, CanvasImageInterpolation.HighQualityCubic);
        ds.Transform = Matrix3x2.Identity;
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
