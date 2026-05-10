namespace ColorKids.Models;

/// <summary>Describes a built-in coloring template (Mode 1).</summary>
public sealed record DrawingTemplate(
    string Name,
    string PathData,
    double StrokeThickness = 6.0,
    string Category = "Autres"
);
