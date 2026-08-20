using System.Windows.Input;
using System.Windows.Media;

namespace ChunkPilot.App;

/// <summary>A square crop surface with normalized coordinates independent of display DPI.</summary>
public sealed class ServerIconCropControl : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(ServerIconCropControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CropXProperty = DependencyProperty.Register(
        nameof(CropX), typeof(double), typeof(ServerIconCropControl),
        new FrameworkPropertyMetadata(0.5d, FrameworkPropertyMetadataOptions.AffectsRender, OnCropChanged));
    public static readonly DependencyProperty CropYProperty = DependencyProperty.Register(
        nameof(CropY), typeof(double), typeof(ServerIconCropControl),
        new FrameworkPropertyMetadata(0.5d, FrameworkPropertyMetadataOptions.AffectsRender, OnCropChanged));
    public static readonly DependencyProperty CropSizeProperty = DependencyProperty.Register(
        nameof(CropSize), typeof(double), typeof(ServerIconCropControl),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender, OnCropChanged,
            (_, value) => Math.Clamp((double)value, 0.08, 1)));
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(ServerIconCropControl),
        new FrameworkPropertyMetadata(1d, OnZoomChanged, (_, value) => Math.Clamp((double)value, 1, 12)));

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double CropX { get => (double)GetValue(CropXProperty); set => SetValue(CropXProperty, Math.Clamp(value, 0, 1)); }
    public double CropY { get => (double)GetValue(CropYProperty); set => SetValue(CropYProperty, Math.Clamp(value, 0, 1)); }
    public double CropSize { get => (double)GetValue(CropSizeProperty); set => SetValue(CropSizeProperty, value); }
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    public event EventHandler? CropChanged;

    private Point? dragStart;
    private double startX;
    private double startY;

    public ServerIconCropControl()
    {
        Cursor = System.Windows.Input.Cursors.SizeAll;
        ClipToBounds = true;
        Focusable = true;
        MouseLeftButtonDown += BeginDrag;
        MouseMove += Drag;
        MouseLeftButtonUp += EndDrag;
        MouseWheel += (_, e) =>
        {
            Zoom += e.Delta > 0 ? 0.25 : -0.25;
            e.Handled = true;
        };
        KeyDown += OnKeyDown;
    }

    public void Fit()
    {
        CropX = 0.5;
        CropY = 0.5;
        Zoom = 1;
    }

    private static void OnZoomChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (ServerIconCropControl)sender;
        control.CropSize = 1d / (double)args.NewValue;
    }

    private static void OnCropChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ServerIconCropControl)sender).CropChanged?.Invoke(sender, EventArgs.Empty);

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        const double step = 0.02;
        switch (e.Key)
        {
            case Key.Left: CropX -= step; break;
            case Key.Right: CropX += step; break;
            case Key.Up: CropY -= step; break;
            case Key.Down: CropY += step; break;
            case Key.Add:
            case Key.OemPlus: Zoom += 0.25; break;
            case Key.Subtract:
            case Key.OemMinus: Zoom -= 0.25; break;
            case Key.Home: Fit(); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(ResourceBrush("AppSurfaceSunken"), null,
            new Rect(0, 0, ActualWidth, ActualHeight));
        if (Source is null || Source.Width <= 0 || Source.Height <= 0)
            return;

        var scale = Math.Min(ActualWidth / Source.Width, ActualHeight / Source.Height);
        var imageRect = new Rect(
            (ActualWidth - Source.Width * scale) / 2,
            (ActualHeight - Source.Height * scale) / 2,
            Source.Width * scale,
            Source.Height * scale);
        drawingContext.DrawImage(Source, imageRect);

        var side = Math.Min(imageRect.Width, imageRect.Height) * CropSize;
        var crop = new Rect(
            imageRect.Left + CropX * Math.Max(0, imageRect.Width - side),
            imageRect.Top + CropY * Math.Max(0, imageRect.Height - side), side, side);
        var shade = ResourceBrush("AppSurfaceScrim");
        drawingContext.DrawRectangle(shade, null, new Rect(imageRect.Left, imageRect.Top, imageRect.Width, crop.Top - imageRect.Top));
        drawingContext.DrawRectangle(shade, null, new Rect(imageRect.Left, crop.Bottom, imageRect.Width, imageRect.Bottom - crop.Bottom));
        drawingContext.DrawRectangle(shade, null, new Rect(imageRect.Left, crop.Top, crop.Left - imageRect.Left, crop.Height));
        drawingContext.DrawRectangle(shade, null, new Rect(crop.Right, crop.Top, imageRect.Right - crop.Right, crop.Height));
        drawingContext.DrawRectangle(null, new Pen(ResourceBrush("AppAccentMuted"), 2), crop);

        var thirds = side / 3;
        var guide = new Pen(ResourceBrush("AppTextMuted"), 1);
        drawingContext.DrawLine(guide, new Point(crop.Left + thirds, crop.Top), new Point(crop.Left + thirds, crop.Bottom));
        drawingContext.DrawLine(guide, new Point(crop.Left + thirds * 2, crop.Top), new Point(crop.Left + thirds * 2, crop.Bottom));
        drawingContext.DrawLine(guide, new Point(crop.Left, crop.Top + thirds), new Point(crop.Right, crop.Top + thirds));
        drawingContext.DrawLine(guide, new Point(crop.Left, crop.Top + thirds * 2), new Point(crop.Right, crop.Top + thirds * 2));
    }

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(this);
        startX = CropX;
        startY = CropY;
        CaptureMouse();
        e.Handled = true;
    }

    private void Drag(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || Source is null)
            return;
        var scale = Math.Min(ActualWidth / Source.Width, ActualHeight / Source.Height);
        var side = Math.Min(Source.Width * scale, Source.Height * scale) * CropSize;
        var availableX = Math.Max(1, Source.Width * scale - side);
        var availableY = Math.Max(1, Source.Height * scale - side);
        var current = e.GetPosition(this);
        CropX = startX + (current.X - start.X) / availableX;
        CropY = startY + (current.Y - start.Y) / availableY;
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        dragStart = null;
        ReleaseMouseCapture();
    }

    private Brush ResourceBrush(string key) =>
        TryFindResource(key) as Brush ?? Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
}
