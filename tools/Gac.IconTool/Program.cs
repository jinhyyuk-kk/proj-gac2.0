using System.Drawing;
using System.Drawing.Imaging;
using Gac.IconTool;

// Gac.IconTool extract <setup.exe> <out.ico>   — full icon group from a PE file (+ <out>-256.png of the largest frame)
// Gac.IconTool build   <image>     <out.ico>   — multi-size icon from a transparent image (+ <out>-256.png)
if (args.Length != 3 || args[0] is not ("extract" or "build"))
{
    Console.Error.WriteLine("Usage: Gac.IconTool extract <setup.exe> <out.ico> | build <image> <out.ico>");
    return 2;
}

var (verb, input, output) = (args[0], args[1], args[2]);
var png = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output) + "-256.png");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
try
{
    if (verb == "extract")
    {
        var frames = IconExtractor.ExtractMainIcon(input);
        File.WriteAllBytes(output, IcoFile.Write(frames));
        var largest = frames.OrderByDescending(f => f.Width).ThenByDescending(f => f.BitCount).First();
        if (largest.IsPng) File.WriteAllBytes(png, largest.Data);
        else using (var bmp = IcoFile.Decode(largest)) bmp.Save(png, ImageFormat.Png);
        Console.WriteLine($"{frames.Count} frames: {string.Join(", ", frames.Select(f => $"{f.Width}x{f.Height}@{f.BitCount}"))}");
    }
    else
    {
        using var src = new Bitmap(input);
        var (ico, largest) = IconBuilder.Build(src);
        using (largest) largest.Save(png, ImageFormat.Png);
        File.WriteAllBytes(output, ico);
        Console.WriteLine($"{IconBuilder.Sizes.Length} frames: {string.Join(", ", IconBuilder.Sizes)}");
    }
    Console.WriteLine($"Wrote {output} and {png}");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine("Failed: " + e.Message);
    return 1;
}
