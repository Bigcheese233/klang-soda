using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klang_WinUI.Models;
using Klang_WinUI.Services;
using Klang_WinUI.Services.Cloud;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Klang_WinUI.Controls;

/// <summary>
/// MV 播放层：盖住整个窗口，用 MV 自己的声音播放（MV 的剪辑和歌曲版本往往不同，没法和正在播放的歌对齐）。
/// 顶部左侧是返回和标题；底部是自己的控制条（进度、播放暂停、时间、音量、画质、全屏）。
/// 不用系统自带的控制条：它依赖播放器的系统媒体控制，而这里关掉了系统媒体控制（那是 Klang 主播放器的），
/// 自带控制条的播放按钮会一直不可用。单击画面暂停 / 继续，双击全屏；鼠标静止 3 秒后控制条淡出。视频在线播放，不缓存。
/// </summary>
public sealed class MvPlayerView : UserControl
{
    public event EventHandler? CloseRequested;
    public event EventHandler? FullscreenRequested;

    private readonly MediaPlayerElement _element = new()
    {
        AreTransportControlsEnabled = false,
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        IsHitTestVisible = false
    };
    private readonly MediaPlayer _player = new() { AutoPlay = true };

    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = White(0xFF), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressRing _ring = new() { Width = 40, Height = 40, IsActive = false, Foreground = White(0xFF), IsHitTestVisible = false };
    private readonly TextBlock _message = new() { Foreground = White(0xCC), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false };

