using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ColorKids.Models;

namespace ColorKids.Services;

/// <summary>
/// Mode 1 — Génère un OutlineLayer depuis un DrawingTemplate (PathGeometry WPF).
/// Résultat : pixels noirs opaques sur fond transparent (Pbgra32).
/// </summary>
public static class OutlineGeneratorService
{
    /// <summary>
    /// Catalogue de templates intégrés pour enfants de 4 ans.
    /// </summary>
    public static readonly DrawingTemplate[] Templates =
    [
        new("Soleil",   "M 100,100 m -60,0 a 60,60 0 1,0 120,0 a 60,60 0 1,0 -120,0 M100,10 L100,0 M100,190 L100,200 M10,100 L0,100 M190,100 L200,100 M129,29 L136,22 M71,171 L64,178 M29,71 L22,64 M171,129 L178,136", 8),
        new("Maison",   "M 50,150 L 50,80 L 100,30 L 150,80 L 150,150 Z M 80,150 L 80,110 L 120,110 L 120,150 Z M 60,95 L 80,95 L 80,115 L 60,115 Z", 6),
        new("Chat",     "M 80,140 Q 100,90 120,140 Z M 60,130 Q 100,60 140,130 Q 140,160 100,170 Q 60,160 60,130 Z M 70,100 L 65,70 L 85,90 Z M 130,100 L 135,70 L 115,90 Z M 90,125 Q 100,130 110,125 M 85,115 A 5,5 0 1,0 95,115 M 105,115 A 5,5 0 1,0 115,115", 5),
        new("Étoile",   "M 100,20 L 115,70 L 170,70 L 125,100 L 140,155 L 100,125 L 60,155 L 75,100 L 30,70 L 85,70 Z", 6),
        new("Fleur",    "M 100,100 m -15,0 a 15,15 0 1,0 30,0 a 15,15 0 1,0 -30,0 M 100,60 a 15,20 0 1,0 0.1,0 M 100,140 a 15,20 0 1,0 0.1,0 M 60,100 a 20,15 0 1,0 0,0.1 M 140,100 a 20,15 0 1,0 0,0.1 M 73,73 a 15,20 45 1,0 0.1,0 M 127,127 a 15,20 45 1,0 0.1,0 M 127,73 a 15,20 135 1,0 0.1,0 M 73,127 a 15,20 135 1,0 0.1,0", 5),
        new("Poisson",  "M 50,100 Q 100,50 160,100 Q 100,150 50,100 Z M 160,100 L 190,70 L 190,130 Z M 85,95 A 7,7 0 1,0 85,96", 5),
        new("Ballon",   "M 100,30 a 55,65 0 1,0 0.1,0 M 100,160 L 100,190 M 85,160 Q 100,175 115,160", 6),
    ];

    /// <summary>
    /// Rastérise le template sur <paramref name="outlineLayer"/> (Pbgra32, fond transparent).
    /// </summary>
    public static void Generate(DrawingTemplate template, WriteableBitmap outlineLayer)
    {
        int w = outlineLayer.PixelWidth;
        int h = outlineLayer.PixelHeight;

        // Parse WPF PathGeometry from the SVG-path-compatible string
        // Clone to avoid InvalidOperationException on frozen (read-only) geometry
        var geometry = Geometry.Parse(template.PathData).Clone();

        // Scale geometry to fit the canvas uniformly with padding
        const double padding = 40.0;
        var bounds = geometry.GetRenderBounds(new Pen(Brushes.Black, template.StrokeThickness));
        if (bounds.IsEmpty || bounds.Width == 0 || bounds.Height == 0)
            bounds = new Rect(0, 0, 200, 200);

        double scaleX = (w - padding * 2) / bounds.Width;
        double scaleY = (h - padding * 2) / bounds.Height;
        double scale  = Math.Min(scaleX, scaleY);

        double tx = padding - bounds.X * scale + (w - padding * 2 - bounds.Width  * scale) / 2.0;
        double ty = padding - bounds.Y * scale + (h - padding * 2 - bounds.Height * scale) / 2.0;

        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(scale, scale));
        transform.Children.Add(new TranslateTransform(tx, ty));
        geometry.Transform = transform;

        // Rasterize via RenderTargetBitmap
        // Cap the rendered stroke so it never exceeds 8 screen pixels regardless of scale.
        double strokePx = Math.Min(template.StrokeThickness * scale, 8.0);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawGeometry(null,
                new Pen(Brushes.Black, strokePx),
                geometry);
        }
        rtb.Render(visual);

        // Copy RTB → OutlineLayer, binarize: non-transparent → pure black
        int stride = w * 4;
        var pixels = new byte[stride * h];
        rtb.CopyPixels(pixels, stride, 0);

        // Binarize + write to outlineLayer
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a > 20)
            {
                pixels[i]     = 0;   // B
                pixels[i + 1] = 0;   // G
                pixels[i + 2] = 0;   // R
                pixels[i + 3] = 255; // A
            }
            else
            {
                pixels[i]     = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                pixels[i + 3] = 0;
            }
        }

        // Morphological dilation — 1 pass 3×3 to slightly thicken the outline
        pixels = Dilate(pixels, w, h, 1);

        outlineLayer.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
    }

    // ── helpers ──────────────────────────────────────────────────

    private static byte[] Dilate(byte[] src, int w, int h, int passes)
    {
        var buf = (byte[])src.Clone();
        int stride = w * 4;
        for (int p = 0; p < passes; p++)
        {
            var next = (byte[])buf.Clone();
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int idx = y * stride + x * 4;
                    if (buf[idx + 3] == 255) continue; // already black
                    // Check 8-neighbours
                    bool hasBlack = false;
                    for (int dy = -1; dy <= 1 && !hasBlack; dy++)
                        for (int dx = -1; dx <= 1 && !hasBlack; dx++)
                        {
                            int ni = (y + dy) * stride + (x + dx) * 4;
                            if (buf[ni + 3] == 255) hasBlack = true;
                        }
                    if (hasBlack)
                    {
                        next[idx]     = 0;
                        next[idx + 1] = 0;
                        next[idx + 2] = 0;
                        next[idx + 3] = 255;
                    }
                }
            }
            buf = next;
        }
        return buf;
    }
}
