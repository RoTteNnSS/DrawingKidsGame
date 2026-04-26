using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ColorKids.Services;

/// <summary>
/// Fusionne BackgroundLayer + DrawingLayer + OutlineLayer vers un CompositeOutput.
/// Toutes les méthodes sont synchrones et doivent être appelées sur le thread UI.
/// Format attendu : Pbgra32 (pré-multiplié, 4 octets par pixel B G R A).
/// </summary>
public static class CompositeRenderService
{
    /// <summary>
    /// Recompose la totalité du composite.
    /// À appeler après un Clear ou un chargement d'outline.
    /// </summary>
    public static void RenderFull(
        WriteableBitmap background,
        WriteableBitmap drawing,
        WriteableBitmap outline,
        WriteableBitmap output)
        => RenderRegion(background, drawing, outline, output,
                        new Int32Rect(0, 0, output.PixelWidth, output.PixelHeight));

    /// <summary>
    /// Recompose uniquement la dirty region fournie (perf — appel par coup de pinceau).
    /// </summary>
    public static void RenderRegion(
        WriteableBitmap background,
        WriteableBitmap drawing,
        WriteableBitmap outline,
        WriteableBitmap output,
        Int32Rect dirty)
    {
        int w = output.PixelWidth;
        int h = output.PixelHeight;

        dirty = Clamp(dirty, w, h);
        if (dirty.Width <= 0 || dirty.Height <= 0) return;

        int stride = w * 4;
        int regionBytes = dirty.Width * dirty.Height * 4;

        // Read the three layers into flat buffers (dirty region only).
        // bgBuf is reused as the output buffer to avoid a 4th allocation.
        var bgBuf  = CopyRegion(background, dirty, stride);
        var drBuf  = CopyRegion(drawing,    dirty, stride);
        var otBuf  = CopyRegion(outline,    dirty, stride);

        int regionStride = dirty.Width * 4;

        for (int i = 0; i < regionBytes; i += 4)
        {
            // Background (always opaque white)
            byte bgB = bgBuf[i];
            byte bgG = bgBuf[i + 1];
            byte bgR = bgBuf[i + 2];

            // Drawing layer — alpha blend over background
            byte drA = drBuf[i + 3];
            byte drB = drBuf[i];
            byte drG = drBuf[i + 1];
            byte drR = drBuf[i + 2];
            byte rb, rg, rr;
            if (drA == 255)
            {
                rb = drB; rg = drG; rr = drR;
            }
            else if (drA == 0)
            {
                rb = bgB; rg = bgG; rr = bgR;
            }
            else
            {
                int inv = 255 - drA;
                rb = (byte)((drB * drA + bgB * inv) / 255);
                rg = (byte)((drG * drA + bgG * inv) / 255);
                rr = (byte)((drR * drA + bgR * inv) / 255);
            }

            // Outline layer — paint over result (black outline, transparent bg)
            byte otA = otBuf[i + 3];
            if (otA == 255)
            {
                rb = otBuf[i]; rg = otBuf[i + 1]; rr = otBuf[i + 2];
            }
            else if (otA > 0)
            {
                int inv = 255 - otA;
                rb = (byte)((otBuf[i]     * otA + rb * inv) / 255);
                rg = (byte)((otBuf[i + 1] * otA + rg * inv) / 255);
                rr = (byte)((otBuf[i + 2] * otA + rr * inv) / 255);
            }

            // Write result back into bgBuf (avoids a 4th allocation)
            bgBuf[i]     = rb;
            bgBuf[i + 1] = rg;
            bgBuf[i + 2] = rr;
            bgBuf[i + 3] = 255;
        }

        output.WritePixels(dirty, bgBuf, regionStride, 0);
    }

    // ── helpers ──────────────────────────────────────────────────

    private static byte[] CopyRegion(WriteableBitmap bmp, Int32Rect region, int fullStride)
    {
        int regionStride = region.Width * 4;
        var buf = new byte[region.Width * region.Height * 4];
        bmp.CopyPixels(region, buf, regionStride, 0);
        return buf;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Int32Rect Clamp(Int32Rect r, int w, int h)
    {
        int x1 = Math.Max(0, r.X);
        int y1 = Math.Max(0, r.Y);
        int x2 = Math.Min(w, r.X + r.Width);
        int y2 = Math.Min(h, r.Y + r.Height);
        return new Int32Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }
}
