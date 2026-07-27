using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: VeXArk.BrandAssets <project-root>");
    return 2;
}

var projectRoot = Path.GetFullPath(args[0]);
var desktopAssets = Path.Combine(
    projectRoot,
    "src",
    "PhoneBackup.Desktop",
    "Assets");
Directory.CreateDirectory(desktopAssets);

var png512 = RenderIcon(512);
var png256 = RenderIcon(256);
await File.WriteAllBytesAsync(Path.Combine(desktopAssets, "vexark-512.png"), png512);
await WritePngIconAsync(Path.Combine(desktopAssets, "vexark.ico"), png256);
return 0;

static byte[] RenderIcon(int size)
{
    var visual = new DrawingVisual();
    using (var context = visual.RenderOpen())
    {
        context.DrawRoundedRectangle(
            Brushes.Black,
            null,
            new Rect(0, 0, size, size),
            size * 0.21875,
            size * 0.21875);

        var mark = new GeometryGroup { FillRule = FillRule.EvenOdd };
        mark.Children.Add(Geometry.Parse(
            "M96,128 L256,36 L416,128 L256,304 Z " +
            "M154,145 L256,86 L358,145 L256,246 Z"));
        mark.Children.Add(Geometry.Parse(
            "M92,194 L132,158 L420,382 L380,426 Z"));
        mark.Children.Add(Geometry.Parse(
            "M420,194 L380,158 L92,382 L132,426 Z"));
        mark.Transform = new ScaleTransform(size / 512d, size / 512d);
        context.DrawGeometry(Brushes.White, null, mark);
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var output = new MemoryStream();
    encoder.Save(output);
    return output.ToArray();
}

static async Task WritePngIconAsync(string path, byte[] png)
{
    await using var output = new FileStream(
        path,
        FileMode.Create,
        FileAccess.Write,
        FileShare.None);
    using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)1);
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(png.Length);
    writer.Write(22);
    writer.Write(png);
    await output.FlushAsync();
}
