namespace GameFerry.Core.Library;

/// <summary>Game titles by ID, loaded from a GameTDB <c>wiitdb.txt</c> (lines of <c>ID = Title</c>).</summary>
public sealed class GameTitles
{
    private readonly Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);

    public static GameTitles Empty { get; } = new();

    public int Count => _titles.Count;

    /// <summary>Loads the titles file; a missing or unreadable file just yields no titles.</summary>
    public static GameTitles Load(string? path)
    {
        GameTitles titles = new();
        if (path is null || !File.Exists(path))
            return titles;

        try
        {
            foreach (string line in File.ReadLines(path))
            {
                int separator = line.IndexOf(" = ", StringComparison.Ordinal);
                if (separator is < 4 or > 6)
                    continue;

                string id = line[..separator];
                string title = line[(separator + 3)..].Trim();

                // The first line is a "TITLES = <url>" banner.
                if (id != "TITLES" && title.Length > 0)
                    titles._titles[id] = title;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return titles;
    }

    /// <summary>Finds a title by six-character game ID, falling back to the four-character form.</summary>
    public string? Find(string? id6)
    {
        if (string.IsNullOrEmpty(id6))
            return null;
        if (_titles.TryGetValue(id6, out string? title))
            return title;
        return id6.Length >= 4 && _titles.TryGetValue(id6[..4], out title) ? title : null;
    }
}
