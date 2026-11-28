using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace REM.Services;

/// <summary>
/// Extracts a subtle dark-theme ambient radial gradient brush from album cover bytes using a 32x32 decode.
/// </summary>
public static class DominantColorExtractor
{
    public static IBrush CreateAmbientGlowBrush(byte[] imageBytes) =>
        CreateAmbientGlowBrush(ExtractDominantColor(imageBytes));

    public static IBrush CreateAmbientGlowBrush(Color color)
    {
        var center = Color.FromArgb(64, color.R, color.G, color.B);
        var mid = Color.FromArgb(24, color.R, color.G, color.B);
        var edge = Color.FromArgb(0, color.R, color.G, color.B);

        return new RadialGradientBrush
        {
            Center = new RelativePoint(0.28, 0.25, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.28, 0.25, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.85, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.85, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(center, 0.0),
                new GradientStop(mid, 0.48),
                new GradientStop(edge, 1.0)
            }
        };
    }

    public static Color ExtractDominantColor(byte[] imageBytes)
    {
        var fallback = Color.FromRgb(29, 185, 84);
        if (imageBytes.Length == 0)
        {
            return fallback;
        }

        try
        {
            using var ms = new MemoryStream(imageBytes, writable: false);
            using var tiny = Bitmap.DecodeToWidth(ms, 32);
            var w = tiny.PixelSize.Width;
            var h = tiny.PixelSize.Height;
            if (w <= 0 || h <= 0)
            {
                return fallback;
            }

            var stride = w * 4;
            var pixels = new byte[stride * h];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                tiny.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), pixels.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            var isRgba = tiny.Format == PixelFormats.Rgba8888;
            double rSum = 0;
            double gSum = 0;
            double bSum = 0;
            double weightSum = 0;

            for (var i = 0; i <= pixels.Length - 4; i += 4)
            {
                byte r = isRgba ? pixels[i] : pixels[i + 2];
                byte g = pixels[i + 1];
                byte b = isRgba ? pixels[i + 2] : pixels[i];
                byte a = pixels[i + 3];
                if (a < 128)
                {
                    continue;
                }

                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var luma = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                if (luma < 0.08 || luma > 0.92)
                {
                    continue;
                }

                var sat = max > 0 ? (max - min) / (double)max : 0.0;
                var weight = 0.25 + sat * 2.5;
                rSum += r * weight;
                gSum += g * weight;
                bSum += b * weight;
                weightSum += weight;
            }

            if (weightSum <= 0.001)
            {
                return fallback;
            }

            var outR = (byte)Math.Clamp((int)Math.Round(rSum / weightSum), 24, 235);
            var outG = (byte)Math.Clamp((int)Math.Round(gSum / weightSum), 24, 235);
            var outB = (byte)Math.Clamp((int)Math.Round(bSum / weightSum), 24, 235);
            return Color.FromRgb(outR, outG, outB);
        }
        catch
        {
            return fallback;
        }
    }
}

