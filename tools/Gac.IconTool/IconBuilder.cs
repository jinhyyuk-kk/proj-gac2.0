using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Gac.IconTool;

/// <summary>
/// Builds a multi-size .ico from a transparent image (CHANGE-002 §1.2): trim fully transparent borders, centre on a
/// transparent square (no stretching), downscale with premultiplied alpha + high-quality bicubic (no dark fringe),
/// 256 px frame PNG-compressed, smaller frames as 32-bit BGRA DIBs with an AND mask.
/// </summary>
public static class IconBuilder
{
    public static readonly int[] Sizes = [16, 24, 32, 48, 64, 128, 256];

    public static (byte[] Ico, Bitmap Largest) Build(Bitmap source)
    {
        using var square = SquareCanvas(source);
        var frames = new List<IcoFrame>();
        Bitmap? largest = null;
        foreach (var size in Sizes)
        {
            var bmp = Resize(square, size);
            if (size == 256)
            {
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                frames.Add(new IcoFrame(256, 256, 0, 1, 32, ms.ToArray()));
                largest = bmp;
            }
            else
            {
                frames.Add(new IcoFrame(size, size, 0, 1, 32, ToDib(bmp)));
                bmp.Dispose();
            }
        }
        return (IcoFile.Write(frames), largest!);
    }

    /// <summary>Crops to the bounding box of non-transparent pixels and centres it on a transparent square.</summary>
    public static Bitmap SquareCanvas(Bitmap source)
    {
        using var argb = To32(source, PixelFormat.Format32bppArgb);
        var px = Pixels(argb);
        int w = argb.Width, h = argb.Height, minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if ((px[y * w + x] >>> 24) != 0) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        if (maxX < 0) { minX = minY = 0; maxX = w - 1; maxY = h - 1; } // fully transparent: keep as is
        int cw = maxX - minX + 1, ch = maxY - minY + 1, side = Math.Max(cw, ch);
        var canvas = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        var dst = new int[side * side];
        int ox = (side - cw) / 2, oy = (side - ch) / 2;
        for (var y = 0; y < ch; y++)
            Array.Copy(px, (minY + y) * w + minX, dst, (oy + y) * side + ox, cw);
        SetPixels(canvas, dst);
        return canvas;
    }

    /// <summary>Premultiplied-alpha downscale: halve with bicubic until within 2x, then a final high-quality bicubic pass.</summary>
    public static Bitmap Resize(Bitmap square, int size)
    {
        var current = To32(square, PixelFormat.Format32bppPArgb);
        while (current.Width / 2 >= size * 2)
        {
            var next = Draw(current, current.Width / 2);
            current.Dispose();
            current = next;
        }
        var final = Draw(current, size);
        current.Dispose();
        var result = To32(final, PixelFormat.Format32bppArgb); // un-premultiply for storage
        final.Dispose();
        return result;
    }

    private static Bitmap Draw(Bitmap src, int size)
    {
        var dst = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(dst);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY); // no darkening from sampling outside the image
        g.DrawImage(src, new Rectangle(0, 0, size, size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        return dst;
    }

    /// <summary>BITMAPINFOHEADER (height doubled) + bottom-up BGRA + 1-bpp AND mask (1 = transparent), rows padded to 4 bytes.</summary>
    public static byte[] ToDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var px = Pixels(bmp);
        var maskStride = ((w + 31) / 32) * 4;
        var data = new byte[40 + w * h * 4 + maskStride * h];
        var d = data.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(d, 40);
        BinaryPrimitives.WriteInt32LittleEndian(d[4..], w);
        BinaryPrimitives.WriteInt32LittleEndian(d[8..], h * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(d[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(d[14..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(d[20..], (uint)(w * h * 4 + maskStride * h));
        var mask = 40 + w * h * 4;
        for (var y = 0; y < h; y++)
        {
            var row = h - 1 - y; // bottom-up
            for (var x = 0; x < w; x++)
            {
                var argb = px[y * w + x];
                BinaryPrimitives.WriteInt32LittleEndian(d[(40 + (row * w + x) * 4)..], argb); // little-endian ARGB int = B,G,R,A bytes
                if ((argb >>> 24) == 0) data[mask + row * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }
        return data;
    }

    private static Bitmap To32(Bitmap src, PixelFormat format)
    {
        var dst = new Bitmap(src.Width, src.Height, format);
        using var g = Graphics.FromImage(dst);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
        return dst;
    }

    public static int[] Pixels(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new int[bmp.Width * bmp.Height];
            for (var y = 0; y < bmp.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * bmp.Width, bmp.Width);
            return px;
        }
        finally { bmp.UnlockBits(data); }
    }

    private static void SetPixels(Bitmap bmp, int[] px)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (var y = 0; y < bmp.Height; y++) Marshal.Copy(px, y * bmp.Width, data.Scan0 + y * data.Stride, bmp.Width); }
        finally { bmp.UnlockBits(data); }
    }
}
