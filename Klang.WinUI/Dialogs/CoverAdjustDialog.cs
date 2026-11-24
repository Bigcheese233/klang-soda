using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Klang_WinUI.Models;
using Klang_WinUI.Services;
using Windows.Foundation;

namespace Klang_WinUI.Dialogs;

/// <summary>
/// “调整封面”（#18）：在原图上拖动 / 缩放一个正方形取景框，选定要显示的部分；也可以换成另一张图片。
/// 保存的是裁好的正方形图片（数据目录\CustomCovers），可以只用于这首歌，也可以用于整张专辑；音乐文件本身不改。
/// </summary>
public static class CoverAdjustDialog
{
    public enum Outcome { Cancel, Saved, Reset }

    private const double Stage = 400;          // 预览区边长（DIP）
    private const int MaxOutput = 1200;        // 保存的图片最大边长（像素）

    /// <param name="album">同一张专辑的曲目（含这首）；只有一首时不显示“应用到整张专辑”。</param>
    /// <returns>结果与受影响的曲目。</returns>
    public static async Task<(Outcome Outcome, IReadOnlyList<TrackItem> Affected)> ShowAsync(
        XamlRoot root, IntPtr ownerHwnd, TrackItem track, IReadOnlyList<TrackItem> album)
    {
        Brush secondary = (root.Content is FrameworkElement fe ? ThemeService.Brush(fe, "TextFillColorSecondaryBrush") : null)
            ?? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        // ---- 状态（源图像素坐标）----
        CanvasBitmap? bitmap = null;
        double imgW = 0, imgH = 0;      // 源图尺寸
        double side = 0, cx = 0, cy = 0; // 取景框边长与中心
        double scale = 1, ox = 0, oy = 0; // 源图 → 预览区的缩放与偏移
        bool dragging = false;
        Point grab = default;            // 按下时指针相对取景框中心的偏移（源图像素）

        // ---- 界面 ----
        var intro = new TextBlock
        {
            Text = L.F("为《{0}》选择封面要显示的部分。拖动方框调整位置，滚轮或下方滑块调整大小。", track.Title),
            TextWrapping = TextWrapping.Wrap, Foreground = secondary, FontSize = 12
        };

        var image = new Image { Stretch = Stretch.Fill };
        var mask = new Microsoft.UI.Xaml.Shapes.Path
        {
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 0, 0, 0)),
            IsHitTestVisible = false
        };
        var outer = new RectangleGeometry { Rect = new Rect(0, 0, Stage, Stage) };
        var hole = new RectangleGeometry();
        mask.Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { outer, hole } };
        var frame = new Border
        {
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false
        };
        var empty = new TextBlock
        {
            Text = L.T("这首歌没有封面，请点“更换图片…”选择一张。"),
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(40), Foreground = secondary
        };
        var canvas = new Canvas { Width = Stage, Height = Stage, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        canvas.Children.Add(image);
        canvas.Children.Add(mask);
        canvas.Children.Add(frame);
        var stage = new Grid
        {
            Width = Stage, Height = Stage, CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stage.Children.Add(empty);
        stage.Children.Add(canvas);

        var sizeSlider = new Slider { Minimum = 15, Maximum = 100, Value = 100, StepFrequency = 1, Header = L.T("取景框大小") };
        var replaceBtn = new Button { Content = L.T("更换图片…") };
        var centerBtn = new Button { Content = L.T("居中") };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        btnRow.Children.Add(replaceBtn);
        btnRow.Children.Add(centerBtn);

        var albumBox = new CheckBox
        {
            Content = L.F("应用到整张专辑《{0}》（{1} 首）", track.Album, album.Count),
            Visibility = album.Count > 1 ? Visibility.Visible : Visibility.Collapsed
        };

        var content = new StackPanel { Spacing = 12, Width = Stage + 40 };
        content.Children.Add(intro);
        content.Children.Add(stage);
        content.Children.Add(sizeSlider);
        content.Children.Add(btnRow);
        content.Children.Add(albumBox);

        bool hasCustom = CustomCoverService.Has(track.Id) || album.Any(t => CustomCoverService.Has(t.Id));
        var dlg = new ContentDialog
        {
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            RequestedTheme = ThemeService.Current,
            XamlRoot = root,
            Title = L.T("调整封面"),
            Content = content,
            PrimaryButtonText = L.T("保存"),
            SecondaryButtonText = L.T("恢复默认"),
            CloseButtonText = L.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = hasCustom
        };

        // ---- 取景框 ----
        void Clamp()
        {
            double min = Math.Min(imgW, imgH);
            side = Math.Clamp(side, Math.Max(16, min * sizeSlider.Minimum / 100), min);
            cx = Math.Clamp(cx, side / 2, imgW - side / 2);
            cy = Math.Clamp(cy, side / 2, imgH - side / 2);
        }

        void Redraw()
        {
            if (bitmap == null) return;
            Clamp();
            double l = ox + (cx - side / 2) * scale, t = oy + (cy - side / 2) * scale, s = side * scale;
            Canvas.SetLeft(frame, l); Canvas.SetTop(frame, t);
            frame.Width = s; frame.Height = s;
            hole.Rect = new Rect(l, t, s, s);
        }

        bool updatingSlider = false;
        void SyncSlider()
        {
            updatingSlider = true;
            sizeSlider.Value = Math.Round(side / Math.Min(imgW, imgH) * 100);
            updatingSlider = false;
        }

        async Task<bool> LoadAsync(string path)
        {
            try
            {
                var bmp = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), path);
                bitmap?.Dispose();
                bitmap = bmp;
                imgW = bmp.SizeInPixels.Width; imgH = bmp.SizeInPixels.Height;
                scale = Math.Min(Stage / imgW, Stage / imgH);
                ox = (Stage - imgW * scale) / 2; oy = (Stage - imgH * scale) / 2;

                var source = new BitmapImage { DecodePixelWidth = (int)Math.Ceiling(imgW * scale * 2) };
                source.UriSource = new Uri(path);
                image.Source = source;
                image.Width = imgW * scale; image.Height = imgH * scale;
                Canvas.SetLeft(image, ox); Canvas.SetTop(image, oy);

                // 默认：居中、尽量大的正方形
                side = Math.Min(imgW, imgH); cx = imgW / 2; cy = imgH / 2;
                SyncSlider();
                Redraw();
                empty.Visibility = Visibility.Collapsed;
                canvas.Visibility = Visibility.Visible;
                dlg.IsPrimaryButtonEnabled = true;
                return true;
            }
            catch (Exception ex)
            {
                App.LogError("Cover adjust load", ex.Message);
                empty.Text = L.T("无法读取这张图片，请换一张。");
                return false;
            }
        }

        canvas.Visibility = Visibility.Collapsed;
        // 从原始封面开始调（而不是已经裁过的那张），这样可以重新选范围
        string? start = track.OriginalCoverPath;
        if (!string.IsNullOrEmpty(start) && File.Exists(start)) await LoadAsync(start);

        sizeSlider.ValueChanged += (s, e) =>
        {
            if (updatingSlider || bitmap == null) return;
            side = Math.Min(imgW, imgH) * e.NewValue / 100;
            Redraw();
        };
        centerBtn.Click += (s, e) => { if (bitmap == null) return; cx = imgW / 2; cy = imgH / 2; Redraw(); };

        Point ToSource(Point p) => new((p.X - ox) / scale, (p.Y - oy) / scale);

        canvas.PointerPressed += (s, e) =>
        {
            if (bitmap == null) return;
            var p = ToSource(e.GetCurrentPoint(canvas).Position);
            // 按在框外：先把框移到这里
            if (Math.Abs(p.X - cx) > side / 2 || Math.Abs(p.Y - cy) > side / 2) { cx = p.X; cy = p.Y; Redraw(); }
            grab = new Point(p.X - cx, p.Y - cy);
            dragging = canvas.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        canvas.PointerMoved += (s, e) =>
        {
            if (!dragging) return;
            var p = ToSource(e.GetCurrentPoint(canvas).Position);
            cx = p.X - grab.X; cy = p.Y - grab.Y;
            Redraw();
            e.Handled = true;
        };
        void EndDrag(object s, PointerRoutedEventArgs e) { dragging = false; canvas.ReleasePointerCaptures(); }
        canvas.PointerReleased += EndDrag;
        canvas.PointerCaptureLost += (s, e) => dragging = false;
        canvas.PointerWheelChanged += (s, e) =>
        {
            if (bitmap == null) return;
            int delta = e.GetCurrentPoint(canvas).Properties.MouseWheelDelta;
            side *= delta > 0 ? 0.92 : 1 / 0.92;
            Clamp(); SyncSlider(); Redraw();
            e.Handled = true;
        };
        canvas.PointerEntered += (s, e) => canvas.ProtectedCursorSetter(InputSystemCursorShape.SizeAll);

        replaceBtn.Click += async (s, e) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);
                foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif", ".tif", ".tiff" }) picker.FileTypeFilter.Add(ext);
                var file = await picker.PickSingleFileAsync();
                if (file != null) await LoadAsync(file.Path);
            }
            catch (Exception ex) { App.LogError("Cover adjust pick", ex.Message); }
        };

        var result = await dlg.ShowAsync();
        var targets = albumBox.IsChecked == true && album.Count > 1 ? album : new[] { track };
        try
        {
            if (result == ContentDialogResult.Primary && bitmap != null)
            {
                Clamp();
                var src = new Rect(cx - side / 2, cy - side / 2, side, side);
                int outSide = (int)Math.Round(Math.Min(side, MaxOutput));
                var path = CustomCoverService.NewFilePath();
                using (var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), outSide, outSide, 96))
                {
                    using (var ds = target.CreateDrawingSession())
                    {
                        ds.Clear(Microsoft.UI.Colors.Black);
                        ds.DrawImage(bitmap, new Rect(0, 0, outSide, outSide), src, 1f, CanvasImageInterpolation.HighQualityCubic);
                    }
                    await target.SaveAsync(path, CanvasBitmapFileFormat.Jpeg, 0.92f);
                }
                CustomCoverService.Assign(targets.Select(t => t.Id), path);
                CrashReporter.Note($"调整封面：{track.Title}（{targets.Count} 首）");
                return (Outcome.Saved, targets);
            }
            if (result == ContentDialogResult.Secondary)
            {
                // 勾了整张专辑就整张恢复，否则只恢复这首
                CustomCoverService.Clear(targets.Select(t => t.Id));
                return (Outcome.Reset, targets);
            }
        }
        catch (Exception ex)
        {
            App.LogError("Cover adjust save", ex);
        }
        finally { bitmap?.Dispose(); }
        return (Outcome.Cancel, Array.Empty<TrackItem>());
    }

    /// <summary>同一张专辑的曲目：专辑名相同，且歌手相同或在同一个文件夹里。</summary>
    public static List<TrackItem> AlbumOf(TrackItem track, IEnumerable<TrackItem> library)
    {
        if (string.IsNullOrWhiteSpace(track.Album) || track.Album is "Unknown Album" or "未知专辑") return new() { track };
        string? dir = SafeDir(track.FilePath);
        var list = library.Where(t => t == track || (t.Album == track.Album
            && (t.Artist == track.Artist || (dir != null && string.Equals(SafeDir(t.FilePath), dir, StringComparison.OrdinalIgnoreCase)))))
            .ToList();
        if (!list.Contains(track)) list.Insert(0, track);
        return list;
    }

    private static string? SafeDir(string path)
    {
        try { return string.IsNullOrEmpty(path) ? null : System.IO.Path.GetDirectoryName(path); } catch { return null; }
    }
}

internal static class CursorExtensions
{
    /// <summary>ProtectedCursor 只能在控件内部设置；这里借反射给预览区换成“移动”指针（失败就算了）。</summary>
    public static void ProtectedCursorSetter(this UIElement element, InputSystemCursorShape shape)
    {
        try
        {
            typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(element, InputSystemCursor.Create(shape));
        }
        catch { }
    }
}
