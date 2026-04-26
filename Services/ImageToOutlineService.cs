using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ColorKids.Services;

/// <summary>
/// Convertit une image importée en outline noir sur transparent.
/// Pipeline adaptatif :
///   - Image à fond transparent (PNG avec alpha) → extraction directe par canal alpha.
///   - Image outline (fond blanc + lignes noires) → seuillage Otsu.
///   - Image couleur → détection de contours Canny-like (Sobel + NMS + hystérésis).
/// </summary>
public static class ImageToOutlineService
{
    /// <summary>
    /// Charge l'image, génère l'outline et l'écrit dans outlineLayer.
    /// Doit être appelé depuis le thread UI ; les calculs lourds sont déportés sur Task.Run.
    /// </summary>
    public static async Task GenerateAsync(
        string filePath,
        WriteableBitmap outlineLayer,
        int sobelThreshold = 120,
        int dilatePasses   = 1,
        CancellationToken ct = default)
    {
        int dstW = outlineLayer.PixelWidth;
        int dstH = outlineLayer.PixelHeight;

        // Décoder l'image originale
        var src = new BitmapImage();
        src.BeginInit();
        src.UriSource   = new Uri(filePath, UriKind.Absolute);
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.EndInit();
        src.Freeze();

        // Convertir en Pbgra32 pour lecture uniforme 4 octets/pixel
        var formatted = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
        formatted.Freeze();

        int srcW   = formatted.PixelWidth;
        int srcH   = formatted.PixelHeight;
        int stride = srcW * 4;
        var pixels = new byte[stride * srcH];
        formatted.CopyPixels(pixels, stride, 0);

        ct.ThrowIfCancellationRequested();

        // Redimensionnement Stretch=Uniform dans le canvas cible
        double scaleX = (double)dstW / srcW;
        double scaleY = (double)dstH / srcH;
        double scale  = Math.Min(scaleX, scaleY);

        int scaledW = Math.Max(1, (int)(srcW * scale));
        int scaledH = Math.Max(1, (int)(srcH * scale));
        int offsetX = (dstW - scaledW) / 2;
        int offsetY = (dstH - scaledH) / 2;

        byte[] scaled = await Task.Run(() =>
            BilinearResize(pixels, srcW, srcH, scaledW, scaledH), ct);

        ct.ThrowIfCancellationRequested();

        // Détection automatique du type d'image (priorité : transparent > outline > couleur)
        bool isTransparent  = await Task.Run(() => DetectTransparentOutline(scaled, scaledW, scaledH), ct);
        bool isOutlineImage = !isTransparent && await Task.Run(() => DetectOutlineImage(scaled, scaledW, scaledH), ct);

        ct.ThrowIfCancellationRequested();

        // Pipeline d'extraction des contours
        byte[] result = await Task.Run(() =>
        {
            if (isTransparent)
                return ExtractOutlineFromTransparent(scaled, scaledW, scaledH);
            else if (isOutlineImage)
                return ExtractOutlineFromWhiteBackground(scaled, scaledW, scaledH);
            else
                return ExtractOutlineFromColor(scaled, scaledW, scaledH, sobelThreshold, dilatePasses, ct);
        }, ct);

        // Écriture centrée dans outlineLayer
        int dstStride    = dstW * 4;
        var dstPixels    = new byte[dstStride * dstH];
        int resultStride = scaledW * 4;

        for (int row = 0; row < scaledH; row++)
        {
            int srcOff = row * resultStride;
            int dstRow = row + offsetY;
            if (dstRow < 0 || dstRow >= dstH) continue;
            int dstOff   = dstRow * dstStride + offsetX * 4;
            int copyBytes = Math.Min(scaledW, dstW - offsetX) * 4;
            if (copyBytes > 0)
                Array.Copy(result, srcOff, dstPixels, dstOff, copyBytes);
        }

        outlineLayer.WritePixels(new Int32Rect(0, 0, dstW, dstH), dstPixels, dstStride, 0);
    }

    // ── Détection du type d'image ─────────────────────────────────────────────

