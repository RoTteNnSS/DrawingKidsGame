using System.Collections;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ColorKids.Services;

/// <summary>
/// Low-level drawing operations on a WriteableBitmap (Pbgra32 format).
/// All methods are synchronous and must be called on the UI thread.
/// </summary>
public static class BitmapDrawingService
{

    public static void Clear(WriteableBitmap bmp)
    {
        int stride = bmp.PixelWidth * 4;
        var white = new byte[stride * bmp.PixelHeight];
        // Chaque pixel Pbgra32 blanc = 0xFFFFFFFF ; on remplit en int pour aller 4x plus vite
        MemoryMarshal.Cast<byte, int>(white.AsSpan()).Fill(-1); // -1 == 0xFFFFFFFF
        bmp.WritePixels(new Int32Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight), white, stride, 0);
    }

    // ──────────────────────────────────────────────────────────────
    //  Brush / Eraser stroke
    // ──────────────────────────────────────────────────────────────

    // ──────────────────────────────────────────────────────────────
    //  Flood fill (BFS iterative)
    // ──────────────────────────────────────────────────────────────

    // ──────────────────────────────────────────────────────────────
    //  Layer-aware variants (3-layer system)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws a brush stroke on <paramref name="drawing"/> while skipping pixels
    /// that are covered by an opaque black outline in <paramref name="outline"/>.
    /// </summary>
    public static void DrawStrokeMasked(
        WriteableBitmap drawing,
        WriteableBitmap outline,
        Point from, Point to,
        Color color, double radius)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy)));

        int r  = (int)radius;
        int w  = drawing.PixelWidth;
        int h  = drawing.PixelHeight;

        int dirtyX1 = Math.Max(0, (int)Math.Min(from.X, to.X) - r);
        int dirtyY1 = Math.Max(0, (int)Math.Min(from.Y, to.Y) - r);
        int dirtyX2 = Math.Min(w - 1, (int)Math.Max(from.X, to.X) + r);
        int dirtyY2 = Math.Min(h - 1, (int)Math.Max(from.Y, to.Y) + r);

        // Read only the dirty region of the outline (avoids copying the entire bitmap)
        int regionW      = dirtyX2 - dirtyX1 + 1;
        int regionH      = dirtyY2 - dirtyY1 + 1;
        int regionStride = regionW * 4;
        var outlineRegion = new Int32Rect(dirtyX1, dirtyY1, regionW, regionH);
        var outlineBuf    = new byte[regionStride * regionH];
        outline.CopyPixels(outlineRegion, outlineBuf, regionStride, 0);

        drawing.Lock();
        try
        {
            for (int i = 0; i <= steps; i++)
            {
                double t = steps == 0 ? 0 : (double)i / steps;
                double cx = from.X + dx * t;
                double cy = from.Y + dy * t;
                FillCircleMasked(drawing, outlineBuf, dirtyX1, dirtyY1, regionW, (int)cx, (int)cy, r, color);
            }
        }
        finally
        {
            int dw = dirtyX2 - dirtyX1 + 1;
            int dh = dirtyY2 - dirtyY1 + 1;
            if (dw > 0 && dh > 0)
                drawing.AddDirtyRect(new Int32Rect(dirtyX1, dirtyY1, dw, dh));
            drawing.Unlock();
        }
    }

    /// <summary>
    /// Flood-fills <paramref name="drawing"/> stopping at opaque pixels in <paramref name="outline"/>.
    /// </summary>
    public static void FloodFillMasked(
        WriteableBitmap drawing,
        WriteableBitmap outline,
        Point hitPoint, Color fillColor, int tolerance = 10)
    {
        int w = drawing.PixelWidth;
        int h = drawing.PixelHeight;
        int stride = w * 4;

        int startX = Math.Clamp((int)hitPoint.X, 0, w - 1);
        int startY = Math.Clamp((int)hitPoint.Y, 0, h - 1);

        var drBuf = new byte[stride * h];
        var otBuf = new byte[stride * h];
        drawing.CopyPixels(drBuf, stride, 0);
        outline.CopyPixels(otBuf, stride, 0);

        // Bail if start pixel is on the outline
        if (otBuf[startY * stride + startX * 4 + 3] > 128) return;

        int startIdx = startY * stride + startX * 4;
        byte tb = drBuf[startIdx];
        byte tg = drBuf[startIdx + 1];
        byte tr = drBuf[startIdx + 2];
        byte ta = drBuf[startIdx + 3];

        byte fb = fillColor.B;
        byte fg = fillColor.G;
        byte fr = fillColor.R;
        const byte fa = 255;

        if (tb == fb && tg == fg && tr == fr && ta == fa) return;

        var queue   = new System.Collections.Generic.Queue<int>();
        var visited = new System.Collections.BitArray(w * h);

        queue.Enqueue(startY * w + startX);
        visited[startY * w + startX] = true;

        while (queue.Count > 0)
        {
            int pos = queue.Dequeue();
            int py  = pos / w;
            int px  = pos % w;
            int idx = py * stride + px * 4;

            drBuf[idx]     = fb;
            drBuf[idx + 1] = fg;
            drBuf[idx + 2] = fr;
            drBuf[idx + 3] = fa;

            TryEnqueue(px - 1, py);
            TryEnqueue(px + 1, py);
            TryEnqueue(px, py - 1);
            TryEnqueue(px, py + 1);
        }

        drawing.WritePixels(new Int32Rect(0, 0, w, h), drBuf, stride, 0);
        return;

        void TryEnqueue(int x, int y)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            int vi = y * w + x;
            if (visited[vi]) return;
            visited[vi] = true;
            // Blocked by outline
            if (otBuf[y * stride + x * 4 + 3] > 128) return;
            int idx = y * stride + x * 4;
            if (ColorMatch(drBuf[idx], drBuf[idx + 1], drBuf[idx + 2], drBuf[idx + 3],
                           tb, tg, tr, ta, tolerance))
                queue.Enqueue(vi);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Private helpers
    // ──────────────────────────────────────────────────────────────

    private static bool ColorMatch(byte b, byte g, byte r, byte a,
                                   byte tb, byte tg, byte tr, byte ta, int tol)
        => Math.Abs(b - tb) <= tol &&
           Math.Abs(g - tg) <= tol &&
           Math.Abs(r - tr) <= tol &&
           Math.Abs(a - ta) <= tol;

    /// <summary>
    /// Like FillCircleUnsafe but skips pixels where outlineBuf[pixel].A > 128.
    /// outlineBuf covers only [regionX .. regionX+regionW) × [regionY .. regionY+regionH).
    /// Caller must have already called bmp.Lock().
    /// </summary>
    private static unsafe void FillCircleMasked(
        WriteableBitmap bmp, byte[] outlineBuf,
        int regionX, int regionY, int regionW,
        int cx, int cy, int r, Color color)
    {
        int w = bmp.PixelWidth;
        int h = bmp.PixelHeight;
        nint backBuffer = bmp.BackBuffer;
        int stride = bmp.BackBufferStride;

        byte cb = color.B, cg = color.G, cr = color.R;
        int rSq = r * r;
        int yMin = Math.Max(0, cy - r);
        int yMax = Math.Min(h - 1, cy + r);

        for (int py = yMin; py <= yMax; py++)
        {
            int dy = py - cy;
            int xSpan = (int)Math.Sqrt(rSq - dy * dy);
            int xMin = Math.Max(0, cx - xSpan);
            int xMax = Math.Min(w - 1, cx + xSpan);

            byte* row = (byte*)(backBuffer + py * stride);
            for (int px = xMin; px <= xMax; px++)
            {
                // Index into the partial outline buffer using region-relative coords
                int rx = px - regionX;
                int ry = py - regionY;
                if (rx >= 0 && ry >= 0 && rx < regionW &&
                    outlineBuf[ry * regionW * 4 + rx * 4 + 3] > 128)
                    continue;

                int off = px * 4;
                row[off]     = cb;
                row[off + 1] = cg;
                row[off + 2] = cr;
                row[off + 3] = 255;
            }
        }
    }
}
