using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Perception;

/// <summary>One captured screen image, ready to hand to a vision model.</summary>
public sealed class ScreenFrame
{
    /// <summary>data:image/jpeg;base64,... ready for the OpenAI vision payload.</summary>
    public required string DataUri { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>64-bit difference hash of the downscaled frame, used to skip unchanged screens.</summary>
    public required ulong PerceptualHash { get; init; }

    /// <summary>Window title at capture time, so she knows what she is looking at.</summary>
    public string WindowTitle { get; init; } = string.Empty;

    /// <summary>Rough byte size of the encoded image, shown in diagnostics.</summary>
    public int EncodedByteCount { get; init; }
}

/// <summary>
/// Takes screenshots the cheap way and describes how different two frames are.
///
/// The strategy that keeps an 8 GB card comfortable:
///   1. capture once with GDI (about a millisecond);
///   2. shrink to a 9x8 thumbnail and compute a 64-bit difference hash (dHash);
///   3. skip everything if the hash barely changed — a static screen costs nothing;
///   4. otherwise downscale the real frame to the vision sweet spot (896 px) and
///      encode a small JPEG.
///
/// A 4B vision model then receives roughly the same token count as a short message,
/// while still being able to read interface text.
/// </summary>
public static class ScreenCapture
{
    /// <summary>Captures the primary screen, or one rectangle of it.</summary>
    /// <param name="maxDimension">Longest edge after downscaling. 896 is the default.</param>
    /// <param name="jpegQuality">1 to 100. 72 keeps text readable and files tiny.</param>
    /// <param name="captureBounds">Optional rectangle; null means the whole primary screen.</param>
    /// <param name="windowTitle">Recorded with the frame for later explanation.</param>
    public static ScreenFrame? Capture(int maxDimension, int jpegQuality, Rectangle? captureBounds = null,
        string windowTitle = "")
    {
        try
        {
            Rectangle bounds = captureBounds ?? ReadPrimaryScreenBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return null;
            }

            using Bitmap fullSizeFrame = new(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(fullSizeFrame))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            ulong hash = ComputeDifferenceHash(fullSizeFrame);

            using Bitmap scaledFrame = Downscale(fullSizeFrame, Math.Clamp(maxDimension, 256, 2048));
            using MemoryStream imageStream = new();
            SaveJpeg(scaledFrame, imageStream, Math.Clamp(jpegQuality, 40, 95));

            byte[] imageBytes = imageStream.ToArray();
            string dataUri = "data:image/jpeg;base64," + Convert.ToBase64String(imageBytes);

            return new ScreenFrame
            {
                DataUri = dataUri,
                Width = scaledFrame.Width,
                Height = scaledFrame.Height,
                CapturedAt = DateTimeOffset.Now,
                PerceptualHash = hash,
                WindowTitle = windowTitle,
                EncodedByteCount = imageBytes.Length
            };
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Senses", $"Screen capture failed: {exception.Message}");
            return null;
        }
    }

    /// <summary>Number of differing bits between two perceptual hashes (0 to 64).</summary>
    public static int CountChangedBits(ulong firstHash, ulong secondHash) =>
        BitOperations.PopCount(firstHash ^ secondHash);

    /// <summary>Screen rectangle of the primary display.</summary>
    public static Rectangle ReadPrimaryScreenBounds()
    {
        (int width, int height) = WindowsNative.ReadPrimaryScreenSize();
        return new Rectangle(0, 0, Math.Max(width, 640), Math.Max(height, 480));
    }

    /// <summary>
    /// 64-bit difference hash: resize to 9x8 greyscale and record which neighbour is
    /// brighter. It changes when the picture changes, ignores tiny colour shifts,
    /// and costs microseconds.
    /// </summary>
    private static ulong ComputeDifferenceHash(Bitmap source)
    {
        const int columns = 9;
        const int rows = 8;

        using Bitmap tiny = new(columns, rows, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(tiny))
        {
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            graphics.DrawImage(source, 0, 0, columns, rows);
        }

        ulong hash = 0;
        int bitIndex = 0;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns - 1; column++)
            {
                Color leftPixel = tiny.GetPixel(column, row);
                Color rightPixel = tiny.GetPixel(column + 1, row);
                int leftBrightness = leftPixel.R + leftPixel.G + leftPixel.B;
                int rightBrightness = rightPixel.R + rightPixel.G + rightPixel.B;

                if (leftBrightness > rightBrightness)
                {
                    hash |= 1UL << bitIndex;
                }

                bitIndex++;
            }
        }

        return hash;
    }

    private static Bitmap Downscale(Bitmap source, int maxDimension)
    {
        int longestEdge = Math.Max(source.Width, source.Height);
        if (longestEdge <= maxDimension)
        {
            return new Bitmap(source);
        }

        double scale = (double)maxDimension / longestEdge;
        int targetWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        int targetHeight = Math.Max(1, (int)Math.Round(source.Height * scale));

        Bitmap scaled = new(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
        using Graphics graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, targetWidth, targetHeight);
        return scaled;
    }

    private static void SaveJpeg(Bitmap frame, Stream destination, int quality)
    {
        ImageCodecInfo? jpegEncoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(encoder => encoder.FormatID == ImageFormat.Jpeg.Guid);

        if (jpegEncoder is null)
        {
            frame.Save(destination, ImageFormat.Jpeg);
            return;
        }

        using EncoderParameters parameters = new(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        frame.Save(destination, jpegEncoder, parameters);
    }
}
