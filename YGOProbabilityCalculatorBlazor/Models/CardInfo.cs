namespace YGOProbabilityCalculatorBlazor.Models;

/// <summary>Reusable objective fields from the bundled ygocdb-derived card catalog.</summary>
public sealed record CardInfo {
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>Search aliases (简中 / 旧译 / 日 / 英 / YGOPro 名等). Primary <see cref="Name"/> is also listed when present.</summary>
    public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();
    public int? Cid { get; init; }
    /// <summary>YGOPro ot bitmask (OCG/TCG). Optional.</summary>
    public int? Ot { get; init; }
    public string? Type { get; init; }
    public string? FrameType { get; init; }
    public string? Race { get; init; }
    public string? Attribute { get; init; }
    public int? Level { get; init; }
    public int? LinkVal { get; init; }
    public int? Scale { get; init; }
    public string? Archetype { get; init; }
    /// <summary>Offline resolved membership. Null means unverified; the provider association is never a substitute.</summary>
    public IReadOnlyList<string>? ResolvedArchetypes { get; init; }
    public IReadOnlyList<string>? ResolvedNames { get; init; }
    public IReadOnlyList<string>? MentionedNames { get; init; }
    public int? CanonicalCardId { get; init; }
    public IReadOnlyList<int> ArtworkImageIds { get; init; } = Array.Empty<int>();
    public bool ArtworkMetadataKnown { get; init; }

    public int? SelectArtworkImageId(int importedPasscode) {
        if (ArtworkImageIds.Contains(importedPasscode)) {
            return importedPasscode;
        }

        if (CanonicalCardId is { } canonical && ArtworkImageIds.Contains(canonical)) {
            return canonical;
        }

        return ArtworkImageIds.Count > 0 ? ArtworkImageIds[0] : null;
    }

    public bool MatchesSearch(string query) {
        if (string.IsNullOrEmpty(query)) {
            return false;
        }

        if (Name.Contains(query, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        foreach (string alias in Names) {
            if (alias.Contains(query, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    public bool StartsWithSearch(string query) {
        if (string.IsNullOrEmpty(query)) {
            return false;
        }

        if (Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        foreach (string alias in Names) {
            if (alias.StartsWith(query, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }
}
