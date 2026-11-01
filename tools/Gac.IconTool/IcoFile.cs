using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;

namespace Gac.IconTool;

/// <summary>One image inside an .ico file. <see cref="Data"/> is either a PNG stream or a DIB (BITMAPINFOHEADER + XOR + AND).</summary>
public sealed record IcoFrame(int Width, int Height, byte ColorCount, ushort Planes, ushort BitCount, byte[] Data)
{
    public bool IsPng => Data.Length >= 8 && Data[0] == 0x89 && Data[1] == 0x50 && Data[2] == 0x4E && Data[3] == 0x47;
}

/// <summary>
/// Reads and writes the ICO container format (ICONDIR + ICONDIRENTRY[] + image data). Layout per Microsoft's
/// "Icons" documentation: 6-byte header (reserved 0, type 1, count), 16-byte entries (width, height — 0 means 256 —,
/// colour count, reserved, planes, bit count, bytes in resource, image offset).
/// </summary>
public static class IcoFile
{
    public static byte[] Write(IReadOnlyList<IcoFrame> frames)
    {
        using var ms = new MemoryStream();
        var header = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)frames.Count);
        ms.Write(header);
        var offset = 6 + 16 * frames.Count;
        foreach (var f in frames)
        {
            var e = new byte[16];
            e[0] = (byte)(f.Width >= 256 ? 0 : f.Width);
            e[1] = (byte)(f.Height >= 256 ? 0 : f.Height);
            e[2] = f.ColorCount;
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(4), f.Planes);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(6), f.BitCount);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), (uint)f.Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), (uint)offset);
            ms.Write(e);
            offset += f.Data.Length;
        }
        foreach (var f in frames) ms.Write(f.Data);
        return ms.ToArray();
    }

    public static IReadOnlyList<IcoFrame> Read(byte[] ico)
    {
        if (ico.Length < 6) throw new InvalidDataException("ICO file too short.");
        var span = ico.AsSpan();
        if (BinaryPrimitives.ReadUInt16LittleEndian(span) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(span[2..]) != 1)
            throw new InvalidDataException("Not an ICO file (bad header).");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        if (count == 0 || ico.Length < 6 + 16 * count) throw new InvalidDataException("ICO file has no or truncated entries.");
        var frames = new List<IcoFrame>(count);
        for (var i = 0; i < count; i++)
        {
            var e = span.Slice(6 + 16 * i, 16);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (off < 0 || size <= 0 || (long)off + size > ico.Length) throw new InvalidDataException($"ICO entry {i} points outside the file.");
            frames.Add(new IcoFrame(e[0] == 0 ? 256 : e[0], e[1] == 0 ? 256 : e[1], e[2], BinaryPrimitives.ReadUInt16LittleEndian(e[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(e[6..]), span.Slice(off, size).ToArray()));
        }
        return frames;
    }

    /// <summary>Decodes one frame to a 32-bit ARGB bitmap (PNG via GDI+, 32-bit DIB by hand, other DIBs via <see cref="Icon"/>).</summary>
    public static Bitmap Decode(IcoFrame f)
    {
        if (f.IsPng)
        {
            using var ms = new MemoryStream(f.Data);
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        var d = f.Data.AsSpan();
        var w = BinaryPrimitives.ReadInt32LittleEndian(d[4..]);
        var h2 = BinaryPrimitives.ReadInt32LittleEndian(d[8..]);
        var bpp = BinaryPrimitives.ReadUInt16LittleEndian(d[14..]);
        var hdr = (int)BinaryPrimitives.ReadUInt32LittleEndian(d);
        var h = Math.Abs(h2) / 2;
        if (bpp == 32)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var p = hdr + ((h - 1 - y) * w + x) * 4; // bottom-up BGRA
                    bmp.SetPixel(x, y, Color.FromArgb(d[p + 3], d[p + 2], d[p + 1], d[p]));
                }
            return bmp;
        }
        using var single = new MemoryStream(Write([f]));
        using var icon = new Icon(single);
        return icon.ToBitmap();
    }
}
