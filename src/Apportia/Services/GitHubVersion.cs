namespace Apportia.Services;

public static class GitHubVersion
{
    /// Turns a release tag into a 4-part numeric version. Strips a non-digit prefix
    /// (e.g. "v"), splits on `.` `-` `_` `+`, keeps only leading digits per segment,
    /// stops at the first segment without any, and pads to four with `.0`. Returns
    /// empty when nothing numeric can be extracted.
    public static string NormalizeTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return string.Empty;

        var trimmed = tag.Trim();
        var start = 0;
        while (start < trimmed.Length && !char.IsDigit(trimmed[start]))
            start++;
        if (start >= trimmed.Length)
            return string.Empty;

        var parts = trimmed[start..].Split(['.', '-', '_', '+']);
        var segments = new List<string>(4);
        foreach (var part in parts)
        {
            if (segments.Count == 4)
                break;
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0)
                break;
            segments.Add(digits);
        }

        if (segments.Count == 0)
            return string.Empty;
        while (segments.Count < 4)
            segments.Add("0");
        return string.Join('.', segments);
    }

    /// Fallback used when the tag yields no numeric segments (e.g. "nightly"). Encodes
    /// the release timestamp as yy.M.d.HHMM so ordering stays monotonic per release.
    public static string FromDate(DateTimeOffset published)
    {
        var local = published.LocalDateTime;
        return $"{local:yy}.{local.Month}.{local.Day}.{local.Hour * 100 + local.Minute}";
    }

    /// Prefers the tag; falls back to the release date. Returns empty on both empty.
    public static string Derive(string tag, DateTimeOffset? published)
    {
        var fromTag = NormalizeTag(tag);
        if (fromTag.Length > 0)
            return fromTag;
        return published is { } dt ? FromDate(dt) : string.Empty;
    }
}
