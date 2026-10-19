using System.Globalization;
using System.Text.Json;
using YGOProbabilityCalculatorBlazor.Models;
using YGOProbabilityCalculatorBlazor.Services.Interface;

namespace YGOProbabilityCalculatorBlazor.Services.DeckImport;

public class CardInfoService : ICardInfoService {
    private const string LocalCatalogUrl = "data/card-catalog.v1.json";
    private const string YgocdbCardApiTemplate = "https://ygocdb.com/api/v0/card/{0}";
    private const string YgocdbSearchApiTemplate = "https://ygocdb.com/api/v0/?search={0}";
    private const string CacheKey = "cardCache";
    // Bumped so English YGOPRODeck browser caches cannot poison Chinese names.
    private const int CacheSchemaVersion = 3;
    private static readonly TimeSpan CacheTimeToLive = TimeSpan.FromDays(7);
    private const int SearchResultLimit = 20;
    private const int MaximumCatalogBytes = 20 * 1024 * 1024;
    private const int MaximumCatalogCards = 30_000;
    private static readonly JsonSerializerOptions CatalogJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILocalStorageService _localStorage;
    private readonly TimeProvider _timeProvider;
    private readonly Lazy<Task> _loadCacheTask;
    private readonly Lazy<Task<IReadOnlyList<CardInfo>>> _loadCatalogTask;
    private CardMetadataCache _cache = new();
    private readonly SemaphoreSlim _singleLookupGate = new(1, 1);
    private readonly HashSet<int> _singleLookups = [];
    private readonly SemaphoreSlim _artworkLookupGate = new(1, 1);
    private readonly HashSet<int> _missingArtwork = [];
    private DateTimeOffset _nextArtworkLookup;

    public async Task<CardInfo> GetCardArtworkInfoAsync(int id) {
        // Artwork never initializes/refreshes the full catalog. Reuse even stale
        // validated image IDs, then enrich only a visible requested passcode.
        await _loadCacheTask.Value;

        if (_loadCatalogTask.IsValueCreated) {
            try {
                CardInfo? catalogCard = (await _loadCatalogTask.Value).FirstOrDefault(card => card.Id == id);

                if (catalogCard is not null) {
                    return catalogCard;
                }
            }
            catch {
                // A missing local catalog must not block the existing artwork fallback.
            }
        }

        // Retained identities must not queue behind another card's cold metadata.
        await _singleLookupGate.WaitAsync();

        try {
            if (_cache.Cards.TryGetValue(id, out CardInfo cached) && cached.ArtworkMetadataKnown) {
                return cached;
            }
        }
        finally {
            _singleLookupGate.Release();
        }

        await _artworkLookupGate.WaitAsync();

        try {
            if (_missingArtwork.Contains(id)) {
                return new CardInfo { Id = id, ArtworkMetadataKnown = true };
            }

            await _singleLookupGate.WaitAsync();

            try {
                if (_cache.Cards.TryGetValue(id, out CardInfo cached) && cached.ArtworkMetadataKnown) {
                    return cached;
                }
            }
            finally {
                _singleLookupGate.Release();
            }

            // Cache hits remain immediate. Space only new artwork JSON requests;
            // the metadata API's limit does not authorize image-download bursts.
            TimeSpan wait = _nextArtworkLookup - _timeProvider.GetUtcNow();

            if (wait > TimeSpan.Zero) {
                await Task.Delay(wait, _timeProvider);
            }

            // Prefer ygocdb metadata; on miss, mark known-empty so the artwork worker is not polled forever.
            // Catalog-backed passcodes already resolve above. Missing art must not break probability calc.
            _nextArtworkLookup = _timeProvider.GetUtcNow().AddMilliseconds(100);
            CardInfo? info;
            try {
                info = await TryFetchYgocdbCardAsync(id);
            }
            catch (CardArtworkLookupException) {
                throw;
            }

            if (info is null) {
                _missingArtwork.Add(id);
                info = new CardInfo { Id = id, ArtworkMetadataKnown = true };
            }
            else if (!info.ArtworkMetadataKnown || info.ArtworkImageIds.Count == 0) {
                info = info with {
                    ArtworkImageIds = info.ArtworkImageIds.Count > 0 ? info.ArtworkImageIds : [id],
                    ArtworkMetadataKnown = true
                };
            }

            // Do not hold the import/enrichment gate across artwork HTTP or storage.
            // Cache publication is the only shared critical section.
            await _singleLookupGate.WaitAsync();
            CardMetadataCache snapshot;

            try {
                _cache.Cards[id] = info;

                if (_cache.SchemaVersion != CacheSchemaVersion) {
                    _cache.LastFullRefreshUtc = null;
                }

                _cache.SchemaVersion = CacheSchemaVersion;

                snapshot = new() {
                    SchemaVersion = _cache.SchemaVersion,
                    LastFullRefreshUtc = _cache.LastFullRefreshUtc,
                    Cards = new(_cache.Cards)
                };
            }
            finally {
                _singleLookupGate.Release();
            }

            await SaveCacheAsync(snapshot);

            return info;
        }
        finally {
            _artworkLookupGate.Release();
        }
    }