    /// <summary>
    /// Retourne true si l'image a un fond majoritairement transparent (alpha &lt; 30)
    /// avec des pixels opaques formant les traits. Typique des PNG outline/clip-art.
    /// </summary>
    internal static bool DetectTransparentOutline(byte[] pixels, int w, int h)
    {
        int total = w * h;
        int transparentCount = 0;
        int opaqueCount = 0;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a < 30)  transparentCount++;
            else         opaqueCount++;
        }

        double transparentRatio = (double)transparentCount / total;
        // Au moins 30% de pixels transparents → image avec fond transparent
        return transparentRatio > 0.30;
    }

    /// <summary>
    /// Retourne true si l'image est un outline fond blanc + lignes noires :
    /// >70% de pixels très clairs ET au moins 0.5% de pixels très sombres,
    /// en ignorant les pixels transparents.
    /// </summary>
    internal static bool DetectOutlineImage(byte[] pixels, int w, int h)
    {
        int total      = 0;
        int lightCount = 0;
        int darkCount  = 0;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a < 30) continue;
            byte b = pixels[i]; byte g = pixels[i + 1]; byte r = pixels[i + 2];
            int lum = (int)(0.114 * b + 0.587 * g + 0.299 * r);
            if (lum > 220) lightCount++;
            else if (lum < 50) darkCount++;
            total++;
        }

        if (total == 0) return false;
        double lightRatio = (double)lightCount / total;
        double darkRatio  = (double)darkCount  / total;
        // Image outline : fond très blanc dominant + traits noirs
        return lightRatio > 0.65 && darkRatio > 0.003;
    }

    // ── Pipeline 0 : Image à fond transparent ────────────────────────────────

    /// <summary>
    /// Extrait l'outline depuis une image PNG à fond transparent.
    /// Utilise directement le canal alpha + luminance pour produire un trait propre.
    /// </summary>
    internal static byte[] ExtractOutlineFromTransparent(byte[] pixels, int w, int h)
    {
        var dst = new byte[pixels.Length];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a < 10) continue; // fond transparent → rester transparent

            byte b = pixels[i]; byte g = pixels[i + 1]; byte r = pixels[i + 2];
            int lum = (int)(0.114 * b + 0.587 * g + 0.299 * r);

            // Pixels sombres et opaques → traits noirs
            // Pixels clairs et opaques (remplissage blanc/gris) → ignorer
            if (lum < 180)
            {
                // Opacité combinant alpha source et noirceur
                byte alpha = (byte)Math.Clamp((int)a * (180 - lum) / 180, 0, 255);
                dst[i] = 0; dst[i + 1] = 0; dst[i + 2] = 0; dst[i + 3] = alpha;
            }
        }

        return Denoise(Dilate(dst, w, h, 2), w, h, minIslandSize: 15);
    }

    // ── Pipeline 1 : Image outline fond blanc

    /// <summary>
    /// Extrait les lignes noires sur fond blanc via seuil adaptatif Otsu.
    /// </summary>
    internal static byte[] ExtractOutlineFromWhiteBackground(byte[] pixels, int w, int h)
    {
        int threshold = Math.Clamp(OtsuThreshold(pixels), 80, 180);
        int stride    = w * 4;
        const int margin = 5;
        var dst = new byte[pixels.Length];

        for (int y = 0; y < h; y++)
        {
            bool inYMargin = y < margin || y >= h - margin;
            for (int x = 0; x < w; x++)
            {
                if (inYMargin || x < margin || x >= w - margin) continue; // ignorer les bords
                int i = y * stride + x * 4;
                byte b = pixels[i]; byte g = pixels[i + 1]; byte r = pixels[i + 2];
                int lum = (int)(0.114 * b + 0.587 * g + 0.299 * r);
                if (lum < threshold)
                {
                    dst[i] = 0; dst[i + 1] = 0; dst[i + 2] = 0; dst[i + 3] = 255;
                }
            }
        }

        return Denoise(Dilate(dst, w, h, 2), w, h, minIslandSize: 20);
    }

    /// <summary>Calcule le seuil optimal
    private static int OtsuThreshold(byte[] pixels)
    {
        var hist  = new int[256];
        int total = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a < 30) continue;
            byte b = pixels[i]; byte g = pixels[i + 1]; byte r = pixels[i + 2];
            int lum = (int)(0.114 * b + 0.587 * g + 0.299 * r);
            hist[lum]++;
            total++;
        }
        if (total == 0) return 128;

        double sum = 0;
        for (int t = 0; t < 256; t++) sum += t * hist[t];

        double sumB = 0; int wB = 0; double maxVar = 0; int thr = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            int wF = total - wB;
            if (wF == 0) break;
            sumB += t * hist[t];
            double mB = sumB / wB;
            double mF = (sum - sumB) / wF;
            double v  = (double)wB * wF * (mB - mF) * (mB - mF);
            if (v > maxVar) { maxVar = v; thr = t; }
        }
        return thr;
    }

    // ── Pipeline 2 : Image couleur → Coloring Book ───────────────────────────
    //
    // Stratégie "forme globale" :
    //   1. Blur fort → niveler texture et dégradés
    //   2. Seuil Otsu → masque binaire (sujet vs fond)
    //   3. Morphological Closing (dilate+erode) → bouche les trous dans le masque
    //   4. FillHoles (flood-fill depuis les bords) → fond = background, reste = sujet plein
    //   5. Extraction du contour (boundary) → ligne propre 1px
    //   6. Dilate final → trait épais adapté au coloriage
    //   7. Fusion avec Canny léger → ajouter les détails principaux (yeux, bouche…)

    internal static byte[] ExtractOutlineFromColor(
        byte[] pixels, int w, int h,
        int sobelThreshold, int dilatePasses,
        CancellationToken ct)
    {
        var gray = ToGrayscale(pixels, w, h);

        // ── Passe 1 : Forme globale (morphologie) ──────────────────────────
        // Blur fort pour effacer texture et dégradés
        var blurHeavy = gray;
        for (int i = 0; i < 6; i++) blurHeavy = GaussianBlur5x5(blurHeavy, w, h);
        ct.ThrowIfCancellationRequested();

        // Seuil Otsu sur la version très floutée → masque sujet/fond fiable
        int otsuT  = OtsuThresholdGray(blurHeavy, w, h);
        bool[] mask = ThresholdToBoolMask(blurHeavy, w, h, otsuT);

        // Closing morphologique (dilate 8px puis erode 8px) → bouche les trous internes
        mask = DilateMask(mask, w, h, 8);
        ct.ThrowIfCancellationRequested();
        mask = ErodeMask(mask, w, h, 8);
        ct.ThrowIfCancellationRequested();

        // FillHoles : tout ce qui est fond (atteignable depuis un bord) devient false
        FillBackground(mask, w, h);

        // Dilate le masque de 3px avant extraction du contour → trait plus épais
        mask = DilateMask(mask, w, h, 3);

        // Extraire le contour (boundary) du masque
        var shapeEdges = ExtractBoundary(mask, w, h);

        // ── Passe 2 : Détails (Canny léger) ───────────────────────────────
        var blurLight = GaussianBlur5x5(gray, w, h);
        blurLight     = GaussianBlur5x5(blurLight, w, h);
        ct.ThrowIfCancellationRequested();

        SobelWithAngle(blurLight, w, h, out float[] mag, out float[] angle);
        var nms   = NonMaximumSuppression(mag, angle, w, h);
        int highT = ComputeAdaptiveThreshold(mag, sobelThreshold);
        var cannyEdges = HysteresisThreshold(nms, w, h, highT / 2, highT);

        // Masquer Canny : ne garder que les détails à l'intérieur du masque sujet
        cannyEdges = MaskToSubject(cannyEdges, mask, w, h);
        cannyEdges = Denoise(cannyEdges, w, h, minIslandSize: 120);
        ct.ThrowIfCancellationRequested();

        // ── Fusion forme + détails ─────────────────────────────────────────
        var merged = MergeEdges(shapeEdges, cannyEdges, w, h);
        return Dilate(merged, w, h, 1);
    }

    // ── Helpers morphologiques ────────────────────────────────────────────────

    /// <summary>Seuil Otsu sur tableau grayscale Pbgra32 (canal B = luminance).</summary>
    private static int OtsuThresholdGray(byte[] gray, int w, int h)
    {
        var hist  = new int[256];
        int total = w * h;
        for (int i = 0; i < gray.Length; i += 4) hist[gray[i]]++;

        double sum = 0;
        for (int t = 0; t < 256; t++) sum += t * hist[t];
        double sumB = 0; int wB = 0; double maxVar = 0; int thr = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t]; if (wB == 0) continue;
            int wF = total - wB; if (wF == 0) break;
            sumB += t * hist[t];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            double v = (double)wB * wF * (mB - mF) * (mB - mF);
            if (v > maxVar) { maxVar = v; thr = t; }
        }
        return thr;
    }

    /// <summary>Crée un masque bool[] : true = pixel sombre (sujet), false = fond clair.</summary>
    private static bool[] ThresholdToBoolMask(byte[] gray, int w, int h, int threshold)
    {
        var mask = new bool[w * h];
        for (int i = 0; i < gray.Length; i += 4)
            mask[i / 4] = gray[i] < threshold;
        return mask;
    }

    /// <summary>Dilatation morphologique sur masque bool[].</summary>
    private static bool[] DilateMask(bool[] src, int w, int h, int radius)
    {
        var dst = new bool[src.Length];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int idx = y * w + x;
            if (src[idx]) { dst[idx] = true; continue; }
            bool found = false;
            for (int dy = -radius; dy <= radius && !found; dy++)
            for (int dx = -radius; dx <= radius && !found; dx++)
            {
                int ny = y + dy, nx = x + dx;
                if (ny >= 0 && ny < h && nx >= 0 && nx < w && src[ny * w + nx]) found = true;
            }
            dst[idx] = found;
        }
        return dst;
    }

    /// <summary>Érosion morphologique sur masque bool[].</summary>
    private static bool[] ErodeMask(bool[] src, int w, int h, int radius)
    {
        var dst = new bool[src.Length];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int idx = y * w + x;
            if (!src[idx]) continue;
            bool allTrue = true;
            for (int dy = -radius; dy <= radius && allTrue; dy++)
            for (int dx = -radius; dx <= radius && allTrue; dx++)
            {
                int ny = y + dy, nx = x + dx;
                if (ny < 0 || ny >= h || nx < 0 || nx >= w || !src[ny * w + nx]) allTrue = false;
            }
            dst[idx] = allTrue;
        }
        return dst;
    }

    /// <summary>
    /// Flood-fill depuis tous les bords : marque en false tout pixel false accessible.
    /// Les pixels true (sujet) ne sont jamais franchis → les trous internes restent true.
    /// </summary>
    private static void FillBackground(bool[] mask, int w, int h)
    {
        var queue = new System.Collections.Generic.Queue<int>();
        void Seed(int x, int y)
        {
            int idx = y * w + x;
            if (!mask[idx]) { mask[idx] = true; queue.Enqueue(idx); } // true = "visité comme fond"
        }
        // On va inverser la convention ici : flood-fill pour marquer "fond" à true temporairement
        // puis à la fin tout ce qui était false ET atteignable depuis les bords → false (fond)
        // On utilise un tableau séparé pour ne pas polluer le masque
        var background = new bool[mask.Length]; // true = fond atteignable
        var q2 = new System.Collections.Generic.Queue<int>();

        void SeedBg(int x, int y) {
            int idx = y * w + x;
            if (!mask[idx] && !background[idx]) { background[idx] = true; q2.Enqueue(idx); }
        }

        for (int x = 0; x < w; x++) { SeedBg(x, 0); SeedBg(x, h - 1); }
        for (int y = 0; y < h; y++) { SeedBg(0, y); SeedBg(w - 1, y); }

        while (q2.Count > 0)
        {
            int pos = q2.Dequeue();
            int py = pos / w, px = pos % w;
            foreach (var (nx, ny) in new[]{(px-1,py),(px+1,py),(px,py-1),(px,py+1)})
            {
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                int ni = ny * w + nx;
                if (!mask[ni] && !background[ni]) { background[ni] = true; q2.Enqueue(ni); }
            }
        }

        // Pixels false mais non atteignables depuis les bords = trous internes → true (sujet)
        for (int i = 0; i < mask.Length; i++)
            if (!mask[i] && !background[i]) mask[i] = true;
    }

    /// <summary>Extrait le contour (boundary) du masque : pixels true ayant au moins un voisin false.</summary>
    private static byte[] ExtractBoundary(bool[] mask, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            if (!mask[y * w + x]) continue;
            bool isBoundary =
                !mask[(y - 1) * w + x] || !mask[(y + 1) * w + x] ||
                !mask[y * w + (x - 1)] || !mask[y * w + (x + 1)];
            if (isBoundary) dst[(y * w + x) * 4 + 3] = 255;
        }
        return dst;
    }

    /// <summary>Masque les pixels Canny à l'extérieur du sujet (garde uniquement les détails intérieurs).</summary>
    private static byte[] MaskToSubject(byte[] edges, bool[] mask, int w, int h)
    {
        var dst = new byte[edges.Length];
        for (int i = 0; i < mask.Length; i++)
            if (mask[i] && edges[i * 4 + 3] != 0)
                dst[i * 4 + 3] = 255;
        return dst;
    }

    /// <summary>Fusionne deux tableaux de contours Pbgra32 : pixel noir si l'un ou l'autre est noir.</summary>
    private static byte[] MergeEdges(byte[] a, byte[] b, int w, int h)
    {
        var dst = new byte[a.Length];
        for (int i = 3; i < dst.Length; i += 4)
            dst[i] = (byte)Math.Max(a[i], b[i]);
        return dst;
    }

    /// <summary>
    /// Calcule un seuil adaptatif basé sur le 90e percentile des magnitudes non nulles.
    /// Permet d'ignorer automatiquement la texture de fond (grain de papier, etc.).
    /// </summary>
    private static int ComputeAdaptiveThreshold(float[] mag, int fallback)
    {
        // Collecter les magnitudes significatives
        int count = 0;
        for (int i = 0; i < mag.Length; i++)
            if (mag[i] > 0) count++;

        if (count == 0) return fallback;

        var nonZero = new float[count];
        int idx = 0;
        for (int i = 0; i < mag.Length; i++)
            if (mag[i] > 0) nonZero[idx++] = mag[i];

        Array.Sort(nonZero);
        // 85e percentile : équilibre entre supprimer la texture et garder les détails
        int p85 = (int)(nonZero[(int)(count * 0.85f)]);
        return Math.Clamp(p85, fallback, 255);
    }

    // ── Redimensionnement bilinéaire ──────────────────────────────────────────

    private static byte[] BilinearResize(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        int srcStride = srcW * 4;
        int dstStride = dstW * 4;
        var dst = new byte[dstStride * dstH];

        double scaleX = (double)srcW / dstW;
        double scaleY = (double)srcH / dstH;

        for (int dy = 0; dy < dstH; dy++)
        {
            double sy = (dy + 0.5) * scaleY - 0.5;
            int y0 = Math.Clamp((int)sy,  0, srcH - 1);
            int y1 = Math.Clamp(y0 + 1,   0, srcH - 1);
            double fy = Math.Clamp(sy - y0, 0, 1);

            for (int dx = 0; dx < dstW; dx++)
            {
                double sx = (dx + 0.5) * scaleX - 0.5;
                int x0 = Math.Clamp((int)sx,  0, srcW - 1);
                int x1 = Math.Clamp(x0 + 1,   0, srcW - 1);
                double fx = Math.Clamp(sx - x0, 0, 1);

                int o00 = y0 * srcStride + x0 * 4;
                int o10 = y0 * srcStride + x1 * 4;
                int o01 = y1 * srcStride + x0 * 4;
                int o11 = y1 * srcStride + x1 * 4;
                int dOff = dy * dstStride + dx * 4;

                for (int c = 0; c < 4; c++)
                {
                    double v = src[o00 + c] * (1 - fx) * (1 - fy)
                             + src[o10 + c] *      fx  * (1 - fy)
                             + src[o01 + c] * (1 - fx) *      fy
                             + src[o11 + c] *      fx  *      fy;
                    dst[dOff + c] = (byte)Math.Clamp((int)v, 0, 255);
                }
            }
        }
        return dst;
    }

    // ── Utilitaires Canny ─────────────────────────────────────────────────────

    private static byte[] ToGrayscale(byte[] src, int w, int h)
    {
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            byte b = src[i]; byte g = src[i + 1]; byte r = src[i + 2];
            byte l = (byte)(0.114 * b + 0.587 * g + 0.299 * r);
            dst[i] = dst[i + 1] = dst[i + 2] = l;
            dst[i + 3] = 255;
        }
        return dst;
    }

    private static readonly float[] _gaussKernel5 =
    [
        1/273f,  4/273f,  7/273f,  4/273f, 1/273f,
        4/273f, 16/273f, 26/273f, 16/273f, 4/273f,
        7/273f, 26/273f, 41/273f, 26/273f, 7/273f,
        4/273f, 16/273f, 26/273f, 16/273f, 4/273f,
        1/273f,  4/273f,  7/273f,  4/273f, 1/273f,
    ];

    private static byte[] GaussianBlur5x5(byte[] src, int w, int h)
    {
        int stride = w * 4;
        var dst = new byte[src.Length];
        for (int y = 2; y < h - 2; y++)
        {
            for (int x = 2; x < w - 2; x++)
            {
                float sum = 0;
                int ki = 0;
                for (int ky = -2; ky <= 2; ky++)
                    for (int kx = -2; kx <= 2; kx++)
                        sum += src[(y + ky) * stride + (x + kx) * 4] * _gaussKernel5[ki++];
                byte v   = (byte)Math.Clamp((int)sum, 0, 255);
                int  idx = y * stride + x * 4;
                dst[idx] = dst[idx + 1] = dst[idx + 2] = v;
                dst[idx + 3] = 255;
            }
        }
        return dst;
    }

    private static void SobelWithAngle(byte[] gray, int w, int h, out float[] mag, out float[] angle)
    {
        int stride = w * 4;
        int n = w * h;
        mag   = new float[n];
        angle = new float[n];

        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int g00 = gray[(y - 1) * stride + (x - 1) * 4];
                int g01 = gray[(y - 1) * stride +  x      * 4];
                int g02 = gray[(y - 1) * stride + (x + 1) * 4];
                int g10 = gray[ y      * stride + (x - 1) * 4];
                int g12 = gray[ y      * stride + (x + 1) * 4];
                int g20 = gray[(y + 1) * stride + (x - 1) * 4];
                int g21 = gray[(y + 1) * stride +  x      * 4];
                int g22 = gray[(y + 1) * stride + (x + 1) * 4];

                float gx = -g00 + g02 - 2 * g10 + 2 * g12 - g20 + g22;
                float gy = -g00 - 2 * g01 - g02 + g20 + 2 * g21 + g22;

                int idx = y * w + x;
                mag[idx]   = MathF.Sqrt(gx * gx + gy * gy);
                angle[idx] = MathF.Atan2(gy, gx);
            }
        }
    }

    private static float[] NonMaximumSuppression(float[] mag, float[] angle, int w, int h)
    {
        var nms = new float[mag.Length];
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int   idx = y * w + x;
                float m   = mag[idx];
                if (m == 0) continue;

                float a = angle[idx];
                if (a < 0) a += MathF.PI;
                float deg = a * (180f / MathF.PI);

                float m1, m2;
                if (deg < 22.5f || deg >= 157.5f)
                {
                    m1 = mag[y * w + (x - 1)];
                    m2 = mag[y * w + (x + 1)];
                }
                else if (deg < 67.5f)
                {
                    m1 = mag[(y - 1) * w + (x + 1)];
                    m2 = mag[(y + 1) * w + (x - 1)];
                }
                else if (deg < 112.5f)
                {
                    m1 = mag[(y - 1) * w + x];
                    m2 = mag[(y + 1) * w + x];
                }
                else
                {
                    m1 = mag[(y - 1) * w + (x - 1)];
                    m2 = mag[(y + 1) * w + (x + 1)];
                }

                if (m >= m1 && m >= m2)
                    nms[idx] = m;
            }
        }
        return nms;
    }

    private static byte[] HysteresisThreshold(float[] nms, int w, int h, int lowT, int highT)
    {
        int n = w * h;
        var strength = new byte[n];
        for (int i = 0; i < n; i++)
        {
            if      (nms[i] >= highT) strength[i] = 2;
            else if (nms[i] >= lowT)  strength[i] = 1;
        }

        var queue = new System.Collections.Generic.Queue<int>();
        for (int i = 0; i < n; i++)
            if (strength[i] == 2) queue.Enqueue(i);

        while (queue.Count > 0)
        {
            int pos = queue.Dequeue();
            int py  = pos / w;
            int px  = pos % w;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dy == 0 && dx == 0) continue;
                int ny = py + dy, nx = px + dx;
                if (ny < 0 || ny >= h || nx < 0 || nx >= w) continue;
                int ni = ny * w + nx;
                if (strength[ni] == 1) { strength[ni] = 2; queue.Enqueue(ni); }
            }
        }

        var dst = new byte[n * 4];
        for (int i = 0; i < n; i++)
            if (strength[i] == 2) dst[i * 4 + 3] = 255;
        return dst;
    }

    private static byte[] Dilate(byte[] src, int w, int h, int passes)
    {
        int stride = w * 4;
        var buf = (byte[])src.Clone();
        for (int p = 0; p < passes; p++)
        {
            var next = (byte[])buf.Clone();
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int idx = y * stride + x * 4;
                    if (buf[idx + 3] == 255) continue;
                    bool hasBlack = false;
                    for (int dy = -1; dy <= 1 && !hasBlack; dy++)
                        for (int dx = -1; dx <= 1 && !hasBlack; dx++)
                            if (buf[(y + dy) * stride + (x + dx) * 4 + 3] == 255) hasBlack = true;
                    if (hasBlack) next[idx + 3] = 255;
                }
            }
            buf = next;
        }
        return buf;
    }

    private static byte[] Denoise(byte[] src, int w, int h, int minIslandSize)
    {
        int stride  = w * 4;
        var dst     = (byte[])src.Clone();
        var visited = new bool[w * h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int vi = y * w + x;
                if (visited[vi]) continue;
                if (src[y * stride + x * 4 + 3] == 0) { visited[vi] = true; continue; }

                var island = new System.Collections.Generic.List<int>();
                var queue  = new System.Collections.Generic.Queue<int>();
                queue.Enqueue(vi);
                visited[vi] = true;
                while (queue.Count > 0)
                {
                    int pos = queue.Dequeue();
                    island.Add(pos);
                    int py = pos / w, px = pos % w;
                    foreach (var (nx, ny) in new[] { (px-1,py),(px+1,py),(px,py-1),(px,py+1) })
                    {
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        int nv = ny * w + nx;
                        if (visited[nv]) continue;
                        visited[nv] = true;
                        if (src[ny * stride + nx * 4 + 3] != 0) queue.Enqueue(nv);
                    }
                }

                if (island.Count < minIslandSize)
                {
                    foreach (int pos in island)
                    {
                        int py = pos / w, px = pos % w;
                        dst[py * stride + px * 4 + 3] = 0;
                    }
                }
            }
        }
        return dst;
    }
}
