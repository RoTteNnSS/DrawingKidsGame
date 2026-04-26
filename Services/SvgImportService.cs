using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace ColorKids.Services;

/// <summary>
/// Importe un fichier SVG dans l'OutlineLayer.
/// Le SVG est rastÃ©risÃ© via SharpVectors (Stretch=Uniform, centrÃ©),
/// puis traitÃ© par le mÃªme pipeline que <see cref="ImageToOutlineService"/>.
/// </summary>
public static class SvgImportService
{
    /// <summary>
    /// Doit Ãªtre appelÃ© depuis le thread UI (SharpVectors est STA-only).
    /// </summary>
    public static async Task GenerateAsync(
        string filePath,
        WriteableBitmap outlineLayer,
        CancellationToken ct = default)
    {
        int dstW = outlineLayer.PixelWidth;
        int dstH = outlineLayer.PixelHeight;

        // â”€â”€ Lire + convertir le SVG en DrawingGroup (thread UI requis) â”€â”€â”€â”€â”€â”€â”€â”€
        var settings = new WpfDrawingSettings
        {
            IncludeRuntime = true,
            TextAsGeometry = false,
        };

        DrawingGroup drawing;
        using var converter = new FileSvgReader(settings);
        drawing = converter.Read(filePath)
                  ?? throw new InvalidOperationException("Le SVG n'a pas pu Ãªtre chargÃ©.");

        var svgBounds = drawing.Bounds;
        if (svgBounds.IsEmpty || svgBounds.Width <= 0 || svgBounds.Height <= 0)
            throw new InvalidOperationException("Le SVG ne contient pas de gÃ©omÃ©trie visible.");

        // â”€â”€ Calcul Stretch=Uniform â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        double scaleX = dstW / svgBounds.Width;
        double scaleY = dstH / svgBounds.Height;
        double scale  = Math.Min(scaleX, scaleY);
        int scaledW   = Math.Max(1, (int)(svgBounds.Width  * scale));
        int scaledH   = Math.Max(1, (int)(svgBounds.Height * scale));
        int offsetX   = (dstW - scaledW) / 2;
        int offsetY   = (dstH - scaledH) / 2;

        // â”€â”€ Rasteriser en RenderTargetBitmap (thread UI) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var rtb    = new RenderTargetBitmap(scaledW, scaledH, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            // Fond blanc pour la dÃ©tection outline
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, scaledW, scaledH));
            ctx.PushTransform(new TranslateTransform(-svgBounds.X * scale, -svgBounds.Y * scale));
            ctx.PushTransform(new ScaleTransform(scale, scale));
            ctx.DrawDrawing(drawing);
            ctx.Pop();
            ctx.Pop();
        }
        rtb.Render(visual);
        rtb.Freeze();

        int stride = scaledW * 4;
        var pixels = new byte[stride * scaledH];
        rtb.CopyPixels(pixels, stride, 0);

        ct.ThrowIfCancellationRequested();

        // â”€â”€ Pipeline outline â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var result = await Task.Run(() =>
        {
            bool isOutline = ImageToOutlineService.DetectOutlineImage(pixels, scaledW, scaledH);
            return isOutline
                ? ImageToOutlineService.ExtractOutlineFromWhiteBackground(pixels, scaledW, scaledH)
                : ImageToOutlineService.ExtractOutlineFromColor(pixels, scaledW, scaledH,
                      sobelThreshold: 60, dilatePasses: 2, ct);
        }, ct);

        // â”€â”€ Ã‰criture centrÃ©e dans outlineLayer â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        int dstStride    = dstW * 4;
        var dstPixels    = new byte[dstStride * dstH];
        int resultStride = scaledW * 4;

        for (int row = 0; row < scaledH; row++)
        {
            int dstRow = row + offsetY;
            if (dstRow < 0 || dstRow >= dstH) continue;
            int srcOff    = row * resultStride;
            int dstOff    = dstRow * dstStride + offsetX * 4;
            int copyBytes = Math.Min(scaledW, dstW - offsetX) * 4;
            if (copyBytes > 0)
                Array.Copy(result, srcOff, dstPixels, dstOff, copyBytes);
        }

        outlineLayer.WritePixels(new Int32Rect(0, 0, dstW, dstH), dstPixels, dstStride, 0);
    }
}