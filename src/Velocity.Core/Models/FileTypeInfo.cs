namespace Velocity.Core.Models;

/// <summary>Maps a file name to a display category and short extension label.</summary>
public static class FileTypeInfo
{
    private static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "Documents", [".doc"] = "Documents", [".docx"] = "Documents", [".txt"] = "Documents", [".xls"] = "Documents", [".xlsx"] = "Documents",
        [".mp4"] = "Videos", [".mkv"] = "Videos", [".avi"] = "Videos", [".mov"] = "Videos",
        [".mp3"] = "Music", [".wav"] = "Music", [".flac"] = "Music", [".aac"] = "Music",
        [".zip"] = "Archives", [".rar"] = "Archives", [".7z"] = "Archives", [".tar"] = "Archives", [".gz"] = "Archives",
        [".png"] = "Images", [".jpg"] = "Images", [".jpeg"] = "Images", [".gif"] = "Images", [".webp"] = "Images",
        [".exe"] = "Software", [".msi"] = "Software"
    };

    public static string Category(string fileName) =>
        Categories.TryGetValue(Path.GetExtension(fileName ?? string.Empty), out var category) ? category : "Other";

    public static string Label(string fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).TrimStart('.');
        if (extension.Length is 0 or > 4) return "FILE";
        return extension.ToUpperInvariant();
    }
}
