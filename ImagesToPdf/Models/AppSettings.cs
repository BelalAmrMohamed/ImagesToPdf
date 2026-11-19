namespace ImagesToPdf.Models;

public sealed class AppSettings
{
    public string ThemePreference { get; set; } = "System";
    public bool RecursiveFolderImport { get; set; }
    public bool IncludeAllFrames { get; set; }
    public int PageSizeIndex { get; set; }
    public int ResolutionIndex { get; set; }
    public int DpiIndex { get; set; } = 0;
    public int FitModeIndex { get; set; }
    public int PdfThemeIndex { get; set; }
    public int QualityIndex { get; set; } = 1;
    public bool AutoSort { get; set; } = true;
}
