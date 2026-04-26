using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ColorKids.Models;
using ColorKids.Services;

namespace ColorKids.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int MaxUndoLevels = 10;

    // â”€â”€ Active state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private Color _activeColor = Colors.Red;
    public Color ActiveColor
    {
        get => _activeColor;
        set => SetProperty(ref _activeColor, value);
    }

    private DrawingTool _activeTool = DrawingTool.Brush;
    public DrawingTool ActiveTool
    {
        get => _activeTool;
        set => SetProperty(ref _activeTool, value);
    }

    private double _brushSize = 30.0;
    public double BrushSize
    {
        get => _brushSize;
        set => SetProperty(ref _brushSize, value);
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }

    private bool _hasOutline;
    public bool HasOutline
    {
        get => _hasOutline;
        set => SetProperty(ref _hasOutline, value);
    }

    // â”€â”€ Zoom â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private const double ZoomStep = 0.25;
    private const double ZoomMin  = 0.5;
    private const double ZoomMax  = 4.0;

    private double _zoomLevel = 1.0;
    public double ZoomLevel
    {
        get => _zoomLevel;
        set => SetProperty(ref _zoomLevel, Math.Clamp(value, ZoomMin, ZoomMax));
    }

    // â”€â”€ Layers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>White background â€” never modified.</summary>
    public WriteableBitmap BackgroundLayer { get; private set; }

    /// <summary>Child's drawing â€” only layer the child modifies.</summary>
    public WriteableBitmap DrawingLayer { get; private set; }

    /// <summary>Black outline on transparent â€” set by templates / import.</summary>
    public WriteableBitmap OutlineLayer { get; private set; }

    /// <summary>Composed result displayed on screen.</summary>
    public WriteableBitmap CompositeOutput { get; private set; }

    /// <summary>Current bitmap width in pixels.</summary>
    public int CanvasPixelWidth  => CompositeOutput.PixelWidth;

    /// <summary>Current bitmap height in pixels.</summary>
    public int CanvasPixelHeight => CompositeOutput.PixelHeight;

    // â”€â”€ Palette â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public static Color[] Palette { get; } =
    [
        Color.FromRgb(255,   0,   0),
        Color.FromRgb(255, 127,   0),
        Color.FromRgb(255, 255,   0),
        Color.FromRgb(127, 255,   0),
        Color.FromRgb(0,   200,   0),
        Color.FromRgb(0,   200, 200),
        Color.FromRgb(0,   100, 255),
        Color.FromRgb(100,   0, 255),
        Color.FromRgb(200,   0, 200),
        Color.FromRgb(255, 105, 180),
        Color.FromRgb(139,  69,  19),
        Color.FromRgb(0,     0,   0),
    ];

    // â”€â”€ Templates â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public static DrawingTemplate[] Templates => OutlineGeneratorService.Templates;

    // â”€â”€ Undo history (DrawingLayer snapshots as raw byte[]) â”€â”€â”€â”€â”€â”€

    private readonly LinkedList<byte[]> _history = new();

    public bool CanUndo => _history.Count > 0;

    /// <summary>Saves a snapshot of DrawingLayer before a stroke.</summary>
    public void PushHistory()
    {
        if (_history.Count >= MaxUndoLevels)
            _history.RemoveLast();

        int stride   = DrawingLayer.PixelWidth * 4;
        var snapshot = new byte[stride * DrawingLayer.PixelHeight];
        DrawingLayer.CopyPixels(snapshot, stride, 0);
        _history.AddFirst(snapshot);
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>Restores the last snapshot into DrawingLayer, then recomposites.</summary>
    public bool PopHistory()
    {
        if (_history.Count == 0) return false;
        var snapshot = _history.First!.Value;
        _history.RemoveFirst();

        int stride = DrawingLayer.PixelWidth * 4;
        DrawingLayer.WritePixels(
            new System.Windows.Int32Rect(0, 0, DrawingLayer.PixelWidth, DrawingLayer.PixelHeight),
            snapshot, stride, 0);

        CompositeRenderService.RenderFull(BackgroundLayer, DrawingLayer, OutlineLayer, CompositeOutput);
        OnPropertyChanged(nameof(CanUndo));
        return true;
    }

    // â”€â”€ Commands â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public IRelayCommand<Color>           SelectColorCommand    { get; }
    public IRelayCommand<DrawingTool>     SelectToolCommand     { get; }
    public IRelayCommand<DrawingTemplate> SelectTemplateCommand { get; }
    public IRelayCommand                  ZoomInCommand         { get; }
    public IRelayCommand                  ZoomOutCommand        { get; }
    public IRelayCommand                  ZoomResetCommand      { get; }

    public MainViewModel()
    {
        // Bitmaps initialized at minimal size â€” CanvasView will resize them on first layout.
        BackgroundLayer = CreateWhiteBitmap(1, 1);
        DrawingLayer    = CreateWhiteBitmap(1, 1);
        OutlineLayer    = CreateTransparentBitmap(1, 1);
        CompositeOutput = CreateWhiteBitmap(1, 1);

        SelectColorCommand    = new RelayCommand<Color>(color => ActiveColor = color);
        SelectToolCommand     = new RelayCommand<DrawingTool>(tool => ActiveTool = tool);
        SelectTemplateCommand = new RelayCommand<DrawingTemplate>(ApplyTemplate);
        ZoomInCommand         = new RelayCommand(() => ZoomLevel += ZoomStep);
        ZoomOutCommand        = new RelayCommand(() => ZoomLevel -= ZoomStep);
        ZoomResetCommand      = new RelayCommand(() => ZoomLevel  = 1.0);
    }

    // â”€â”€ Canvas resize (called by CanvasView on SizeChanged) â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Recreates all layers at the new physical pixel size and recomposites.
    /// Should only be called when the available size actually changes significantly.
    /// </summary>
    public void ResizeLayers(int newW, int newH)
    {
        if (newW <= 0 || newH <= 0) return;
        if (BackgroundLayer.PixelWidth == newW && BackgroundLayer.PixelHeight == newH) return;

        // Preserve existing DrawingLayer content scaled to new size
        var oldDrawing = DrawingLayer;
        var oldOutline = OutlineLayer;
        int oldW = oldDrawing.PixelWidth;
        int oldH = oldDrawing.PixelHeight;

        BackgroundLayer = CreateWhiteBitmap(newW, newH);
        DrawingLayer    = CreateWhiteBitmap(newW, newH);
        OutlineLayer    = CreateTransparentBitmap(newW, newH);
        CompositeOutput = CreateWhiteBitmap(newW, newH);

        // Carry over old drawing content if it had real pixels
        if (oldW > 1 && oldH > 1)
            ScaleLayerInto(oldDrawing, DrawingLayer);

        if (oldW > 1 && oldH > 1)
            ScaleLayerInto(oldOutline, OutlineLayer);

        _history.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanvasPixelWidth));
        OnPropertyChanged(nameof(CanvasPixelHeight));

        CompositeRenderService.RenderFull(BackgroundLayer, DrawingLayer, OutlineLayer, CompositeOutput);
    }

    // â”€â”€ Internal helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void ApplyTemplate(DrawingTemplate? template)
    {
        if (template is null) return;
        ClearDrawingLayer();
        OutlineGeneratorService.Generate(template, OutlineLayer);
        CompositeRenderService.RenderFull(BackgroundLayer, DrawingLayer, OutlineLayer, CompositeOutput);
        HasOutline = true;
        _history.Clear();
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>Clears only DrawingLayer (white) and recomposites.</summary>
    internal void ClearDrawingLayer()
    {
        BitmapDrawingService.Clear(DrawingLayer);
        CompositeRenderService.RenderFull(BackgroundLayer, DrawingLayer, OutlineLayer, CompositeOutput);
    }

    /// <summary>Scales <paramref name="src"/> into <paramref name="dst"/> using WPF TransformedBitmap.</summary>
    private static void ScaleLayerInto(WriteableBitmap src, WriteableBitmap dst)
    {
        try
        {
            var tb = new System.Windows.Media.Imaging.TransformedBitmap(
                src,
                new ScaleTransform(
                    (double)dst.PixelWidth  / src.PixelWidth,
                    (double)dst.PixelHeight / src.PixelHeight));
            int stride = dst.PixelWidth * 4;
            var px = new byte[stride * dst.PixelHeight];
            tb.CopyPixels(px, stride, 0);
            dst.WritePixels(new System.Windows.Int32Rect(0, 0, dst.PixelWidth, dst.PixelHeight), px, stride, 0);
        }
        catch { /* non-fatal â€” content is lost but app keeps running */ }
    }

    private static WriteableBitmap CreateWhiteBitmap(int w, int h)
    {
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        BitmapDrawingService.Clear(bmp);
        return bmp;
    }

    private static WriteableBitmap CreateTransparentBitmap(int w, int h)
        => new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
}