    // 底部控制条
    private readonly Slider _progress = new() { Minimum = 0, Maximum = 1, StepFrequency = 0.1, IsThumbToolTipEnabled = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = new() { FontSize = 12, Foreground = White(0xCC), VerticalAlignment = VerticalAlignment.Center, Text = "0:00 / 0:00" };
    private readonly FontIcon _playIcon = new() { Glyph = "", FontSize = 18 };
    private readonly FontIcon _volumeIcon = new() { Glyph = "", FontSize = 15 };
    private readonly FontIcon _fullIcon = new() { Glyph = "", FontSize = 15 };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, Width = 96, IsThumbToolTipEnabled = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _quality = new() { MinWidth = 92, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _topBar, _bottomBar;

    private readonly DispatcherQueueTimer _tick, _idle;
    private TrackItem? _track;
    private ICloudSource? _source;
    private CancellationTokenSource? _cts;
    private bool _updatingQuality, _updatingProgress, _barsShown = true;
    private Button _backButton = null!, _fullButton = null!;
    private bool _pointerOverBar;

    private static SolidColorBrush White(byte a) => new(Windows.UI.Color.FromArgb(a, 255, 255, 255));

    public MvPlayerView()
    {
        RequestedTheme = ElementTheme.Dark;
        Visibility = Visibility.Collapsed;
        _player.CommandManager.IsEnabled = false;   // 系统媒体控制和媒体键仍然归 Klang 主播放器管，由主窗口转发
        _player.MediaFailed += (s, e) => DispatcherQueue.TryEnqueue(() => ShowMessage(L.T("MV 播放失败：") + e.ErrorMessage));
        _player.PlaybackSession.PlaybackStateChanged += (s, e) => DispatcherQueue.TryEnqueue(UpdatePlayState);
        _element.SetMediaPlayer(_player);

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(250);
        _tick.Tick += (s, e) => UpdateProgress();
        _idle = DispatcherQueue.CreateTimer();
        _idle.Interval = TimeSpan.FromSeconds(3);
        _idle.IsRepeating = false;
        _idle.Tick += (s, e) =>
        {
            if (!IsPlaying) return;
            if (_pointerOverBar) { _idle.Start(); return; }   // 鼠标还停在控制条上：过一会儿再看
            ShowBars(false);
        };

        _topBar = BuildTopBar();
        _bottomBar = BuildBottomBar();

        // 画面区域：单击暂停 / 继续，双击全屏
        var surface = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        surface.Tapped += (s, e) => TogglePlayPause();
        // 鼠标停在控制条上时不自动隐藏
        foreach (var bar in new[] { _topBar, _bottomBar })
        {
            bar.PointerEntered += (s, e) => _pointerOverBar = true;
            bar.PointerExited += (s, e) => _pointerOverBar = false;
        }
        surface.DoubleTapped += (s, e) => { TogglePlayPause(); FullscreenRequested?.Invoke(this, EventArgs.Empty); };

        var root = new Grid { Background = new SolidColorBrush(Colors.Black) };
        root.Children.Add(_element);
        root.Children.Add(surface);
        root.Children.Add(_ring);
        root.Children.Add(_message);
        root.Children.Add(_topBar);
        root.Children.Add(_bottomBar);
        root.PointerMoved += (s, e) => WakeBars();
        Content = root;
    }

    private Grid BuildTopBar()
    {
        var back = _backButton = IconButton("", L.T("关闭 MV (Esc)"));
        back.Click += (s, e) => CloseRequested?.Invoke(this, EventArgs.Empty);
        var bar = new Grid { Padding = new Thickness(12, 10, 16, 28), ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Top, Background = Fade(top: true) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.Children.Add(back);
        Grid.SetColumn(_title, 1); bar.Children.Add(_title);
        return bar;
    }

    private Grid BuildBottomBar()
    {
        var play = IconButton(_playIcon, L.T("播放 / 暂停 (空格)"));
        play.Click += (s, e) => TogglePlayPause();
        var mute = IconButton(_volumeIcon, L.T("静音"));
        mute.Click += (s, e) => { _player.IsMuted = !_player.IsMuted; UpdateVolumeIcon(); };
        var full = _fullButton = IconButton(_fullIcon, L.T("全屏"));
        full.Click += (s, e) => FullscreenRequested?.Invoke(this, EventArgs.Empty);
        ToolTipService.SetToolTip(_quality, L.T("画质"));
        _quality.SelectionChanged += Quality_SelectionChanged;
        _progress.ValueChanged += (s, e) => { if (!_updatingProgress) _player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue); };
        _volume.ValueChanged += (s, e) => { _player.Volume = e.NewValue / 100.0; if (_player.IsMuted && e.NewValue > 0) _player.IsMuted = false; UpdateVolumeIcon(); };

        var row = new Grid { ColumnSpacing = 8 };
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        void Put(UIElement el, int col) { Grid.SetColumn((FrameworkElement)el, col); row.Children.Add(el); }
        Put(play, 0); Put(_time, 1); Put(mute, 3); Put(_volume, 4); Put(_quality, 5); Put(full, 6);

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(_progress);
        panel.Children.Add(row);
        var bar = new Grid { Padding = new Thickness(20, 36, 20, 14), VerticalAlignment = VerticalAlignment.Bottom, Background = Fade(top: false) };
        bar.Children.Add(panel);
        return bar;
    }

    private static LinearGradientBrush Fade(bool top) => new()
    {
        StartPoint = new Windows.Foundation.Point(0, top ? 0 : 1),
        EndPoint = new Windows.Foundation.Point(0, top ? 1 : 0),
        GradientStops =
        {
            new GradientStop { Color = Windows.UI.Color.FromArgb(0xB4, 0, 0, 0), Offset = 0 },
            new GradientStop { Color = Windows.UI.Color.FromArgb(0, 0, 0, 0), Offset = 1 }
        }
    };

    private static Button IconButton(string glyph, string tip) => IconButton(new FontIcon { Glyph = glyph, FontSize = 15 }, tip);

    private static Button IconButton(FontIcon icon, string tip)
    {
        var b = new Button
        {
            Content = icon,
            Width = 40, Height = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent), Foreground = White(0xFF), BorderThickness = new Thickness(0)
        };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    private bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    /// <summary>
    /// 全屏状态变化时：更新全屏按钮的图标；窗口模式下顶部那一条是窗口标题栏（拖动区域，点击会被系统拿去拖动窗口），
    /// 返回按钮和标题要放到它下面。
    /// </summary>
    public void SetFullscreenState(bool fullscreen, double titleBarHeight)
    {
        _topBar.Margin = new Thickness(0, fullscreen ? 0 : titleBarHeight, 0, 0);
        _fullIcon.Glyph = fullscreen ? "" : "";
        string tip = fullscreen ? L.T("退出全屏") : L.T("全屏");
        ToolTipService.SetToolTip(_fullButton, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_fullButton, tip);
    }

    /// <summary>打开播放层并开始加载这首歌的 MV。</summary>
    public void Open(TrackItem track, ICloudSource source)
    {
        _track = track;
        _source = source;
        _title.Text = string.IsNullOrWhiteSpace(track.Artist) ? track.Title : $"{track.Title} - {track.Artist}";
        _time.Text = "0:00 / 0:00";
        _updatingProgress = true; _progress.Value = 0; _progress.Maximum = 1; _updatingProgress = false;
        _quality.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Visible;
        UiAnimations.FadeIn(this, 220);
        _volume.Value = Math.Clamp(AudioEngineService.Instance.Volume, 0f, 1f) * 100;   // 从 Klang 当前的音量开始
        _player.IsMuted = false;
        UpdateVolumeIcon();
        ShowBars(true);
        _tick.Start();
        // 键盘焦点移进播放层：Esc / 空格才能落到这里（焦点留在被盖住的控件上时可能收不到按键）
        DispatcherQueue.TryEnqueue(() => _backButton.Focus(FocusState.Programmatic));
        _ = LoadAsync(StateService.Instance.State.MvQuality, 0);
    }

    public void Close()
    {
        _cts?.Cancel();
        _tick.Stop();
        _idle.Stop();
        _player.Pause();
        _player.Source = null;
        Visibility = Visibility.Collapsed;
        _track = null;
    }

    public void TogglePlayPause()
    {
        if (_player.Source == null) return;
        if (IsPlaying) _player.Pause(); else _player.Play();
        WakeBars();
    }

    private void UpdatePlayState()
    {
        bool playing = IsPlaying;
        _playIcon.Glyph = playing ? "" : "";
        if (!playing) ShowBars(true);   // 暂停时控制条一直显示
        else WakeBars();
    }

    private void UpdateProgress()
    {
        var s = _player.PlaybackSession;
        double dur = s.NaturalDuration.TotalSeconds, pos = s.Position.TotalSeconds;
        if (dur <= 0) return;
        _updatingProgress = true;
        _progress.Maximum = dur;
        _progress.Value = Math.Clamp(pos, 0, dur);
        _updatingProgress = false;
        _time.Text = $"{Fmt(pos)} / {Fmt(dur)}";
    }

    private static string Fmt(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void UpdateVolumeIcon() =>
        _volumeIcon.Glyph = _player.IsMuted || _volume.Value <= 0 ? "" : _volume.Value < 50 ? "" : "";

    private void WakeBars()
    {
        ShowBars(true);
        _idle.Stop();
        _idle.Start();
    }

    private void ShowBars(bool show)
    {
        if (_barsShown == show) return;
        _barsShown = show;
        foreach (var bar in new[] { _topBar, _bottomBar })
        {
            if (show) { bar.IsHitTestVisible = true; UiAnimations.OpacityTo(bar, 1f, 160); }
            else { bar.IsHitTestVisible = false; UiAnimations.OpacityTo(bar, 0f, 300); }
        }
    }

    private async Task LoadAsync(int height, double resumeAt)
    {
        if (_track == null || _source == null) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _message.Visibility = Visibility.Collapsed;
        _ring.IsActive = true;
        MvStream? mv;
        try { mv = await _source.GetMvAsync(_track, height, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { App.LogError("MV", ex.Message); mv = null; }
        if (cts.IsCancellationRequested || !IsOpen) return;
        _ring.IsActive = false;
        if (mv == null) { ShowMessage(L.T("没有找到这首歌的 MV，或暂时无法获取（可能需要登录）。")); return; }

        if (!string.IsNullOrWhiteSpace(mv.Title)) _title.Text = string.IsNullOrWhiteSpace(_track.Artist) ? mv.Title : $"{mv.Title} - {_track.Artist}";
        _updatingQuality = true;
        _quality.Items.Clear();
        foreach (var h in mv.Heights) _quality.Items.Add(new ComboBoxItem { Content = $"{h}P", Tag = h });
        _quality.SelectedItem = _quality.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == mv.Height);
        _quality.Visibility = mv.Heights.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _updatingQuality = false;

        if (resumeAt > 0.5)
        {
            // 换画质：接着刚才的位置放
            void Opened(MediaPlayer p, object a)
            {
                p.MediaOpened -= Opened;
                p.PlaybackSession.Position = TimeSpan.FromSeconds(resumeAt);
            }
            _player.MediaOpened += Opened;
        }
        _player.Source = MediaSource.CreateFromUri(new Uri(mv.Url));
        _player.Play();
    }

    private void Quality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingQuality || _quality.SelectedItem is not ComboBoxItem { Tag: int h }) return;
        StateService.Instance.State.MvQuality = h;
        StateService.Instance.RequestSave();
        _ = LoadAsync(h, _player.PlaybackSession.Position.TotalSeconds);
    }

    private void ShowMessage(string text)
    {
        _ring.IsActive = false;
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
        ShowBars(true);
    }
}
