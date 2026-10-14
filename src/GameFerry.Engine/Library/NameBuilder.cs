using System.Globalization;
using System.Text;

namespace GameFerry.Engine.Library;

/// <summary>Builds the folder names games are stored under.</summary>
public static class NameBuilder
{
    private const int MaxTitleLength = 80;

    /// <summary>
    /// The folder name USB loaders expect: <c>Title [GAMEID]</c>. The first candidate title that survives
    /// sanitising is used; with none, the game ID stands in for the title.
    /// </summary>
    public static string GameFolderName(string id6, params string?[] titleCandidates)
    {
        foreach (string? candidate in titleCandidates)
        {
            string title = SanitizeTitle(candidate);
            if (title.Length > 0)
                return $"{title} [{id6}]";
        }

        return $"{id6} [{id6}]";
    }

    /// <summary>
    /// Reduces a title to plain ASCII that is legal on FAT32 and can't be mistaken for the <c>[ID]</c> suffix.
    /// </summary>
    public static string SanitizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        // Split accented letters so the accent can be dropped and the base letter kept.
        string decomposed = title.Replace(": ", " - ").Normalize(NormalizationForm.FormD);

        StringBuilder text = new(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            text.Append(c switch
            {
                ':' => '-',
                '[' => '(',
                ']' => ')',
                '\\' or '/' or '*' or '?' or '"' or '<' or '>' or '|' => ' ',
                < (char)0x20 or > (char)0x7E => ' ',
                _ => c,
            });
        }

        string collapsed = string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length > MaxTitleLength)
            collapsed = collapsed[..MaxTitleLength];

        // Windows silently drops trailing dots and spaces from names.
        return collapsed.Trim(' ', '.');
    }

    /// <summary>A source file's name without its image/archive extensions, e.g. <c>Game (USA).nkit.iso</c> → <c>Game (USA)</c>.</summary>
    public static string TitleFromFileName(string path)
    {
        string name = Path.GetFileName(path);
        string[] extensions = [".7z", ".zip", ".rar", ".iso", ".gcz", ".wbfs", ".nkit"];
        while (true)
        {
            string? extension = extensions.FirstOrDefault(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
            if (extension is null)
                return name;
            name = name[..^extension.Length];
        }
    }
}
