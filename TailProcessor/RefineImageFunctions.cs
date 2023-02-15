using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

public class RefineImageFunctions
{
    public static void ResizeImage(Stream input, int width, int height, out Stream output)
    {
        using var img = Image.Load(input);
        var newsize = CalculateResizeToFit(img.Size(), new Size(width, height));

        img.Mutate(_ => _.Resize(newsize));
        output = new MemoryStream();
        img.Save(output, new PngEncoder());
    }

    public static void ResizeImage(string originFile, int width, int height, string outputFileName)
    {
        using var input = File.OpenRead(originFile);

        ResizeImage(input, width, height, out var output);

        using var fs = File.Create(outputFileName);
        output.Seek(0, SeekOrigin.Begin);
        output.CopyTo(fs);
    }

    public static void SaveAsJpg(string originFile, string outputFileName)
    {
        using var img = Image.Load(originFile);
        img.Save(outputFileName, new WebpEncoder());// { ColorType = JpegColorType.Rgb, Quality = 80 });
    }

    private static Size CalculateResizeToFit(Size imageSize, Size boxSize)
    {
        var widthScale = boxSize.Width / (double)imageSize.Width;
        var heightScale = boxSize.Height / (double)imageSize.Height;
        var scale = Math.Min(widthScale, heightScale);
        return new Size(
            (int)Math.Round((imageSize.Width * scale)),
            (int)Math.Round((imageSize.Height * scale))
            );
    }
}