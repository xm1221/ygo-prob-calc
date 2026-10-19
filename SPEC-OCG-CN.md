# ygo-prob-calc — OCG 卡池 + 中文 (fork of SeyTi01/ygo-advanced-probability-calculator-web)

Repo: https://github.com/xm1221/ygo-prob-calc  
Upstream: https://github.com/SeyTi01/ygo-advanced-probability-calculator-web (default branch `dev`)  
Site target later: `tools.xm1221.cn/ygocalc` (deploy out of scope for this milestone)

## Goals
1. **OCG-first card pool** from 百鸽 [ygocdb.com](https://ygocdb.com/api), not YGOPRODeck English-only.
2. **Daily auto-refresh** of the bundled catalog when `cards.zip.md5` changes.
3. **Chinese UI** (简体) for calculator chrome: buttons, labels, settings, help stubs; card search prefers 简中 / YGOPro 中文名.
4. Keep the existing **exact probability engine** (combos, categories, Hall matching, draw/search models) intact unless a call site must change for new card name fields.

## Non-goals (this milestone)
- Full i18n of every help essay / built-in simulation model note (English OK if too long; prefer Chinese for primary chrome).
- Perfect TCG banlist UX (OCG `limits.json` is enough; TCG optional).
- tools.xm1221.cn Cloudflare deploy (later).
- Rebranding away from upstream attribution (keep LICENSE / README credit).

## Card pool (replace YGOPRODeck catalog pipeline)

### Source
- Full dump: `GET https://ygocdb.com/api/v0/cards.zip` → `cards.json`
- Update check: `GET https://ygocdb.com/api/v0/cards.zip.md5` (MD5 of the **inner** `cards.json`)
- Single card / search only as fallback: `/api/v0/card/{password}`, `/api/v0/?search=`
- Docs: https://ygocdb.com/api

### Local catalog asset
- Continue shipping a compact JSON under `YGOProbabilityCalculatorBlazor/wwwroot/data/` (keep URL name `card-catalog.v1.json` **or** bump to `card-catalog.v2.json` and update `CardInfoService` constants if schema changes).
- Required fields per card (map from ygocdb):
  - `id` = password (卡密)
  - `cid` = official Konami DB id when present
  - `name` = primary display name: prefer `sc_name` → `cn_name` → `md_name` → `jp_name` → `en_name`
  - `names` / search aliases: include all non-empty of sc/cn/md/jp/en/nwbbs/cnocg so search finds 青眼白龙 / Blue-Eyes / 青眼の白龍
  - objective metadata from `data.*` (type/atk/def/level/race/attribute/setcode/ot) — decode YGOPro bitmasks the same way the upstream catalog did for YGOPRODeck fields where feasible
  - `ot` bitmask retained so UI can filter OCG / TCG if easy
- Drop dependency on `https://db.ygoprodeck.com/api/v7/cardinfo.php` for **catalog build and name search**. Artwork may still use YGOPRODeck images by passcode **or** `cdn.233.momobako.com` if already used in CN community; document choice; missing art must not break calc.
- Rewrite `infrastructure/card-catalog/prepare-catalog.mjs` (or add `prepare-catalog-ygocdb.mjs`) to ingest ygocdb `cards.json`. Keep offline rebuild: `--catalog cards.json --output …`.
- Archetype membership: upstream English-text policy will not apply cleanly to Chinese/Japanese text. **Pragmatic approach for this milestone:**
  - Keep objective type/attr/level/race properties working.
  - Disable or soften auto English archetype-from-description if it would produce wrong results on CN/JP text; user categories remain the main way to tag combo roles.
  - Document this clearly in README.

### Runtime (`CardInfoService` and friends)
- Load local catalog; search by aliases / Chinese names (prefix + substring, case-insensitive for Latin).
- Fallback HTTP: prefer ygocdb single-card by id; do **not** require YGOPRODeck for imports to work.
- Browser cache schema: bump version if stored shape changes; old cache must not poison new names.

## Daily update (GitHub Actions)
Add workflow e.g. `.github/workflows/update-card-catalog.yml`:
1. Cron: once daily (Asia/Shanghai friendly UTC, e.g. `0 16 * * *` ≈ 00:00 CST) + `workflow_dispatch`
2. Fetch `cards.zip.md5`; compare to committed `infrastructure/card-catalog/cards.zip.md5` (or last known hash file)
3. If unchanged: exit 0, no commit
4. If changed: download zip, run prepare-catalog, update md5 + catalog JSON, commit on `dev` (or open PR to `dev` — **prefer commit-to-dev with `[skip ci]` only if tests would thrash; otherwise run catalog-only job then existing tests on push**)
5. Never commit secrets; no tokens beyond `GITHUB_TOKEN`
6. Rate-limit politely; one full download per change

Also expose `npm`/`node` script: `node infrastructure/card-catalog/prepare-catalog.mjs --check-update` for local use.

## Chinese UI
- Add a simple locale layer (resource dictionary / Blazor `IStringLocalizer` or a static `UiStrings.zh-CN` map) for calculator chrome: Import, Calculate, Categories, Combos, Settings, Progressive/Fast/Exact labels, empty states, errors.
- Default locale: **zh-CN**. Optional English toggle is nice-to-have, not required.
- Card list shows Chinese primary `name`; tooltip or secondary line may show `en_name` / `jp_name` when present.
- Do not translate card effect text in the catalog beyond what ygocdb already provides.
- README in Chinese (short) + keep English section linking upstream.

## Banlist (optional small win)
- If cheap: load `https://ygocdb.com/api/v0/limits.json` into a static or fetched OCG limit badge; do not block probability calc if missing.

## Verification
- `dotnet --list-sdks` / follow AGENTS.md + `scripts/codex/RESTRICTED-LINUX.md` if needed on this box
- `dotnet restore && dotnet build && dotnet test` on the solution
- Unit/integration: catalog prepare from a **fixture** mini `cards.json` (not live network in unit tests)
- Manual smoke: import a `.ydk` with known OCG passcodes; search `灰流丽` / `青眼白龙` finds cards; Calculate still returns a probability
- Workflow file present and dry-runnable via `workflow_dispatch` logic (script path tested locally with `--check-update` against cached md5)

## Done when
1. Bundled catalog is generated from ygocdb, not YGOPRODeck full dump
2. Daily GHA (or equivalent) checks md5 and refreshes catalog on change
3. Primary UI strings are Chinese; card search works with Chinese names
4. Existing probability tests still pass (adjust fixtures if card names in tests were English-only)
5. README documents fork purpose, data source, update cron, and upstream credit

## Attribution
MIT fork: credit SeyTi01 upstream in README; note modifications for OCG/CN tools.xm1221.cn.
