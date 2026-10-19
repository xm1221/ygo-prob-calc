# Local card search catalog (百鸽 ygocdb)

`prepare-catalog.mjs` builds the compact `CardInfo` JSON asset used by local search from **百鸽 [ygocdb.com](https://ygocdb.com/api)** `cards.json` (inside `cards.zip`). Primary display names prefer 简中 (`sc_name` → `cn_name` → …). Search aliases keep 简中 / 旧译 / 日 / 英 / YGOPro 名.

The asset URL remains `card-catalog.v1.json` for deployment compatibility. Internal format **version 3** adds `names`, `cid`, `ot`, and `source: "ygocdb"`. Objective type / attribute / race / level are decoded from YGOPro bitmasks into the same English type vocabulary the calculator already understands.

## Offline rebuild

```sh
node infrastructure/card-catalog/prepare-catalog.mjs \
  --catalog cards.json \
  --output YGOProbabilityCalculatorBlazor/wwwroot/data/card-catalog.v1.json \
  --md5-file infrastructure/card-catalog/cards.zip.md5
```

## Daily update check

```sh
# Exit 0 = up to date; exit 2 = remote md5 differs
node infrastructure/card-catalog/prepare-catalog.mjs --check-update

# Download zip when changed, rebuild catalog + write md5
node infrastructure/card-catalog/prepare-catalog.mjs \
  --check-update \
  --output YGOProbabilityCalculatorBlazor/wwwroot/data/card-catalog.v1.json
```

GitHub Actions: `.github/workflows/update-card-catalog.yml` (daily ~00:00 Asia/Shanghai + `workflow_dispatch`).

The remote `cards.zip.md5` is the MD5 of the **inner** `cards.json`, not the zip bytes.

## Archetype membership (softened)

Upstream English description parsing (`archetype-membership.mjs`) is **not** applied to CN/JP ygocdb text in this fork. Catalog entries ship `resolvedArchetypes: null` (unverified). Use **user categories** to tag combo roles. Legacy English policy fixtures live under `legacy-en-archetype/` for reference only.

## Artwork

Passcodes are stored as `artworkImageIds`. The existing artwork worker / YGOPRODeck-by-passcode path may still serve images; missing art does not affect probability calculation.

## Tests

```sh
node --test infrastructure/card-catalog/*.test.mjs
```

Unit tests use `fixtures/mini-cards.json` (no live network).
