using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Gac.IconTool;

namespace Gac.Icons.Tests;

public class IconToolTests
{
    internal static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Gac.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Theory]
    [InlineData("gac.ico")]
    [InlineData("gac-admin.ico")]
    public void Committed_icons_exist_and_parse(string name)
    {
        var frames = IcoFile.Read(File.ReadAllBytes(Path.Combine(RepoRoot(), "assets", "icons", name)));
        Assert.Contains(frames, f => f.Width == 256);
        Assert.Contains(frames, f => f.Width == 16);
        foreach (var f in frames) using (var bmp = IcoFile.Decode(f)) Assert.Equal(f.Width, bmp.Width);
    }

    [Fact]
    public void Gac_icon_has_all_seven_sizes_with_transparent_corners()
    {
        var frames = IcoFile.Read(File.ReadAllBytes(Path.Combine(RepoRoot(), "assets", "icons", "gac.ico")));
        Assert.Equal(IconBuilder.Sizes, frames.Select(f => f.Width).Order());
        foreach (var f in frames)
        {
            Assert.Equal(f.Width == 256, f.IsPng); // 256 PNG, smaller ones BGRA DIBs
            using var bmp = IcoFile.Decode(f);
            Assert.Equal(0, bmp.GetPixel(0, 0).A);
            Assert.Equal(0, bmp.GetPixel(bmp.Width - 1, bmp.Height - 1).A);
        }
    }

    [Fact]
    public void Build_from_non_square_transparent_png_gives_square_frames_without_dark_fringe()
    {
        // 300x120 transparent canvas (transparent BLACK, the worst case for fringes) with a solid red rounded shape.
        using var src = new Bitmap(300, 120, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(src))
        {
            g.Clear(Color.FromArgb(0, 0, 0, 0));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Color.FromArgb(255, 255, 0, 0));
            g.FillEllipse(b, 20, 10, 260, 100);
        }
        var (ico, largest) = IconBuilder.Build(src);
        largest.Dispose();
        var frames = IcoFile.Read(ico);
        Assert.Equal(IconBuilder.Sizes, frames.Select(f => f.Width));
        foreach (var f in frames)
        {
            using var bmp = IcoFile.Decode(f);
            Assert.Equal(bmp.Width, bmp.Height);
            Assert.Equal(0, bmp.GetPixel(0, 0).A);
            Assert.Equal(0, bmp.GetPixel(bmp.Width - 1, 0).A);
            for (var y = 0; y < bmp.Height; y++)
                for (var x = 0; x < bmp.Width; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.A >= 8) Assert.True(p.R >= 200, $"dark fringe at {f.Width}px ({x},{y}): {p}");
                }
        }
    }

    [Fact]
    public void Extract_rebuilds_every_frame_from_an_exe()
    {
        // Make a sample exe with a known multi-size icon group, then extract it back.
        var (ico, largest) = IconBuilder.Build(SampleImage());
        largest.Dispose();
        var expected = IcoFile.Read(ico);
        var dir = Path.Combine(Path.GetTempPath(), "gac-icon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "sample.exe");
        try
        {
            File.Copy(Environment.ProcessPath!, exe);
            Inject(exe, expected);
            var actual = IconExtractor.ExtractMainIcon(exe);
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Width, actual[i].Width);
                Assert.Equal(expected[i].BitCount, actual[i].BitCount);
                Assert.Equal(expected[i].Data, actual[i].Data);
            }
            Assert.Equal(expected.Count, IcoFile.Read(IcoFile.Write(actual)).Count);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static Bitmap SampleImage()
    {
        var b = new Bitmap(64, 40, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.Clear(Color.Transparent);
        g.FillRectangle(Brushes.Blue, 8, 4, 48, 32);
        return b;
    }

    /// <summary>Writes RT_ICON + a named RT_GROUP_ICON (named groups enumerate before numbered ones, so it is "first").</summary>
    private static void Inject(string exe, IReadOnlyList<IcoFrame> frames)
    {
        var h = BeginUpdateResourceW(exe, false);
        Assert.NotEqual(IntPtr.Zero, h);
        var group = new byte[6 + 14 * frames.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(4), (ushort)frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            var id = (ushort)(500 + i);
            Assert.True(UpdateResourceW(h, 3, id, 0, f.Data, (uint)f.Data.Length));
            var e = group.AsSpan(6 + 14 * i);
            e[0] = (byte)(f.Width >= 256 ? 0 : f.Width);
            e[1] = (byte)(f.Height >= 256 ? 0 : f.Height);
            BinaryPrimitives.WriteUInt16LittleEndian(e[4..], f.Planes);
            BinaryPrimitives.WriteUInt16LittleEndian(e[6..], f.BitCount);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)f.Data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(e[12..], id);
        }
        var name = Marshal.StringToHGlobalUni("AAAGACTEST");
        try { Assert.True(UpdateResourceNamed(h, 14, name, 0, group, (uint)group.Length)); }
        finally { Marshal.FreeHGlobal(name); }
        Assert.True(EndUpdateResourceW(h, false));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BeginUpdateResourceW(string file, bool deleteExisting);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateResourceW(IntPtr h, IntPtr type, IntPtr name, ushort lang, byte[] data, uint size);

    [DllImport("kernel32.dll", EntryPoint = "UpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateResourceNamed(IntPtr h, IntPtr type, IntPtr name, ushort lang, byte[] data, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EndUpdateResourceW(IntPtr h, bool discard);
}
