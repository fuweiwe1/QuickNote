using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using QuickNote.Core;

namespace QuickNote;

public partial class NoteCard : UserControl
{
    public Note Note { get; }
    internal int RestingZIndex { get; set; }
    private readonly double restingAngle;
    private Point dragStart;
    private bool dragArmed;
    private bool dragging;
    public event Action<NoteCard>? CopyRequested;
    public event Action<NoteCard>? DeleteRequested;
    public event Action<NoteCard>? OpenRequested;
    public event Action<NoteCard, Point>? DragStarted;
    public event Action<NoteCard>? DragMoved;
    public event Action<NoteCard, bool>? DragFinished;
    public event Action<bool>? InteractionChanged;

    public NoteCard(Note note, NotebookStore store)
    {
        InitializeComponent();
        Note = note;
        restingAngle = ((note.Id.ToByteArray()[0] % 7) - 3) * 0.7;
        CardRotation.Angle = restingAngle;
        var colors = new[] { "#FFF2BE", "#E2EACF", "#F4DCD0", "#DCE9EB", "#E7DEF0" };
        var color = (Color)ColorConverter.ConvertFromString(colors[note.Color]);
        Paper.Background = new LinearGradientBrush(Colors.White, color, 90) { StartPoint = new(0, -3), EndPoint = new(0, 1) };
        DateLabel.Text = note.CreatedAt.ToLocalTime().ToString("MM.dd");
        TypeLabel.Text = note.Kind == NoteKind.Text ? "一 段 文 字" : "一 张 图 片";
        TextPreview.Text = note.Text[..Math.Min(1200, note.Text.Length)];
        if (note.Kind == NoteKind.Image && note.ImageFile is not null)
        {
            TextPreview.Visibility = Visibility.Collapsed;
            ImageFrame.Visibility = Visibility.Visible;
            try { ImagePreview.Source = LoadImage(store.ImagePath(note.ImageFile), 500); }
            catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or ArgumentException)
            {
                ImageFrame.Visibility = Visibility.Collapsed;
                TextPreview.Visibility = Visibility.Visible;
                TextPreview.Text = "图片暂时无法读取\n\n原文件可能被移动，请检查数据目录。";
            }
        }
        AutomationProperties.SetName(this, note.Kind == NoteKind.Text ? note.Text[..Math.Min(80, note.Text.Length)] : note.Label);
        ToolTip = "双击查看 · 按住拖动，松手夹在绳子上";
        Focusable = true;
    }

    internal static BitmapImage LoadImage(string path, int decodeWidth = 0)
    {
        var bitmap = new BitmapImage();
        using var stream = File.OpenRead(path);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public void AnimateIn()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        CardOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-40, 0, TimeSpan.FromMilliseconds(460)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 } });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)));
        CardRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-9, restingAngle, TimeSpan.FromMilliseconds(500)) { EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 } });
    }

    private void OnHover(object sender, MouseEventArgs e)
    {
        Panel.SetZIndex(this, int.MaxValue);
        AnimateHover(1.16, 0, 0);
    }
    private void OnLeave(object sender, MouseEventArgs e)
    {
        Panel.SetZIndex(this, RestingZIndex);
        AnimateHover(1, restingAngle, 0);
    }
    private void AnimateHover(double scale, double angle, double y)
    {
        var time = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 180 : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, time) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, time) { EasingFunction = ease });
        CardRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, time) { EasingFunction = ease });
        CardOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, time) { EasingFunction = ease });
    }
    private void OnCopy(object sender, RoutedEventArgs e) { e.Handled = true; CopyRequested?.Invoke(this); }
    internal async Task ShowCopied()
    {
        CopyButton.Content = "已复制";
        await Task.Delay(1100);
        CopyButton.Content = "复制";
    }
    private void OnDelete(object sender, RoutedEventArgs e) { e.Handled = true; DeleteRequested?.Invoke(this); }
    private void OnOpen(object sender, RoutedEventArgs e) { e.Handled = true; OpenRequested?.Invoke(this); }
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindButton(source)) return;
        if (e.ClickCount == 2) { dragArmed = false; OpenRequested?.Invoke(this); e.Handled = true; return; }
        dragStart = e.GetPosition(this);
        dragArmed = true;
        CaptureMouse();
    }
    private static bool FindButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!dragArmed) return;
        if (e.LeftButton != MouseButtonState.Pressed) { FinishDrag(commit: dragging); return; }
        var point = e.GetPosition(this);
        if (!dragging)
        {
            if (Math.Abs(point.X - dragStart.X) + Math.Abs(point.Y - dragStart.Y) < 5) return;
            dragging = true;
            InteractionChanged?.Invoke(true);
            DragStarted?.Invoke(this, dragStart);
        }
        DragMoved?.Invoke(this);
    }
    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (dragging) e.Handled = true;
        FinishDrag(commit: true);
    }
    private void OnLostCapture(object sender, MouseEventArgs e) => FinishDrag(commit: false);
    private void FinishDrag(bool commit)
    {
        var wasDragging = dragging;
        dragging = false; dragArmed = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (!wasDragging) return;
        try { DragFinished?.Invoke(this, commit); }
        finally { InteractionChanged?.Invoke(false); }
    }

    internal void PrepareDrag()
    {
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CardScale.ScaleX = CardScale.ScaleY = 1;
        CardRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        CardRotation.Angle = 0;
    }
}
