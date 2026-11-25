using System.Text.RegularExpressions;

namespace PlaylistViewer;

internal sealed record M3uEntry(string Name, string Title, string Group, string Id, string Url, string SearchText);

internal static partial class M3uParser
{
    private static readonly Regex AttributeRegex = new(
        @"(?<key>[\w-]+)\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<M3uEntry> Parse(string content)
    {
        var entries = new List<M3uEntry>();
        string? title = null;
        string? name = null;
        string? group = null;
        string? id = null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var separator = FindTitleSeparator(line);
                var metadata = separator >= 0 ? line[8..separator] : line[8..];
                title = separator >= 0 ? line[(separator + 1)..].Trim() : null;

                var attributes = AttributeRegex.Matches(metadata)
                    .ToDictionary(
                        match => match.Groups["key"].Value,
                        match => match.Groups["value"].Value,
                        StringComparer.OrdinalIgnoreCase);

                name = GetAttribute(attributes, "tvg-name");
                group = GetAttribute(attributes, "group-title");
                id = GetAttribute(attributes, "tvg-id");
                continue;
            }

            if (line.StartsWith('#'))
                continue;

            var displayTitle = string.IsNullOrWhiteSpace(title) ? GetUrlName(line) : title;
            var displayName = string.IsNullOrWhiteSpace(name) ? displayTitle : name;
            var displayGroup = group ?? string.Empty;
            var displayId = id ?? string.Empty;
            var searchText = string.Join('\n', displayName, displayTitle, displayGroup, displayId, line);
            entries.Add(new M3uEntry(displayName, displayTitle, displayGroup, displayId, line, searchText));

            title = null;
            name = null;
            group = null;
            id = null;
        }

        return entries;
    }

    private static int FindTitleSeparator(string line)
    {
        var insideQuotes = false;
        for (var index = 8; index < line.Length; index++)
        {
            if (line[index] == '"')
                insideQuotes = !insideQuotes;
            else if (line[index] == ',' && !insideQuotes)
                return index;
        }

        return -1;
    }

    private static string? GetAttribute(Dictionary<string, string> attributes, string key) =>
        attributes.TryGetValue(key, out var value) ? value : null;

    private static string GetUrlName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrWhiteSpace(lastSegment))
                return Uri.UnescapeDataString(lastSegment);
            return uri.Host;
        }

        return url;
    }
}
