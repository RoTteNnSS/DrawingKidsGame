using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ColorKids.Services;

/// <summary>Handles saving the composite bitmap to disk (PNG, JPEG, BMP, SVG) and printing.</summary>
internal static class ExportService
{
    /// <summary>Saves <paramref name="bitmap"/> to <paramref name="path"/> using the given <paramref name="format"/> ("png", "jpeg", "bmp").</summary>
    internal static void SaveBitmap(WriteableBitmap bitmap, string path, string format)
    {
        BitmapEncoder encoder = format.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => new JpegBitmapEncoder { QualityLevel = 95 },
            "bmp"           => new BmpBitmapEncoder(),
            _               => new PngBitmapEncoder()
        };

        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// Saves a minimal SVG representation of <paramref name="bitmap"/> (embedded PNG data URI).
    /// The SVG wraps the raster image so it can be opened in any SVG viewer or editor.
    /// </summary>
    internal static void SaveSvg(WriteableBitmap bitmap, string path)
    {
        // Encode bitmap to PNG in memory, then base64-embed in the SVG
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));

        using var ms = new MemoryStream();
        png.Save(ms);
        string b64 = Convert.ToBase64String(ms.ToArray());

        int w = bitmap.PixelWidth;
        int h = bitmap.PixelHeight;

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\"");
        sb.AppendLine($"     xmlns:xlink=\"http://www.w3.org/1999/xlink\"");
        sb.AppendLine($"     width=\"{w}\" height=\"{h}\"");
        sb.AppendLine($"     viewBox=\"0 0 {w} {h}\">");
        sb.AppendLine($"  <image width=\"{w}\" height=\"{h}\" x=\"0\" y=\"0\"");
        sb.AppendLine($"         xlink:href=\"data:image/png;base64,{b64}\"/>");
        sb.AppendLine("</svg>");

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>Sends <paramref name="bitmap"/> to the printer via the given <paramref name="dialog"/>.</summary>
    internal static void Print(WriteableBitmap bitmap, PrintDialog dialog)
    {
        double pageW = dialog.PrintableAreaWidth;
        double pageH = dialog.PrintableAreaHeight;

        // Scale to fit the page while keeping aspect ratio
        double bmpW = bitmap.PixelWidth;
        double bmpH = bitmap.PixelHeight;
        double scale = Math.Min(pageW / bmpW, pageH / bmpH);
        double drawW = bmpW * scale;
        double drawH = bmpH * scale;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(bitmap,
                new Rect((pageW - drawW) / 2, (pageH - drawH) / 2, drawW, drawH));
        }

        dialog.PrintVisual(visual, "ColorKids — dessin");
    }
}
