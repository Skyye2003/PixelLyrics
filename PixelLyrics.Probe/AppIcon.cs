using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixelLyrics.Probe;

internal static class AppIcon
{
    public static DrawingImage CreateImage()
    {
        var group = new DrawingGroup();
        void Add(string color, string path) => group.Children.Add(new GeometryDrawing(
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), null, Geometry.Parse(path)));
        Add("#7775FF", "M32 16h192v16h16v176h-16v16H32v-16H16V32h16z");
        Add("#121A36", "M40 32h176v16h8v144h-16v16H48v-16H32V48h8z");
        Add("#313B67", "M64 224h128v16H64z");
        Add("#AAA5FF", "M48 64h80v16H48z");
        Add("#7775FF", "M48 96h64v16H48zM48 128h48v16H48z");
        Add("#38E5CB", "M144 64h48v16h-32v72h-16zM176 80h16v88h-16zM128 144h16v16h16v16h-32v-16h-16v-16zM160 160h16v16h16v16h-32v-16h-16v-16zM48 176h16v16H48z");
        Add("#536392", "M80 176h32v16H80z");
        group.Freeze();
        return new DrawingImage(group);
    }

    public static System.Drawing.Icon CreateTrayIcon()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawImage(CreateImage(), new System.Windows.Rect(0, 0, 32, 32));
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var pixels = new MemoryStream();
        png.Save(pixels);
        using var iconData = new MemoryStream();
        using (var writer = new BinaryWriter(iconData, System.Text.Encoding.UTF8, true))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write((byte)32); writer.Write((byte)32); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write((uint)pixels.Length); writer.Write((uint)22);
            writer.Write(pixels.ToArray());
        }
        iconData.Position = 0;
        using var icon = new System.Drawing.Icon(iconData);
        return (System.Drawing.Icon)icon.Clone();
    }
}