    public async Task<IReadOnlyList<CardInfo>> SearchCardsAsync(string query) {
        string normalizedQuery = query?.Trim() ?? string.Empty;

        if (normalizedQuery.Length < 2) {
            return Array.Empty<CardInfo>();
        }

        IReadOnlyList<CardInfo> catalog = await _loadCatalogTask.Value;
        List<CardInfo> prefixMatches = new(SearchResultLimit);
        List<CardInfo> substringMatches = new(SearchResultLimit);
        HashSet<int> seen = [];

        // Prefer primary-name prefixes (catalog is sorted by Name), then alias prefixes, then substrings.
        int lower = 0;
        int upper = catalog.Count;

        while (lower < upper) {
            int middle = lower + (upper - lower) / 2;
            if (StringComparer.OrdinalIgnoreCase.Compare(catalog[middle].Name, normalizedQuery) < 0) {
                lower = middle + 1;
            }
            else {
                upper = middle;
            }
        }

        for (int index = lower; index < catalog.Count && prefixMatches.Count < SearchResultLimit; index++) {
            CardInfo card = catalog[index];
            if (!card.Name.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase)) {
                break;
            }

            if (seen.Add(card.Id)) {
                prefixMatches.Add(card);
            }
        }

        foreach (CardInfo card in catalog) {
            if (prefixMatches.Count == SearchResultLimit) {
                break;
            }

            if (seen.Contains(card.Id)) {
                continue;
            }

            if (card.StartsWithSearch(normalizedQuery) && seen.Add(card.Id)) {
                prefixMatches.Add(card);
            }
        }

        foreach (CardInfo card in catalog) {
            if (prefixMatches.Count + substringMatches.Count == SearchResultLimit) {
                break;
            }

            if (seen.Contains(card.Id)) {
                continue;
            }

            if (card.MatchesSearch(normalizedQuery) && seen.Add(card.Id)) {
                substringMatches.Add(card);
            }
        }

