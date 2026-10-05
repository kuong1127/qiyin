using System.IO;
using System.Windows.Media.Imaging;

namespace TaskbarMediaBar.Services;

public static class CoverImage
{
    // 播控封面只占几十像素；限制长边同时覆盖高 DPI，避免解码整张大图。
    public static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var metadata = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        int width = metadata.PixelWidth, height = metadata.PixelHeight;
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (Math.Max(width, height) > 192)
        {
            if (width >= height) bitmap.DecodePixelWidth = 192;
            else bitmap.DecodePixelHeight = 192;
        }
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
