using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MySnip.App.Services;

internal sealed record StickerContent(BitmapSource[] Frames, int[] Delays, byte[]? OriginalGif = null, string? Color = null)
{
    public StickerContent(BitmapSource source) : this([source], [100]) { }

    public static StickerContent Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, Path.GetExtension(path));
    }

    public static StickerContent Load(Stream stream, string extension)
    {
        if (extension.Equals(".tga", StringComparison.OrdinalIgnoreCase))
        {
            using var targa = new MemoryStream();
            stream.CopyTo(targa);
            return new StickerContent(LoadTarga(targa.ToArray()));
        }
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder is not GifBitmapDecoder || decoder.Frames.Count < 2)
        {
            var source = decoder.Frames[0];
            source.Freeze();
            return new StickerContent(source);
        }
        stream.Position = 0;
        using var gif = new MemoryStream();
        stream.CopyTo(gif);
        return FromGif(gif.ToArray());
    }

    public static StickerContent FromGif(byte[] data)
    {
        using var stream = new MemoryStream(data, false);
        using var gif = System.Drawing.Image.FromStream(stream);
        var dimension = System.Drawing.Imaging.FrameDimension.Time;
        var count = gif.GetFrameCount(dimension);
        if ((long)gif.Width * gif.Height * count > 64_000_000)
            throw new NotSupportedException("GIF 的影格總像素過多。");
        var timing = gif.PropertyIdList.Contains(0x5100) ? gif.GetPropertyItem(0x5100)?.Value : null;
        var frames = new BitmapSource[count];
        var delays = new int[count];
        for (var i = 0; i < count; i++)
        {
            gif.SelectActiveFrame(dimension, i);
            using var png = new MemoryStream();
            gif.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            frames[i] = BitmapFactory.FromPng(png.ToArray());
            delays[i] = timing is not null && timing.Length >= (i + 1) * 4
                ? (int)Math.Clamp((long)BitConverter.ToUInt32(timing, i * 4) * 10, 20, 60_000) : 100;
        }
        return new StickerContent(frames, delays, data);
    }

    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tga" or ".tif" or ".tiff" or ".ico" or ".webp";

    private static BitmapSource LoadTarga(byte[] data)
    {
        if (data.Length < 18 || data[1] != 0 || data[2] is not (2 or 3 or 10 or 11))
            throw new NotSupportedException("這個 TGA 檔使用不支援的色彩格式。");
        var width = BitConverter.ToUInt16(data, 12);
        var height = BitConverter.ToUInt16(data, 14);
        var grayscale = data[2] is 3 or 11;
        var bytesPerPixel = data[16] / 8;
        if (width == 0 || height == 0 || (long)width * height > 64_000_000 ||
            (grayscale ? data[16] != 8 : data[16] is not (24 or 32)))
            throw new NotSupportedException("這個 TGA 檔使用不支援的影像尺寸或位元深度。");
        var pixels = new byte[width * height * 4];
        var input = 18 + data[0];
        var output = 0;
        var compressed = data[2] is 10 or 11;
        while (output < width * height)
        {
            if (input >= data.Length) throw new InvalidDataException("TGA 檔案不完整。");
            var header = compressed ? data[input++] : 0;
            var repeated = (header & 128) != 0;
            var count = (header & 127) + 1;
            if (count > width * height - output || input + (repeated ? 1 : count) * bytesPerPixel > data.Length)
                throw new InvalidDataException("TGA 檔案不完整。");
            for (var i = 0; i < count; i++, output++)
            {
                var row = output / width;
                var col = output % width;
                if ((data[17] & 32) == 0) row = height - row - 1;
                if ((data[17] & 16) != 0) col = width - col - 1;
                var offset = (row * width + col) * 4;
                var source = input + (repeated ? 0 : i * bytesPerPixel);
                pixels[offset] = data[source];
                pixels[offset + 1] = data[source + (grayscale ? 0 : 1)];
                pixels[offset + 2] = data[source + (grayscale ? 0 : 2)];
                pixels[offset + 3] = bytesPerPixel == 4 && (data[17] & 15) != 0 ? data[source + 3] : (byte)255;
            }
            input += (repeated ? 1 : count) * bytesPerPixel;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
