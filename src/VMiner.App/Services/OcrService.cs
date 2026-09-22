using System.Text;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace VMiner.Services;

public sealed class OcrService
{
    private const int Upscale = 4;
    private const int MaximumSide = 4000;

    public static string CheckJapaneseAvailability()
    {
        var language = new Language("ja");
        if (!OcrEngine.IsLanguageSupported(language))
            throw new InvalidOperationException(
                "The Windows Japanese OCR language pack is not installed.");
        if (OcrEngine.TryCreateFromLanguage(language) is null)
            throw new InvalidOperationException(
                "Windows could not initialize the Japanese OCR engine.");
        return "Japanese OCR OK";
    }

    public async Task<string> ReadJapaneseAsync(BitmapSource image)
    {
        CheckJapaneseAvailability();
        var language = new Language("ja");

        var engine = OcrEngine.TryCreateFromLanguage(language)
                     ?? throw new InvalidOperationException(
                         "Windows could not initialize the Japanese OCR engine.");

        var prepared = Prepare(image);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(prepared));
        using var memory = new MemoryStream();
        encoder.Save(memory);

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(memory.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        stream.Seek(0);
        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var bitmap = await decoder.GetSoftwareBitmapAsync();
        var result = await engine.RecognizeAsync(bitmap);
        var text = result.Lines.Count > 0
            ? string.Join('\n', result.Lines.Select(line => line.Text))
            : result.Text;
        return StripJapaneseSpaces(text);
    }

    private static BitmapSource Prepare(BitmapSource image)
    {
        var longest = Math.Max(image.PixelWidth, image.PixelHeight);
        var factor = Math.Min(Upscale, Math.Max(1, MaximumSide / Math.Max(longest, 1)));
        if (factor <= 1)
            return image;

        var transformed = new TransformedBitmap(image, new ScaleTransform(factor, factor));
        transformed.Freeze();
        return transformed;
    }

    private static string StripJapaneseSpaces(string text)
    {
        var output = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == ' ')
            {
                var previous = output.Length > 0 ? output[^1] : '\0';
                var following = index + 1 < text.Length ? text[index + 1] : '\0';
                if (IsJapanese(previous) && IsJapanese(following))
                    continue;
            }
            output.Append(character);
        }
        return output.ToString().Trim();
    }

    private static bool IsJapanese(char value) =>
        value is >= '\u3040' and <= '\u30ff'
        or >= '\u3400' and <= '\u9fff'
        || "、。「」『』！？・ー".Contains(value);
}
