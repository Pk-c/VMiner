using System.Text.RegularExpressions;
using Kawazu;
using VMiner.Models;

namespace VMiner.Services;

public sealed partial class FuriganaService : IDisposable
{
    private readonly KawazuConverter _converter = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<(IReadOnlyList<RubySegment> Segments, string Reading)> ConvertAsync(
        string text)
    {
        await _gate.WaitAsync();
        try
        {
            var ruby = await _converter.Convert(
                text, To.Hiragana, Mode.Furigana, RomajiSystem.Hepburn, "(", ")");
            var reading = await _converter.Convert(text, To.Hiragana, Mode.Normal);
            return (ParseRuby(ruby), reading);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<RubySegment> ParseRuby(string value)
    {
        var segments = new List<RubySegment>();
        var position = 0;
        foreach (Match match in RubyExpression().Matches(value))
        {
            if (match.Index > position)
                segments.Add(new RubySegment(value[position..match.Index]));
            segments.Add(new RubySegment(match.Groups[1].Value, match.Groups[2].Value));
            position = match.Index + match.Length;
        }
        if (position < value.Length)
            segments.Add(new RubySegment(value[position..]));
        return segments;
    }

    [GeneratedRegex("<ruby>(.*?)<rp>.*?</rp><rt>(.*?)</rt><rp>.*?</rp></ruby>",
        RegexOptions.Singleline)]
    private static partial Regex RubyExpression();

    public void Dispose()
    {
        _converter.Dispose();
        _gate.Dispose();
    }
}
