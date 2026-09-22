namespace VMiner.Models;

public sealed class AppConfig
{
    public string Hotkey { get; set; } = "LeftShift";
    public int MinimumCaptureSize { get; set; } = 12;
    public int FontSize { get; set; } = 22;
    public bool AlwaysOnTop { get; set; } = true;
    public bool StealFocus { get; set; }
    public string TranslationModel { get; set; } = @"models\translategemma-4b-it-Q4_K_M.gguf";
    public string VocabularyDatabasePath { get; set; } = "";
}
