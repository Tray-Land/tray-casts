using System.Buffers.Binary;

namespace TrayCasts.Tray;

/// <summary>
/// Draws the new-episode count onto the app icon: a pill in the bottom-right corner with a 1-9
/// count (or "9+") in a pixel font, cut out from the icon by a transparent ring so it reads on
/// any taskbar. Produces a single-image 32-bit .ico, with nothing beyond the BCL.
/// </summary>
public static class BadgeIcon
{
    /// <summary>Offset of the image (BITMAPINFOHEADER onward) in the .ico: ICONDIR plus one ICONDIRENTRY.</summary>
    public const int ImageOffset = 6 + 16;

    private const int GlyphWidth = 3;
    private const int GlyphHeight = 5;
    private const int BitmapInfoHeaderSize = 40;

    // 3x5 glyphs, one byte per row, the low three bits left to right.
    private static readonly byte[][] Digits =
    [
        [0b111, 0b101, 0b101, 0b101, 0b111],
        [0b010, 0b110, 0b010, 0b010, 0b111],
        [0b111, 0b001, 0b111, 0b100, 0b111],
        [0b111, 0b001, 0b111, 0b001, 0b111],
        [0b101, 0b101, 0b111, 0b001, 0b001],
        [0b111, 0b100, 0b111, 0b001, 0b111],
        [0b111, 0b100, 0b111, 0b101, 0b111],
        [0b111, 0b001, 0b001, 0b001, 0b001],
        [0b111, 0b101, 0b111, 0b101, 0b111],
        [0b111, 0b101, 0b111, 0b001, 0b111],
    ];

    private static readonly byte[] Plus = [0b000, 0b010, 0b111, 0b010, 0b000];

    /// <summary>The text the badge shows: the count up to 9, then "9+".</summary>
    public static string Label(int count) => count > 9 ? "9+" : Math.Max(count, 0).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Black or white, whichever reads better on <paramref name="background"/> (0xAARRGGBB).</summary>
    public static uint TextColorFor(uint background)
    {
        double r = (background >> 16) & 0xFF, g = (background >> 8) & 0xFF, b = background & 0xFF;
        double luminance = ((0.299 * r) + (0.587 * g) + (0.114 * b)) / 255;
        return luminance > 0.6 ? 0xFF000000 : 0xFFFFFFFF;
    }

    /// <param name="icon">The app icon, <paramref name="size"/> squared pixels as 0xAARRGGBB (straight alpha), row-major top-down.</param>
    /// <param name="size">Icon width and height in pixels (8-255).</param>
    /// <param name="count">New episodes; 1 or more.</param>
    /// <param name="badge">Badge color as 0xAARRGGBB.</param>
    public static byte[] Create(ReadOnlySpan<uint> icon, int size, int count, uint badge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 255);
        ArgumentOutOfRangeException.ThrowIfNotEqual(icon.Length, size * size, nameof(icon));

        byte[][] glyphs = [.. Label(count).Select(c => c == '+' ? Plus : Digits[c - '0'])];
        int scale = Math.Max(1, size / 16);
        int textWidth = ((glyphs.Length * (GlyphWidth + 1)) - 1) * scale;
        double height = (GlyphHeight + 2) * scale;
        double width = Math.Max(height, textWidth + (4 * scale));
        double gap = scale;

