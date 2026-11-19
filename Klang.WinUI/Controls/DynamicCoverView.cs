using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Klang_WinUI.Controls;

/// <summary>
/// 动态封面：盖在静态封面上的短视频，静音循环播放，跟随音乐播放 / 暂停。
/// 视频真正出画面后才淡入，换歌或没有动态封面时淡出，露出下面的静态封面。
/// MediaPlayerElement 的视频画面不受圆角裁剪，所以这里用帧服务器模式：每一帧复制到 Win2D 位图，
/// 再按封面的圆角和 UniformToFill 方式画出来。不接入系统媒体控制，也不响应鼠标。
/// </summary>
public sealed class DynamicCoverView : UserControl
{
    private readonly CanvasControl _canvas = new() { IsHitTestVisible = false };
    private readonly object _frameLock = new();
    private CanvasDevice? _device;
    private CanvasRenderTarget? _frame;
    private bool _hasFrame;

    private MediaPlayer? _player;
    private string? _path;
    private bool _playing, _shown;
    private int _seq;

    public DynamicCoverView()
    {
        IsHitTestVisible = false;
        IsTabStop = false;
        Content = _canvas;
        ElementCompositionPreview.GetElementVisual(_canvas).Opacity = 0f;
        _canvas.CreateResources += (s, e) => { lock (_frameLock) { _device = s.Device; _frame = null; _hasFrame = false; } };
        _canvas.Draw += OnDraw;
        // 布局切换时控件可能暂时移出界面树：期间只暂停，回来后按原状态继续
        Unloaded += (s, e) => _player?.Pause();
        Loaded += (s, e) => ApplyPlayState();
    }

    /// <summary>圆角半径，和外层封面的圆角一致。</summary>
    public double ClipRadius { get; set; }

    /// <summary>有动态封面时传本地视频路径，没有传 null；playing = 音乐是否在播放。</summary>
    public void Set(string? path, bool playing)
    {
        _playing = playing;
        if (path != _path)
        {
            _path = path;
            int seq = ++_seq;
            if (path == null)
            {
                Fade(false, 260, () =>
                {
                    if (seq != _seq) return;
                    _player?.Pause();
                    if (_player != null) _player.Source = null;
                    lock (_frameLock) _hasFrame = false;
                });
                return;
            }
            HideNow();
            lock (_frameLock) _hasFrame = false;
            _ = OpenAsync(path, seq);
            return;
        }
        ApplyPlayState();
    }

    private async System.Threading.Tasks.Task OpenAsync(string path, int seq)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (seq != _seq) return;
            EnsurePlayer().Source = MediaSource.CreateFromStorageFile(file);
            ApplyPlayState();
        }
        catch (Exception ex)
        {
            App.LogError("DynamicCover", ex.Message);
        }
    }

    private MediaPlayer EnsurePlayer()
    {
        if (_player != null) return _player;
        var p = new MediaPlayer
        {
            IsMuted = true,
            IsLoopingEnabled = true,
            AutoPlay = false,
            IsVideoFrameServerEnabled = true
        };
        p.CommandManager.IsEnabled = false;   // 不出现在系统媒体控制里
        p.VideoFrameAvailable += OnVideoFrame;
        p.MediaFailed += (s, e) => App.LogError("DynamicCover", e.ErrorMessage);
        _player = p;
        return p;
    }

    private int _framePending;

    /// <summary>
    /// 播放线程通知有新帧：只转给界面线程，在那里复制并重画。
    /// 复制会用到和界面共用的 GPU 设备，放在播放线程上做会和界面线程的绘制同时访问设备，导致崩溃。
    /// 界面线程忙不过来时，中间的帧直接丢掉（只保留一个待处理的通知）。
    /// </summary>
    private void OnVideoFrame(MediaPlayer sender, object args)
    {
        if (System.Threading.Interlocked.Exchange(ref _framePending, 1) == 1) return;
        int seq = _seq;
        if (DispatcherQueue?.TryEnqueue(() =>
            {
                System.Threading.Interlocked.Exchange(ref _framePending, 0);
                if (seq != _seq || _path == null) return;
                CopyFrame(sender);
            }) != true)
            System.Threading.Interlocked.Exchange(ref _framePending, 0);
    }

    /// <summary>界面线程：把播放器当前的画面复制到位图并重画。</summary>
    private void CopyFrame(MediaPlayer player)
    {
        bool first;
        lock (_frameLock)
        {
            if (_device == null) return;
            uint w = player.PlaybackSession.NaturalVideoWidth, h = player.PlaybackSession.NaturalVideoHeight;
            if (w == 0 || h == 0) return;
            try
            {
                if (_frame == null || _frame.SizeInPixels.Width != w || _frame.SizeInPixels.Height != h)
                {
                    _frame?.Dispose();
                    _frame = new CanvasRenderTarget(_device, w, h, 96);
                }
                player.CopyFrameToVideoSurface(_frame);
            }
            catch { return; }   // 设备丢失等：等 CreateResources 重建
            first = !_hasFrame;
            _hasFrame = true;
        }
        _canvas.Invalidate();
        if (first && !_shown) Fade(true, 420);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        lock (_frameLock)
        {
            if (!_hasFrame || _frame == null) return;
            float cw = (float)sender.ActualWidth, ch = (float)sender.ActualHeight;
            var src = _frame.Size;
            if (cw <= 0 || ch <= 0 || src.Width <= 0 || src.Height <= 0) return;
            // UniformToFill：铺满封面，多出的部分居中裁掉
            double scale = Math.Max(cw / src.Width, ch / src.Height);
            double sw = cw / scale, sh = ch / scale;
            var srcRect = new Rect((src.Width - sw) / 2, (src.Height - sh) / 2, sw, sh);
            var ds = args.DrawingSession;
            float r = (float)ClipRadius;
            using var clip = CanvasGeometry.CreateRoundedRectangle(ds, 0, 0, cw, ch, r, r);
            using (ds.CreateLayer(1f, clip))
                ds.DrawImage(_frame, new Rect(0, 0, cw, ch), srcRect, 1f, CanvasImageInterpolation.Linear);
        }
    }

    /// <summary>淡入 / 淡出视频层（合成层动画，不占界面线程）。</summary>
    private void Fade(bool show, int ms, Action? done = null)
    {
        _shown = show;
        var v = ElementCompositionPreview.GetElementVisual(_canvas);
        var c = v.Compositor;
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertExpressionKeyFrame(0f, "this.StartingValue");
        a.InsertKeyFrame(1f, show ? 1f : 0f);
        a.Duration = TimeSpan.FromMilliseconds(ms);
        var batch = c.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
        v.StartAnimation("Opacity", a);
        batch.End();
        if (done != null) batch.Completed += (s, e) => done();
    }

    private void HideNow()
    {
        _shown = false;
        var v = ElementCompositionPreview.GetElementVisual(_canvas);
        v.StopAnimation("Opacity");
        v.Opacity = 0f;
    }

    private void ApplyPlayState()
    {
        if (_player == null || _path == null) return;
        if (_playing) _player.Play(); else _player.Pause();
    }
}