        return prefixMatches.Concat(substringMatches).Take(SearchResultLimit).ToArray();
    }

    public async Task<CardInfo> GetCardInfoAsync(int id) {
        await _loadCacheTask.Value;
        IReadOnlyList<CardInfo>? catalog = await TryLoadCatalogAsync();
        CardInfo? catalogCard = FindCatalogCard(catalog, id);

        if (catalogCard is not null) {
            return catalogCard.Id == id ? catalogCard : catalogCard with { Id = id };
        }

        await _singleLookupGate.WaitAsync();

        try {
            bool hasCachedCard = _cache.Cards.TryGetValue(id, out CardInfo card);
            bool needsObjectiveMetadata = !hasCachedCard
                || (string.IsNullOrWhiteSpace(card.Type) && string.IsNullOrWhiteSpace(card.FrameType))
                || !IsFresh(_cache);

            if (needsObjectiveMetadata) {
                if (_singleLookups.Add(id)) {
                    await FetchSingleCardAsync(id);
                }
            }

            return _cache.Cards.TryGetValue(id, out card)
                ? WithCatalogMembership(card, catalog)
                : new CardInfo { Id = id, Name = id.ToString(CultureInfo.InvariantCulture) };
        }
        finally {
            _singleLookupGate.Release();
        }
    }

    public CardInfoService(ILocalStorageService localStorage, HttpClient? httpClient = null, TimeProvider? timeProvider = null) {
        _localStorage = localStorage;
        _httpClient = httpClient ?? new HttpClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _loadCacheTask = new Lazy<Task>(async () => _cache = await LoadCacheAsync());
        _loadCatalogTask = new Lazy<Task<IReadOnlyList<CardInfo>>>(LoadCatalogAsync);
    }

    public async Task<string> GetCardNameAsync(int id) => (await GetCardInfoAsync(id)).Name;

    public async Task<IReadOnlyList<CardInfo>> GetCatalogCardsAsync() => await TryLoadCatalogAsync() ?? [];

    private static CardInfo? FindCatalogCard(IReadOnlyList<CardInfo>? catalog, int id) {
        CardInfo? canonical = catalog?.FirstOrDefault(card => card.Id == id);
        if (canonical is not null || catalog is null) {
            return canonical;
        }

        CardInfo[] artworkMatches = catalog.Where(card => card.ArtworkImageIds.Contains(id)).Take(2).ToArray();
        return artworkMatches.Length == 1 ? artworkMatches[0] : null;
    }

    private static CardInfo WithCatalogMembership(CardInfo info, IReadOnlyList<CardInfo>? catalog) {
        CardInfo? canonical = FindCatalogCard(catalog, info.CanonicalCardId ?? info.Id);
        return canonical is null ? info : info with {
            ResolvedArchetypes = canonical.ResolvedArchetypes,
            ResolvedNames = canonical.ResolvedNames,
            MentionedNames = canonical.MentionedNames
        };
    }

    public async Task<IReadOnlyDictionary<string, CardInfo>> GetCardInfoByExactNamesAsync(IEnumerable<string> names) {
        string[] requested = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Dictionary<string, CardInfo> resolved = new(StringComparer.Ordinal);

        if (requested.Length == 0) {
            return resolved;
        }

        await _loadCacheTask.Value;
        IReadOnlyList<CardInfo>? catalog = await TryLoadCatalogAsync();
        await _singleLookupGate.WaitAsync();

        try {
            foreach (string name in requested) {
                CardInfo[] matches = catalog is null
                    ? []
                    : [.. catalog.Where(card => NameOrAliasEquals(card, name))];

                if (matches.Length == 0) {
                    matches = [.. _cache.Cards.Values.Where(card => NameOrAliasEquals(card, name))];
                }

                if (matches.Length == 1 && HasMetadata(matches[0])) {
                    resolved[name] = WithCatalogMembership(matches[0], catalog);
                }
            }

            // Each unresolved name uses the exact endpoint. A bad/custom name cannot fail other lookups.
            foreach (string name in requested.Where(name => !resolved.ContainsKey(name))) {
                CardInfo? info = await FetchExactNameAsync(name);

                if (info is not null) {
                    resolved[name] = WithCatalogMembership(info, catalog);
                }
            }

            return resolved;
        }
        finally {
            _singleLookupGate.Release();
        }
    }

    private static bool HasMetadata(CardInfo info) => info.Id > 0 &&
        (!string.IsNullOrWhiteSpace(info.Type) || !string.IsNullOrWhiteSpace(info.FrameType)) &&
        CardPropertyProvider.GetCategories(info).Count > 0;

    private async Task<CardInfo?> FetchExactNameAsync(string name) {
        try {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(string.Format(CultureInfo.InvariantCulture, YgocdbSearchApiTemplate, Uri.EscapeDataString(name)))
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

            using JsonDocument document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                return null;
            }

            JsonElement list = default;
            bool hasYgocdb = TryGetProperty(document.RootElement, "result", out list) && list.ValueKind == JsonValueKind.Array;
            bool hasLegacy = !hasYgocdb && TryGetProperty(document.RootElement, "data", out list) && list.ValueKind == JsonValueKind.Array;
            if (!hasYgocdb && !hasLegacy) {
                return null;
            }

            CardInfo?[] matches = list.EnumerateArray()
                .Select(item => hasYgocdb ? ReadYgocdbCard(item) : ReadCardInfo(item))
                .Where(info => info is not null && NameOrAliasEquals(info!, name))
                .ToArray();

            if (matches.Length != 1 || !HasMetadata(matches[0]!)) {
                return null;
            }

            CardInfo match = matches[0]!;
            _cache.Cards[match.Id] = match;

            if (_cache.SchemaVersion != CacheSchemaVersion) {
                _cache.LastFullRefreshUtc = null;
            }

            _cache.SchemaVersion = CacheSchemaVersion;
            await SaveCacheAsync().ConfigureAwait(false);

            return match;
        }
        catch {
            return null;
        }
    }

    private static bool NameOrAliasEquals(CardInfo card, string name) {
        if (card.Name.Equals(name, StringComparison.Ordinal)) {
            return true;
        }

        return card.Names.Any(alias => alias.Equals(name, StringComparison.Ordinal));
    }

    private bool IsFresh(CardMetadataCache cache) {
        if (cache.SchemaVersion != CacheSchemaVersion || cache.LastFullRefreshUtc is not DateTimeOffset lastRefresh) {
            return false;
        }

        TimeSpan age = _timeProvider.GetUtcNow() - lastRefresh;

        return age >= TimeSpan.Zero && age < CacheTimeToLive;
    }

    private async Task<IReadOnlyList<CardInfo>?> TryLoadCatalogAsync() {
        try {
            return await _loadCatalogTask.Value;
        }
        catch {
            // Imported-card and legacy-name paths retain their targeted API fallback.
            return null;
        }
    }

    private async Task<IReadOnlyList<CardInfo>> LoadCatalogAsync() {
        using HttpResponseMessage response = await _httpClient.GetAsync(LocalCatalogUrl);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaximumCatalogBytes) {
            throw new InvalidDataException("The local card catalog exceeds the supported size.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        byte[] json = await ReadCatalogBytesAsync(stream);
        CatalogDocument? document = JsonSerializer.Deserialize<CatalogDocument>(json, CatalogJsonOptions);

        if (document?.Version is not (1 or 2 or 3) || document.Cards is null || document.Cards.Length is 0 or > MaximumCatalogCards) {
            throw new InvalidDataException("The local card catalog is empty, oversized, or unsupported.");
        }

        HashSet<int> ids = new();

        foreach (CardInfo card in document.Cards) {
            if (card.Id <= 0 || string.IsNullOrWhiteSpace(card.Name) || card.Name.Length > 512
                || !card.ArtworkMetadataKnown || card.ArtworkImageIds is null || card.ArtworkImageIds.Count > 100
                || card.ArtworkImageIds.Any(id => id <= 0) || !ids.Add(card.Id)) {
                throw new InvalidDataException("The local card catalog contains an invalid card record.");
            }
            if (card.Names is not null && (card.Names.Count > 32 || card.Names.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512)
                || card.Names.Distinct(StringComparer.Ordinal).Count() != card.Names.Count)) {
                throw new InvalidDataException("The local card catalog contains invalid search aliases.");
            }
            foreach (IReadOnlyList<string>? values in new[] { card.ResolvedNames, card.MentionedNames }) {
                if (values is not null && (values.Count > 32 || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512)
                    || values.Distinct(StringComparer.Ordinal).Count() != values.Count)) {
                    throw new InvalidDataException("The local card catalog contains invalid name metadata.");
                }
            }
            if (card.ResolvedArchetypes is { } memberships && (memberships.Count > 32
                || memberships.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 100)
                || memberships.Distinct(StringComparer.OrdinalIgnoreCase).Count() != memberships.Count)) {
                throw new InvalidDataException("The local card catalog contains invalid archetype memberships.");
            }
        }

        return document.Cards
            .Select(card => document.Version == 1 ? card with { ResolvedArchetypes = null } : card)
            .OrderBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(card => card.Name, StringComparer.Ordinal)
            .ThenBy(card => card.Id)
            .ToArray();
    }

    private static async Task<byte[]> ReadCatalogBytesAsync(Stream stream) {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81_920];
        int read;

        while ((read = await stream.ReadAsync(chunk)) > 0) {
            if (buffer.Length + read > MaximumCatalogBytes) {
                throw new InvalidDataException("The local card catalog exceeds the supported size.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read));
        }

        if (buffer.Length == 0) {
            throw new InvalidDataException("The local card catalog is empty.");
        }

        return buffer.ToArray();
    }

    private async Task FetchSingleCardAsync(int id) {
        try {
            CardInfo? info = await TryFetchYgocdbCardAsync(id).ConfigureAwait(false);

            if (info is not null) {
                _cache.Cards[id] = info.Id == id ? info : info with { Id = id };

                if (_cache.SchemaVersion != CacheSchemaVersion) {
                    _cache.LastFullRefreshUtc = null;
                }

                _cache.SchemaVersion = CacheSchemaVersion;
                await SaveCacheAsync().ConfigureAwait(false);
            }
        }
        catch {
            // Keep imports usable by falling back to the numeric card ID.
        }
    }

    private async Task<CardInfo?> TryFetchYgocdbCardAsync(int id) {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using HttpResponseMessage response = await _httpClient
            .GetAsync(string.Format(CultureInfo.InvariantCulture, YgocdbCardApiTemplate, id), timeout.Token)
            .ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) {
            return null;
        }

        if (!response.IsSuccessStatusCode) {
            System.Net.Http.Headers.RetryConditionHeaderValue? retry = response.Headers.RetryAfter;
            throw new CardArtworkLookupException(retry?.Delta ?? (retry?.Date is DateTimeOffset date
                ? date - _timeProvider.GetUtcNow()
                : TimeSpan.FromMinutes(1)));
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        return ReadRemoteCard(document.RootElement, id);
    }

    private static CardInfo? ReadRemoteCard(JsonElement root, int? cacheId = null) {
        // ygocdb single-card object
        CardInfo? ygocdb = ReadYgocdbCard(root);
        if (ygocdb is not null) {
            return ygocdb;
        }

        // Legacy / test YGOPRODeck envelope: { "data": [ {card}, ... ] }
        if (root.ValueKind == JsonValueKind.Object
            && TryGetProperty(root, "data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Array) {
            return data.EnumerateArray().Select(item => ReadCardInfo(item, cacheId)).FirstOrDefault(card => card is not null);
        }

        return ReadCardInfo(root, cacheId);
    }

    private async Task<CardMetadataCache> LoadCacheAsync() {
        try {
            string? json = await _localStorage.GetRawItemAsync(CacheKey);

            if (string.IsNullOrWhiteSpace(json)) {
                return new CardMetadataCache();
            }

            using JsonDocument document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                return new CardMetadataCache();
            }

            JsonElement root = document.RootElement;

            if (TryGetProperty(root, "schemaVersion", out _)) {
                return TryReadVersionedCache(root, out CardMetadataCache versionedCache)
                    ? versionedCache
                    : new CardMetadataCache();
            }

            return TryReadLegacyCache(root, out CardMetadataCache legacyCache)
                ? legacyCache
                : new CardMetadataCache();
        }
        catch {
            // Malformed data and browser-storage failures must not break imports.
            return new CardMetadataCache();
        }
    }

    private static bool TryReadVersionedCache(JsonElement root, out CardMetadataCache cache) {
        cache = new CardMetadataCache();
        JsonElement cardsProperty = default;
        int version = 0;

        bool hasInvalidCacheHeader = !TryGetProperty(root, "schemaVersion", out JsonElement versionProperty)
            || versionProperty.ValueKind != JsonValueKind.Number
            || !versionProperty.TryGetInt32(out version)
            || version is not (1 or 2 or CacheSchemaVersion)
            || !TryGetProperty(root, "cards", out cardsProperty)
            || cardsProperty.ValueKind != JsonValueKind.Object;

        if (hasInvalidCacheHeader) {
            return false;
        }

        DateTimeOffset? lastFullRefreshUtc = null;
        DateTimeOffset parsedTimestamp = default;

        bool hasValidTimestamp = TryGetProperty(root, "lastFullRefreshUtc", out JsonElement timestampProperty)
            && timestampProperty.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                timestampProperty.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsedTimestamp
            );

        if (hasValidTimestamp) {
            lastFullRefreshUtc = parsedTimestamp;
        }

        Dictionary<int, CardInfo> cards = new();

        foreach (JsonProperty property in cardsProperty.EnumerateObject()) {
            if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                || property.Value.ValueKind != JsonValueKind.Object
                || !TryGetProperty(property.Value, "name", out JsonElement nameProperty)
                || nameProperty.ValueKind != JsonValueKind.String) {

                return false;
            }

            string? name = nameProperty.GetString();

            if (string.IsNullOrWhiteSpace(name)) {
                return false;
            }

            cards[id] = version == 1 ? new CardInfo { Id = id, Name = name } : ReadCardInfo(property.Value, id, fromCache: true)!;
        }

        if (cards.Count == 0) {
            return false;
        }

        cache = new() {
            SchemaVersion = version,
            LastFullRefreshUtc = lastFullRefreshUtc,
            Cards = cards
        };

        return true;
    }

    private static bool TryReadLegacyCache(JsonElement root, out CardMetadataCache cache) {
        Dictionary<int, CardInfo> cards = new();

        foreach (JsonProperty property in root.EnumerateObject()) {
            if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ||
                property.Value.ValueKind != JsonValueKind.String) {
                cache = new CardMetadataCache();

                return false;
            }

            string? name = property.Value.GetString();

            if (!string.IsNullOrWhiteSpace(name)) {
                cards[id] = new CardInfo { Id = id, Name = name };
            }
        }

        cache = new() { SchemaVersion = 1, Cards = cards };

        return true;
    }

    private static CardInfo? ReadYgocdbCard(JsonElement item) {
        if (item.ValueKind != JsonValueKind.Object) {
            return null;
        }

        int? id = Number(item, "id");
        if (id is null || id <= 0) {
            return null;
        }

        List<string> aliases = [];
        void AddAlias(string? value) {
            if (!string.IsNullOrWhiteSpace(value)) {
                string trimmed = value.Trim();
                if (trimmed.Length <= 512 && !aliases.Contains(trimmed, StringComparer.Ordinal)) {
                    aliases.Add(trimmed);
                }
            }
        }

        AddAlias(Text(item, "sc_name"));
        AddAlias(Text(item, "cn_name"));
        AddAlias(Text(item, "md_name"));
        AddAlias(Text(item, "jp_name"));
        AddAlias(Text(item, "en_name"));
        AddAlias(Text(item, "nwbbs_n"));
        AddAlias(Text(item, "cnocg_n"));

        if (TryGetProperty(item, "text", out JsonElement textElement) && textElement.ValueKind == JsonValueKind.Object) {
            AddAlias(Text(textElement, "sc_name"));
            AddAlias(Text(textElement, "cn_name"));
            AddAlias(Text(textElement, "md_name"));
            AddAlias(Text(textElement, "name"));
            AddAlias(Text(textElement, "jp_name"));
            AddAlias(Text(textElement, "en_name"));
        }

        string? name = aliases.Count > 0 ? aliases[0] : null;
        if (string.IsNullOrWhiteSpace(name)) {
            return null;
        }

        string? type = null;
        string? frameType = null;
        string? race = null;
        string? attribute = null;
        int? level = null;
        int? linkVal = null;
        int? scale = null;
        int? ot = null;

        if (TryGetProperty(item, "data", out JsonElement data) && data.ValueKind == JsonValueKind.Object) {
            // Runtime fallback keeps names; objective bitmasks are best-effort via catalog rebuild.
            // Prefer catalog for type/attr/race. Here we only surface ot when present.
            ot = Number(data, "ot");
        }

        // Prefer types string hint when present (Chinese), else leave type empty so HasMetadata may fail
        // and callers keep using the bundled catalog.
        if (TryGetProperty(item, "text", out JsonElement textNode) && textNode.ValueKind == JsonValueKind.Object) {
            string? types = Text(textNode, "types");
            if (!string.IsNullOrWhiteSpace(types)) {
                if (types.Contains("魔法", StringComparison.Ordinal)) {
                    type = "Spell Card";
                    frameType = "spell";
                }
                else if (types.Contains("陷阱", StringComparison.Ordinal)) {
                    type = "Trap Card";
                    frameType = "trap";
                }
                else if (types.Contains("怪兽", StringComparison.Ordinal) || types.Contains("モンスター", StringComparison.Ordinal)) {
                    type = types.Contains("连接", StringComparison.Ordinal) || types.Contains("リンク", StringComparison.Ordinal)
                        ? "Link Monster"
                        : types.Contains("调整", StringComparison.Ordinal) || types.Contains("チューナー", StringComparison.Ordinal)
                            ? "Tuner Monster"
                            : "Effect Monster";
                    frameType = type.StartsWith("Link", StringComparison.Ordinal) ? "link" : "effect";
                }
            }
        }

        return new CardInfo {
            Id = id.Value,
            Name = name,
            Names = aliases,
            Cid = Number(item, "cid"),
            Ot = ot,
            Type = type,
            FrameType = frameType,
            Race = race,
            Attribute = attribute,
            Level = level,
            LinkVal = linkVal,
            Scale = scale,
            CanonicalCardId = id.Value,
            ArtworkImageIds = [id.Value],
            ArtworkMetadataKnown = true
        };

        static string? Text(JsonElement element, string key) =>
            TryGetProperty(element, key, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        static int? Number(JsonElement element, string key) =>
            TryGetProperty(element, key, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
                ? number
                : null;
    }

    private static CardInfo? ReadCardInfo(JsonElement item, int? cacheId = null, bool fromCache = false) {
        if (item.ValueKind != JsonValueKind.Object) {
            return null;
        }

        int? id = cacheId ?? Number("id");
        string? name = Text("name");

        if (id is null || string.IsNullOrWhiteSpace(name)) {
            return null;
        }

        return new CardInfo {
            Id = id.Value,
            Name = name,
            Names = ReadNames(),
            Cid = Number("cid"),
            Ot = Number("ot"),
            Type = Text("type"),
            FrameType = Text("frameType"),
            Race = Text("race"),
            Attribute = Text("attribute"),
            Level = Number("level"),
            LinkVal = Number("linkval") ?? Number("linkVal"),
            Scale = Number("scale"),
            Archetype = Text("archetype"),
            CanonicalCardId = fromCache ? Number("canonicalCardId") : Number("id"),
            ArtworkImageIds = ReadImageIds(),
            ArtworkMetadataKnown = !fromCache || (TryGetProperty(item, "artworkMetadataKnown", out JsonElement known) && known.ValueKind == JsonValueKind.True)
        };

        IReadOnlyList<string> ReadNames() {
            if (!TryGetProperty(item, "names", out JsonElement names) || names.ValueKind != JsonValueKind.Array) {
                return Array.Empty<string>();
            }

            List<string> result = new();
            foreach (JsonElement entry in names.EnumerateArray()) {
                if (entry.ValueKind != JsonValueKind.String) {
                    continue;
                }

                string? value = entry.GetString();
                if (!string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !result.Contains(value, StringComparer.Ordinal)) {
                    result.Add(value);
                }
            }

            return result.Count == 0 ? Array.Empty<string>() : result.AsReadOnly();
        }

        IReadOnlyList<int> ReadImageIds() {
            JsonElement images;
            bool hasArtworkImages = TryGetProperty(item, fromCache ? "artworkImageIds" : "card_images", out images) && images.ValueKind == JsonValueKind.Array;

            if (!hasArtworkImages) {
                return Array.Empty<int>();
            }

            List<int> result = new();

            foreach (JsonElement image in images.EnumerateArray()) {
                int imageId = default;

                if (fromCache) {
                    if (image.ValueKind != JsonValueKind.Number || !image.TryGetInt32(out imageId)) {
                        continue;
                    }
                }
                else {
                    bool hasValidImageId = image.ValueKind == JsonValueKind.Object &&
                        TryGetProperty(image, "id", out JsonElement imageIdValue) &&
                        imageIdValue.ValueKind == JsonValueKind.Number && imageIdValue.TryGetInt32(out imageId);

                    if (!hasValidImageId) {
                        continue;
                    }

                    bool hasExpectedImageUrl = TryGetProperty(image, "image_url_small", out JsonElement url) &&
                        url.ValueKind == JsonValueKind.String &&
                        url.GetString() == $"https://images.ygoprodeck.com/images/cards_small/{imageId}.jpg";

                    if (!hasExpectedImageUrl) {
                        continue;
                    }
                }

                if (imageId is > 0 and <= 2147483647 && !result.Contains(imageId)) {
                    result.Add(imageId);
                }
            }

            return result.Count == 0 ? Array.Empty<int>() : result.AsReadOnly();
        }

        string? Text(string key) => TryGetProperty(item, key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

        int? Number(string key) => TryGetProperty(item, key, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int number)
                ? number
                : null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement property) {
        if (element.TryGetProperty(name, out property)) {
            return true;
        }

        foreach (JsonProperty candidate in element.EnumerateObject()) {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) {
                property = candidate.Value;
                return true;
            }
        }

        property = default;

        return false;
    }

    private async Task SaveCacheAsync(CardMetadataCache? snapshot = null) {
        try {
            await _localStorage.SetItemAsync(CacheKey, snapshot ?? _cache);
        }
        catch {
            // The in-memory cache remains usable for the current page session.
        }
    }

    private sealed class CatalogDocument {
        public CatalogDocument() { }

        public int Version { get; init; }
        public CardInfo[] Cards { get; init; } = [];
    }

    private sealed class CardMetadataCache {
        public int SchemaVersion { get; set; } = CacheSchemaVersion;
        public DateTimeOffset? LastFullRefreshUtc { get; set; }
        public Dictionary<int, CardInfo> Cards { get; set; } = new();
    }
}