        // The pill sits flush with the bottom-right corner.
        double centerX = size - (width / 2);
        double centerY = size - (height / 2);
        uint[] pixels = icon.ToArray();
        uint text = TextColorFor(badge);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double distance = PillDistance(x + 0.5 - centerX, y + 0.5 - centerY, width / 2, height / 2);
                double cutout = Math.Clamp(gap + 0.5 - distance, 0, 1);
                double fill = Math.Clamp(0.5 - distance, 0, 1);
                int i = (y * size) + x;
                pixels[i] = Over(WithAlpha(pixels[i], 1 - cutout), WithAlpha(badge, fill));
            }
        }

        int left = (int)Math.Round(size - width + ((width - textWidth) / 2));
        int top = size - (int)height + scale;
        for (int g = 0; g < glyphs.Length; g++)
        {
            int glyphLeft = left + (g * (GlyphWidth + 1) * scale);
            for (int row = 0; row < GlyphHeight; row++)
            {
                for (int column = 0; column < GlyphWidth; column++)
                {
                    if ((glyphs[g][row] & (1 << (GlyphWidth - 1 - column))) != 0)
                    {
                        FillBlock(pixels, size, glyphLeft + (column * scale), top + (row * scale), scale, text);
                    }
                }
            }
        }

        return Encode(pixels, size);
    }

    /// <summary>Signed distance from a point (relative to the center) to a pill with the given half extents.</summary>
    private static double PillDistance(double px, double py, double halfWidth, double halfHeight)
    {
        double radius = halfHeight;
        double dx = Math.Max(0, Math.Abs(px) - (halfWidth - radius));
        double dy = Math.Max(0, Math.Abs(py) - (halfHeight - radius));
        return Math.Sqrt((dx * dx) + (dy * dy)) - radius;
    }

    private static void FillBlock(uint[] pixels, int size, int left, int top, int scale, uint color)
    {
        for (int y = Math.Max(0, top); y < Math.Min(size, top + scale); y++)
        {
            for (int x = Math.Max(0, left); x < Math.Min(size, left + scale); x++)
            {
                pixels[(y * size) + x] = color;
            }
        }
    }

    private static uint WithAlpha(uint color, double factor) =>
        ((uint)Math.Round((color >> 24) * factor) << 24) | (color & 0x00FFFFFF);

    /// <summary>Straight-alpha "source over destination".</summary>
    private static uint Over(uint destination, uint source)
    {
        double sa = (source >> 24) / 255.0;
        double da = (destination >> 24) / 255.0;
        double outA = sa + (da * (1 - sa));
        if (outA <= 0)
        {
            return 0;
        }

        uint result = (uint)Math.Round(outA * 255) << 24;
        for (int shift = 0; shift < 24; shift += 8)
        {
            double s = (source >> shift) & 0xFF;
            double d = (destination >> shift) & 0xFF;
            result |= (uint)Math.Round(((s * sa) + (d * da * (1 - sa))) / outA) << shift;
        }

        return result;
    }

    /// <summary>ICONDIR + one ICONDIRENTRY + a 32bpp DIB (bottom-up BGRA, then an all-zero AND mask).</summary>
    private static byte[] Encode(uint[] pixels, int size)
    {
        int pixelBytes = size * size * 4;
        int maskBytes = ((size + 31) / 32) * 4 * size;
        int imageBytes = BitmapInfoHeaderSize + pixelBytes + maskBytes;
        byte[] ico = new byte[ImageOffset + imageBytes];
        Span<byte> span = ico;

        // ICONDIR: reserved, type 1 (icon), one image.
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1);

        // ICONDIRENTRY: width, height, no palette, reserved, planes, bit count, size, offset.
        span[6] = (byte)size;
        span[7] = (byte)size;
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], imageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], ImageOffset);

        // BITMAPINFOHEADER: the height covers the color bitmap and the mask.
        Span<byte> header = span[ImageOffset..];
        BinaryPrimitives.WriteInt32LittleEndian(header, BitmapInfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], size);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], size * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], pixelBytes + maskBytes);

        Span<byte> bits = header[BitmapInfoHeaderSize..];
        for (int y = 0; y < size; y++)
        {
            Span<byte> row = bits[((size - 1 - y) * size * 4)..];
            for (int x = 0; x < size; x++)
            {
                // 0xAARRGGBB little-endian is exactly the DIB's B, G, R, A byte order.
                BinaryPrimitives.WriteUInt32LittleEndian(row[(x * 4)..], pixels[(y * size) + x]);
            }
        }

        return ico;
    }
}
