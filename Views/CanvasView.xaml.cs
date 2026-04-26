using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ColorKids.Models;
using ColorKids.Services;
using ColorKids.ViewModels;

namespace ColorKids.Views;

public partial class CanvasView : UserControl
{
    private MainViewModel? _vm;
    private readonly SoundService _sound = new();

    private bool _isDrawing;
    private Point _lastBitmapPoint;

    // Cached coordinate transform factors — recomputed only on resize
    private double _scaleX, _scaleY, _offsetX, _offsetY;
    private bool _transformDirty = true;

    public CanvasView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _transformDirty = true;
        UpdateCursorSize();

        if (_vm is null) return;
        int newW = (int)e.NewSize.Width;
        int newH = (int)e.NewSize.Height;
        if (newW <= 0 || newH <= 0) return;

        _vm.ResizeLayers(newW, newH);
        DrawingImage.Source = _vm.CompositeOutput;
        // Bitmap is now properly sized — recompute cursor
        UpdateCursorSize();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old)
            old.PropertyChanged -= OnVmPropertyChanged;

        if (e.NewValue is MainViewModel vm)
        {
            _vm = vm;
            DrawingImage.Source = vm.CompositeOutput;
            CompositeRenderService.RenderFull(
                vm.BackgroundLayer, vm.DrawingLayer, vm.OutlineLayer, vm.CompositeOutput);
            _transformDirty = true;
            vm.PropertyChanged += OnVmPropertyChanged;
            UpdateCursorSize();
            UpdateCursorColor();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.BrushSize):
                UpdateCursorSize();
                break;
            case nameof(MainViewModel.ActiveColor):
                UpdateCursorColor();
                break;
            case nameof(MainViewModel.ActiveTool):
                UpdateCursorSize();
                UpdateCursorColor();
                break;
            case nameof(MainViewModel.ZoomLevel):
                ApplyZoom(_vm!.ZoomLevel);
                break;
        }
    }

    private void ApplyZoom(double level)
    {
        // Origin (0,0) : le zoom s'étend depuis le coin supérieur gauche
        ZoomTransform.CenterX = 0;
        ZoomTransform.CenterY = 0;
        ZoomTransform.ScaleX  = level;
        ZoomTransform.ScaleY  = level;

        // Agrandir le container pour que le ScrollViewer puisse scroller
        // sur toute la surface zoomée
        double vpW = CanvasScroller.ViewportWidth;
        double vpH = CanvasScroller.ViewportHeight;
        ZoomContainer.Width  = Math.Max(vpW, vpW * level);
        ZoomContainer.Height = Math.Max(vpH, vpH * level);
    }

    /// <summary>Called by ToolbarView when the undo button is pressed.</summary>
    internal void PerformUndo()
    {
        _vm?.PopHistory();
    }

    /// <summary>Called by ToolbarView when the clear button is pressed.</summary>
    internal void PerformClear()
    {
        if (_vm is null) return;
        _vm.PushHistory();
        _vm.ClearDrawingLayer();
        _sound.PlayClear();
    }

    // ──────────────────────────────────────────────────────────────
    //  Mouse handling
    // ──────────────────────────────────────────────────────────────

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;

        Mouse.Capture(DrawingImage);
        _isDrawing = true;

        var pt = ToBitmapPoint(e.GetPosition(DrawingImage));

        if (_vm.ActiveTool == DrawingTool.Fill)
        {
            _vm.PushHistory();
            BitmapDrawingService.FloodFillMasked(
                _vm.DrawingLayer, _vm.OutlineLayer, pt, _vm.ActiveColor);
            CompositeRenderService.RenderFull(
                _vm.BackgroundLayer, _vm.DrawingLayer, _vm.OutlineLayer, _vm.CompositeOutput);
            _sound.PlayFill();
            _isDrawing = false;
            Mouse.Capture(null);
        }
        else if (_vm.ActiveTool is DrawingTool.Brush or DrawingTool.Eraser)
        {
            _vm.PushHistory();
            _lastBitmapPoint = pt;
            DrawAt(pt, pt);
            _sound.PlayBrush();
            AnimateCursorPress(true);
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Cursor overlay must follow the mouse in CursorCanvas coordinates
        MoveCursorEllipse(e.GetPosition(CursorCanvas));

        if (!_isDrawing || _vm is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        if (_vm.ActiveTool is DrawingTool.Brush or DrawingTool.Eraser)
        {
            var pt = ToBitmapPoint(e.GetPosition(DrawingImage));
            DrawAt(_lastBitmapPoint, pt);
            _lastBitmapPoint = pt;
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDrawing = false;
        Mouse.Capture(null);
        AnimateCursorPress(false);
    }

    // ──────────────────────────────────────────────────────────────
    //  Drawing dispatch
    // ──────────────────────────────────────────────────────────────

    private void DrawAt(Point from, Point to)
    {
        if (_vm is null) return;

        Color color   = _vm.ActiveTool == DrawingTool.Eraser ? Colors.White : _vm.ActiveColor;
        double radius = _vm.ActiveTool == DrawingTool.Eraser ? _vm.BrushSize * 1.5 / 2.0
                                                             : _vm.BrushSize / 2.0;
        int r  = (int)radius;
        int bw = _vm.DrawingLayer.PixelWidth;
        int bh = _vm.DrawingLayer.PixelHeight;

        int dx1 = Math.Max(0, (int)Math.Min(from.X, to.X) - r);
        int dy1 = Math.Max(0, (int)Math.Min(from.Y, to.Y) - r);
        int dx2 = Math.Min(bw - 1, (int)Math.Max(from.X, to.X) + r);
        int dy2 = Math.Min(bh - 1, (int)Math.Max(from.Y, to.Y) + r);

        BitmapDrawingService.DrawStrokeMasked(
            _vm.DrawingLayer, _vm.OutlineLayer, from, to, color, radius);

        var dirty = new Int32Rect(dx1, dy1, Math.Max(1, dx2 - dx1 + 1), Math.Max(1, dy2 - dy1 + 1));
        CompositeRenderService.RenderRegion(
            _vm.BackgroundLayer, _vm.DrawingLayer, _vm.OutlineLayer, _vm.CompositeOutput, dirty);
    }

    // ──────────────────────────────────────────────────────────────
    //  Coordinate transform: Image element → bitmap pixels
    // ──────────────────────────────────────────────────────────────

    private void EnsureTransform()
    {
        if (!_transformDirty || _vm is null) return;
        if (DrawingImage.ActualWidth <= 0 || DrawingImage.ActualHeight <= 0) return;
        _transformDirty = false;

        // Stretch="Fill" — bitmap covers the full element, no letterbox offset needed.
        var bmp = _vm.CompositeOutput;
        _offsetX = 0;
        _offsetY = 0;
        _scaleX  = bmp.PixelWidth  / DrawingImage.ActualWidth;
        _scaleY  = bmp.PixelHeight / DrawingImage.ActualHeight;
    }

    private Point ToBitmapPoint(Point elementPt)
    {
        if (_vm is null) return elementPt;
        EnsureTransform();
        var bmp = _vm.CompositeOutput;
        return new Point(
            Math.Clamp((elementPt.X - _offsetX) * _scaleX, 0, bmp.PixelWidth  - 1),
            Math.Clamp((elementPt.Y - _offsetY) * _scaleY, 0, bmp.PixelHeight - 1));
    }

    // ──────────────────────────────────────────────────────────────
    //  Cursor overlay
    // ──────────────────────────────────────────────────────────────

    private void OnMouseEnter(object sender, MouseEventArgs e)
        => CursorEllipse.Opacity = 0.85;

    private void OnMouseLeave(object sender, MouseEventArgs e)
        => CursorEllipse.Opacity = 0;

    private void MoveCursorEllipse(Point pos)
    {
        Canvas.SetLeft(CursorEllipse, pos.X - CursorEllipse.Width  / 2.0);
        Canvas.SetTop(CursorEllipse,  pos.Y - CursorEllipse.Height / 2.0);
    }

    private void UpdateCursorSize()
    {
        if (_vm is null || ActualWidth == 0) return;

        var bmp = _vm.CompositeOutput;
        // Guard: bitmap not yet properly sized (stays at 1×1 until first layout)
        if (bmp.PixelWidth <= 1 || bmp.PixelHeight <= 1) return;

        // Scale = element width / bitmap width, adjusted for zoom so the cursor matches the visual size
        double scale = (DrawingImage.ActualWidth / bmp.PixelWidth) * (_vm?.ZoomLevel ?? 1.0);

        double radiusBitmap = _vm.ActiveTool == DrawingTool.Eraser
            ? _vm.BrushSize * 1.5 / 2.0
            : _vm.BrushSize / 2.0;
        double diameter = Math.Max(6, radiusBitmap * 2.0 * scale);

        CursorEllipse.Width  = diameter;
        CursorEllipse.Height = diameter;
    }

    private void UpdateCursorColor()
    {
        if (_vm is null) return;
        if (_vm.ActiveTool == DrawingTool.Eraser)
        {
            CursorEllipse.Fill   = new SolidColorBrush(Colors.White);
            CursorEllipse.Stroke = new SolidColorBrush(Colors.Gray);
        }
        else
        {
            CursorEllipse.Fill   = new SolidColorBrush(_vm.ActiveColor) { Opacity = 0.5 };
            CursorEllipse.Stroke = new SolidColorBrush(Colors.White);
        }
    }

    private void AnimateCursorPress(bool pressed)
    {
        double target = pressed ? 0.75 : 1.0;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(100));
        CursorScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        CursorScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }
}
