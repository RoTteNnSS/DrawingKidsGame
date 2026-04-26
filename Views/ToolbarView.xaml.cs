using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ColorKids.Models;
using ColorKids.Services;
using ColorKids.ViewModels;
using Microsoft.Win32;

namespace ColorKids.Views;

public partial class ToolbarView : UserControl
{
    private MainViewModel? _vm;
    private readonly ToggleButton[] _toolButtons = [];

    public ToolbarView()
    {
        InitializeComponent();
        _toolButtons = [BrushBtn, EraserBtn, FillBtn];
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old)
            old.PropertyChanged -= OnVmPropertyChanged;

        if (e.NewValue is MainViewModel vm)
        {
            _vm = vm;
            BuildPalette(vm);
            SyncToolButtons(vm.ActiveTool);
            UpdateActiveColorIndicator(vm.ActiveColor);
            UndoBtn.IsEnabled = vm.CanUndo;
            BrushSlider.Value = vm.BrushSize;
            TemplateList.ItemsSource = MainViewModel.Templates;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ActiveTool):
                SyncToolButtons(_vm.ActiveTool);
                break;
            case nameof(MainViewModel.ActiveColor):
                UpdateActiveColorIndicator(_vm.ActiveColor);
                break;
            case nameof(MainViewModel.CanUndo):
                UndoBtn.IsEnabled = _vm.CanUndo;
                break;
            case nameof(MainViewModel.BrushSize):
                if (Math.Abs(BrushSlider.Value - _vm.BrushSize) > 0.5)
                    BrushSlider.Value = _vm.BrushSize;
                break;
            case nameof(MainViewModel.IsProcessing):
                ProcessingIndicator.Visibility = _vm.IsProcessing ? Visibility.Visible : Visibility.Collapsed;
                ImportBtn.IsEnabled = !_vm.IsProcessing;
                break;
            case nameof(MainViewModel.ZoomLevel):
                ZoomLabel.Text = $"{(int)(_vm.ZoomLevel * 100)}%";
                break;
        }
    }

    private void UpdateActiveColorIndicator(Color color)
    {
        ActiveColorBorder.Background = new SolidColorBrush(color);
    }

    private void BuildPalette(MainViewModel vm)
    {
        PaletteGrid.Children.Clear();
        ToggleButton? firstBtn = null;

        foreach (var color in MainViewModel.Palette)
        {
            var btn = new ToggleButton
            {
                Style = (Style)FindResource("ColorSwatchStyle"),
                Background = new SolidColorBrush(color),
                Tag = color
            };
            btn.Click += OnColorButtonClick;
            PaletteGrid.Children.Add(btn);
            firstBtn ??= btn;
        }

        // Select first color by default
        if (firstBtn is not null)
        {
            firstBtn.IsChecked = true;
            vm.ActiveColor = (Color)firstBtn.Tag;
        }
    }

    private void OnColorButtonClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not ToggleButton clicked) return;

        foreach (ToggleButton btn in PaletteGrid.Children)
            btn.IsChecked = btn == clicked;

        _vm.SelectColorCommand.Execute((Color)clicked.Tag);
    }

    private void OnColorPickerClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var picker = new ColorPickerWindow(_vm.ActiveColor) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() == true)
        {
            var picked = picker.SelectedColor;
            // Désélectionner tous les swatches de palette
            foreach (ToggleButton btn in PaletteGrid.Children)
                btn.IsChecked = false;
            _vm.SelectColorCommand.Execute(picked);
        }
    }

    internal void OnToolButtonClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not ToggleButton clicked) return;
        if (clicked.Tag is string tagStr && Enum.TryParse<DrawingTool>(tagStr, out var tool))
        {
            _vm.SelectToolCommand.Execute(tool);
            SyncToolButtons(tool);
        }
    }

    private void OnBrushSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_vm is not null)
            _vm.BrushSize = e.NewValue;
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
            UndoRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
            ClearRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnTemplateClick(object sender, RoutedEventArgs e)
    {
        TemplatePopup.IsOpen = true;
    }

    private void OnTemplateItemClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button btn || btn.Tag is not DrawingTemplate template) return;
        TemplatePopup.IsOpen = false;
        _vm.SelectTemplateCommand.Execute(template);
        _vm.ActiveTool = DrawingTool.Brush;
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.IsProcessing) return;

        var dlg = new OpenFileDialog
        {
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.svg|SVG|*.svg|Images matricielles|*.jpg;*.jpeg;*.png;*.bmp",
            Title  = "Choisir une image à colorier"
        };
        if (dlg.ShowDialog() != true) return;

        _vm.IsProcessing = true;
        _vm.ActiveTool   = DrawingTool.Brush;

        try
        {
            _vm.ClearDrawingLayer();

            bool isSvg = dlg.FileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
            if (isSvg)
                await SvgImportService.GenerateAsync(dlg.FileName, _vm.OutlineLayer);
            else
                await ImageToOutlineService.GenerateAsync(dlg.FileName, _vm.OutlineLayer);
            _vm.HasOutline = true;
            // Recompose with fresh outline
            Services.CompositeRenderService.RenderFull(
                _vm.BackgroundLayer, _vm.DrawingLayer, _vm.OutlineLayer, _vm.CompositeOutput);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show($"Erreur lors du traitement de l'image :\n{ex.Message}",
                "ColorKids", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _vm.IsProcessing = false;
        }
    }

    internal event EventHandler? UndoRequested;
    internal event EventHandler? ClearRequested;

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ZoomInCommand.Execute(null);
        ZoomLabel.Text = $"{(int)(_vm.ZoomLevel * 100)}%";
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ZoomOutCommand.Execute(null);
        ZoomLabel.Text = $"{(int)(_vm.ZoomLevel * 100)}%";
    }

    private void OnZoomResetClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ZoomResetCommand.Execute(null);
        ZoomLabel.Text = "100%";
    }

    private void SyncToolButtons(DrawingTool active)
    {
        BrushBtn.IsChecked  = active == DrawingTool.Brush;
        EraserBtn.IsChecked = active == DrawingTool.Eraser;
        FillBtn.IsChecked   = active == DrawingTool.Fill;
    }
}
