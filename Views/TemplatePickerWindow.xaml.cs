using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ColorKids.Models;
using ColorKids.Services;

namespace ColorKids.Views;

/// <summary>
/// Fenêtre de sélection d'un dessin intégré avec filtrage par catégorie
/// et aperçu miniature SVG rendu via WPF PathGeometry.
/// </summary>
public partial class TemplatePickerWindow : Window
{
    private string _selectedCategory = "Tous";

    /// <summary>Template sélectionné par l'utilisateur, ou null si annulé.</summary>
    public DrawingTemplate? SelectedTemplate { get; private set; }

    public TemplatePickerWindow()
    {
        InitializeComponent();
        BuildCategoryBar();
        PopulateTemplates("Tous");
    }

    // ── Category bar ──────────────────────────────────────────────────────

    private void BuildCategoryBar()
    {
        var categories = new[] { "Tous" }
            .Concat(OutlineGeneratorService.Templates.Select(t => t.Category).Distinct().Order())
            .ToArray();

        foreach (var cat in categories)
        {
            var btn = new ToggleButton
            {
                Content   = cat,
                Style     = (Style)FindResource("CategoryButtonStyle"),
                IsChecked = cat == _selectedCategory,
                Tag       = cat,
            };
            btn.Click += OnCategoryClick;
            CategoryPanel.Children.Add(btn);
        }
    }

    private void OnCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        // Uncheck all others
        foreach (ToggleButton btn in CategoryPanel.Children)
            btn.IsChecked = btn == clicked;

        _selectedCategory = clicked.Tag as string ?? "Tous";
        PopulateTemplates(_selectedCategory);
    }

    // ── Template grid ─────────────────────────────────────────────────────

    private void PopulateTemplates(string category)
    {
        TemplatesPanel.Children.Clear();

        var filtered = category == "Tous"
            ? OutlineGeneratorService.Templates
            : OutlineGeneratorService.Templates.Where(t => t.Category == category).ToArray();

        foreach (var template in filtered)
        {
            var card = CreateCard(template);
            TemplatesPanel.Children.Add(card);
        }
    }

    private Button CreateCard(DrawingTemplate template)
    {
        // Preview path rendered small
        Geometry? geom = null;
        try { geom = Geometry.Parse(template.PathData).Clone(); }
        catch { /* invalid path: show text only */ }

        var cardContent = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };

        if (geom is not null)
        {
            // Scale geometry to fit 90×90 preview with padding
            const double previewSize = 90.0;
            const double padding     = 8.0;

            var pen = new Pen(Brushes.White, template.StrokeThickness);
            var bounds = geom.GetRenderBounds(pen);
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
            {
                double scale = Math.Min(
                    (previewSize - padding * 2) / bounds.Width,
                    (previewSize - padding * 2) / bounds.Height);

                double tx = padding - bounds.X * scale + (previewSize - padding * 2 - bounds.Width  * scale) / 2.0;
                double ty = padding - bounds.Y * scale + (previewSize - padding * 2 - bounds.Height * scale) / 2.0;

                var tg = new TransformGroup();
                tg.Children.Add(new ScaleTransform(scale, scale));
                tg.Children.Add(new TranslateTransform(tx, ty));
                geom.Transform = tg;
            }

            double strokePx = Math.Min(template.StrokeThickness * 0.5, 3.5);
            cardContent.Children.Add(new System.Windows.Shapes.Path
            {
                Data            = geom,
                Stroke          = Brushes.White,
                StrokeThickness = strokePx,
                Fill            = Brushes.Transparent,
                Width           = 90,
                Height          = 90,
                Stretch         = Stretch.None,
            });
        }

        cardContent.Children.Add(new TextBlock
        {
            Text              = template.Name,
            Foreground        = Brushes.White,
            FontSize          = 11,
            TextWrapping      = TextWrapping.Wrap,
            TextAlignment     = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth          = 110,
            Margin            = new Thickness(0, 4, 0, 0),
        });

        var btn = new Button
        {
            Style   = (Style)FindResource("TemplateCardStyle"),
            Content = cardContent,
            Tag     = template,
            ToolTip = $"{template.Name} ({template.Category})",
        };
        btn.Click += OnTemplateCardClick;
        return btn;
    }

    private void OnTemplateCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DrawingTemplate template)
        {
            SelectedTemplate = template;
            DialogResult     = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
