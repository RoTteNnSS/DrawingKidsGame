using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ColorKids.Services;
using ColorKids.ViewModels;

namespace ColorKids;

public partial class MainWindow : Window
{
    private MainViewModel _vm = null!;

    private bool _isFullscreen;
    private WindowStyle   _prevStyle;
    private WindowState   _prevState;
    private ResizeMode    _prevResize;

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref RECT rect);
    [DllImport("user32.dll")]
    private static extern bool ClipCursor(nint ptr);  // ptr=0 to release

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);
    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int    cbSize;
        public RECT   rcMonitor;  // full monitor bounds
        public RECT   rcWork;
        public uint   dwFlags;
    }

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel();
        Toolbar.DataContext       = _vm;
        DrawingCanvas.DataContext = _vm;

        Toolbar.UndoRequested  += (_, _) => DrawingCanvas.PerformUndo();
        Toolbar.ClearRequested += (_, _) => DrawingCanvas.PerformClear();

        // Keyboard shortcuts
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save,   (_, _) => SaveAs("png")));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Print,  (_, _) => PrintDrawing()));

        // F12 toggle fullscreen
        KeyDown    += OnKeyDown;
        Activated  += (_, _) => { if (_isFullscreen) ApplyCursorClip(); };
        Deactivated += (_, _) => ReleaseCursorClip();
        Closing    += (_, _) => ReleaseCursorClip();
    }

    // ── Fullscreen ─────────────────────────────────────────────────

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12)
            ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
            LeaveFullscreen();
        else
            EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        _prevStyle  = WindowStyle;
        _prevState  = WindowState;
        _prevResize = ResizeMode;

        var hwnd    = new WindowInteropHelper(this).Handle;
        var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMonitor, ref mi);

        WindowStyle = WindowStyle.None;
        ResizeMode  = ResizeMode.NoResize;
        // Use Normal + explicit bounds to stay on the current monitor
        WindowState = WindowState.Normal;
        Left   = mi.rcMonitor.Left;
        Top    = mi.rcMonitor.Top;
        Width  = mi.rcMonitor.Right  - mi.rcMonitor.Left;
        Height = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        _isFullscreen = true;

        ApplyCursorClip();
    }

    private void LeaveFullscreen()
    {
        WindowStyle = _prevStyle;
        ResizeMode  = _prevResize;
        WindowState = _prevState;
        _isFullscreen = false;

        ReleaseCursorClip();
    }

    /// <summary>Confine le curseur aux limites de cette fenêtre (évite la fuite sur un 2e moniteur).</summary>
    private void ApplyCursorClip()
    {
        var hwnd    = new WindowInteropHelper(this).Handle;
        var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref mi)) return;

        var rect = mi.rcMonitor;
        ClipCursor(ref rect);
    }

    private static void ReleaseCursorClip() => ClipCursor(nint.Zero);

    // ── Menu handlers ──────────────────────────────────────────────

    private void OnSavePng(object sender, RoutedEventArgs e)  => SaveAs("png");
    private void OnSaveJpeg(object sender, RoutedEventArgs e) => SaveAs("jpeg");
    private void OnSaveBmp(object sender, RoutedEventArgs e)  => SaveAs("bmp");
    private void OnSaveSvg(object sender, RoutedEventArgs e)  => SaveAs("svg");
    private void OnPrint(object sender, RoutedEventArgs e)    => PrintDrawing();
    private void OnExit(object sender, RoutedEventArgs e)     => Close();
    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

    // ── Save ───────────────────────────────────────────────────────

    private void SaveAs(string format)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title            = "Enregistrer le dessin",
            FileName         = "dessin",
            DefaultExt       = $".{(format == "jpeg" ? "jpg" : format)}",
            Filter           = format switch
            {
                "png"  => "PNG Image|*.png",
                "jpeg" => "JPEG Image|*.jpg;*.jpeg",
                "bmp"  => "Bitmap Image|*.bmp",
                "svg"  => "SVG Vector|*.svg",
                _      => "All files|*.*"
            }
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            if (format == "svg")
                ExportService.SaveSvg(_vm.CompositeOutput, dlg.FileName);
            else
                ExportService.SaveBitmap(_vm.CompositeOutput, dlg.FileName, format);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erreur lors de l'enregistrement :\n{ex.Message}",
                "ColorKids", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Print ──────────────────────────────────────────────────────

    private void PrintDrawing()
    {
        var dlg = new System.Windows.Controls.PrintDialog();
        if (dlg.ShowDialog() != true) return;

        try
        {
            ExportService.Print(_vm.CompositeOutput, dlg);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erreur lors de l'impression :\n{ex.Message}",
                "ColorKids", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
