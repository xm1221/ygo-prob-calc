#!/usr/bin/env node
// Build the calculator's compact local card-search catalog from 百鸽 ygocdb cards.json.
// Offline: --catalog cards.json --output … ; Update check: --check-update

import { createHash } from "node:crypto";
import { mkdir, readFile, rename, rm, stat, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import {
    collectAliases,
    decodeAttribute,
    decodeLevelFields,
    decodeRace,
    decodeTypeAndFrame,
    pickPrimaryName,
} from "./ygopro-decode.mjs";

export const YGOCDB_ZIP_URL = "https://ygocdb.com/api/v0/cards.zip";
export const YGOCDB_MD5_URL = "https://ygocdb.com/api/v0/cards.zip.md5";
export const MAX_INPUT_BYTES = 100 * 1024 * 1024;
export const MAX_OUTPUT_BYTES = 20 * 1024 * 1024;
export const MAX_CARD_COUNT = 30_000;
export const MIN_FULL_CATALOG_CARDS = 1_000;
const MAX_TEXT_LENGTH = 512;
const MAX_NUMERIC_VALUE = 1_000;

export function projectYgocdbCard(card) {
    if (!isRecord(card)) {
        throw new Error("Every catalog record must be an object");
    }

    const cardId = card.id;
    if (!Number.isInteger(cardId) || cardId <= 0 || cardId > 2_147_483_647) {
        throw new Error("Every card must have a positive 32-bit integer id");
    }

    const name = pickPrimaryName(card);
    if (!name || name.length > MAX_TEXT_LENGTH) {
        throw new Error(`Card ${cardId} has an invalid name`);
    }

    const data = isRecord(card.data) ? card.data : {};
    const typeBits = Number.isInteger(data.type) ? data.type : 0;
    const { typeName, frameType } = decodeTypeAndFrame(typeBits);
    if (!typeName || typeName.length > MAX_TEXT_LENGTH) {
        throw new Error(`Card ${cardId} has an invalid type`);
    }

    const projected = {
        id: cardId,
        name,
        type: typeName,
        names: collectAliases(card).filter(value => value.length <= MAX_TEXT_LENGTH).slice(0, 32),
    };

    if (frameType) {
        projected.frameType = frameType;
    }

    const race = decodeRace(data.race, typeBits);
    if (race) {
        projected.race = race;
    }

    const attribute = decodeAttribute(data.attribute);
    if (attribute) {
        projected.attribute = attribute;
    }

    const levels = decodeLevelFields(data.level, typeBits);
    for (const [key, value] of Object.entries(levels)) {
        if (!Number.isInteger(value) || value < 0 || value > MAX_NUMERIC_VALUE) {
            throw new Error(`Card ${cardId} has an invalid ${key}`);
        }
        projected[key] = value;
    }

    if (Number.isInteger(card.cid) && card.cid > 0) {
        projected.cid = card.cid;
    }
    if (Number.isInteger(data.ot)) {
        projected.ot = data.ot;
    }

    // Artwork: passcode is enough for YGOPRODeck / momobako URL templates; missing art must not break calc.
    projected.canonicalCardId = cardId;
    projected.artworkImageIds = [cardId];
    projected.artworkMetadataKnown = true;

    // Soften English archetype-from-description: CN/JP text is not fed into the EN membership parser.
    // null = unverified; user categories remain the primary combo tagging path.
    projected.resolvedArchetypes = null;
    projected.resolvedNames = null;
    projected.mentionedNames = null;

    return projected;
}

export function normalizeYgocdbSource(source) {
    if (Array.isArray(source)) {
        return source;
    }
    if (isRecord(source) && Array.isArray(source.data)) {
        // Legacy YGOPRODeck shape is no longer projected; reject loudly.
        throw new Error("YGOPRODeck {data:[]} catalog input is no longer supported; pass ygocdb cards.json");
    }
    if (isRecord(source)) {
        const values = Object.values(source);
        if (values.length > 0 && values.every(isRecord)) {
            return values;
        }
    }
    throw new Error("Expected ygocdb cards.json (object map of cards or an array of cards)");
}

export function buildCatalog(source, minimumCards = 1) {
    const records = normalizeYgocdbSource(source);
    // Prize / unreleased entries sometimes ship with password 0; drop them.
    const usable = records.filter(card => Number.isInteger(card?.id) && card.id > 0);
    if (usable.length < minimumCards || usable.length > MAX_CARD_COUNT) {
        throw new Error("Catalog is empty, incomplete, or larger than the supported limit");
    }

    const cards = usable.map(projectYgocdbCard);
    const ids = new Set(cards.map(card => card.id));
    if (ids.size !== cards.length) {
        throw new Error("Catalog contains duplicate card ids");
    }

    cards.sort((left, right) => compareText(left.name.toLowerCase(), right.name.toLowerCase())
        || compareText(left.name, right.name)
        || left.id - right.id);
    return { version: 3, source: "ygocdb", cards };
}

export function serializeCatalog(catalog) {
    const output = Buffer.from(`${JSON.stringify(catalog)}\n`, "utf8");
    if (output.length === 0 || output.length > MAX_OUTPUT_BYTES) {
        throw new Error("Generated catalog is empty or exceeds the output size limit");
    }
    return output;
}

export async function readCatalogBytes(path) {
    const metadata = await stat(path);
    if (metadata.size > MAX_INPUT_BYTES) {
        throw new Error("Input catalog exceeds the 100 MiB size limit");
    }
    const content = await readFile(path);
    if (content.length === 0 || content.length > MAX_INPUT_BYTES) {
        throw new Error("Input catalog is empty or exceeds the 100 MiB size limit");
    }
    return content;
}

export async function fetchText(url, timeoutMs = 60_000) {
    const response = await fetch(url, { signal: AbortSignal.timeout(timeoutMs) });
    if (!response.ok) {
        throw new Error(`Request failed with HTTP ${response.status}: ${url}`);
    }
    return (await response.text()).trim();
}

export async function downloadYgocdbZip() {
    const response = await fetch(YGOCDB_ZIP_URL, { signal: AbortSignal.timeout(120_000) });
    if (!response.ok) {
        throw new Error(`ygocdb zip request failed with HTTP ${response.status}`);
    }
    const contentLength = Number(response.headers.get("content-length"));
    if (Number.isFinite(contentLength) && contentLength > MAX_INPUT_BYTES) {
        throw new Error("ygocdb zip exceeds the 100 MiB size limit");
    }
    const chunks = [];
    let totalLength = 0;
    for await (const chunk of response.body) {
        totalLength += chunk.length;
        if (totalLength > MAX_INPUT_BYTES) {
            throw new Error("ygocdb zip exceeds the 100 MiB size limit");
        }
        chunks.push(chunk);
    }
    if (totalLength === 0) {
        throw new Error("ygocdb zip response is empty");
    }
    return Buffer.concat(chunks, totalLength);
}

export async function extractCardsJsonFromZip(zipBytes) {
    const { execFile } = await import("node:child_process");
    const { promisify } = await import("node:util");
    const { mkdtemp } = await import("node:fs/promises");
    const { tmpdir } = await import("node:os");
    const { join } = await import("node:path");
    const execFileAsync = promisify(execFile);
    const dir = await mkdtemp(join(tmpdir(), "ygocdb-"));
    const zipPath = join(dir, "cards.zip");
    try {
        await writeFile(zipPath, zipBytes);
        await execFileAsync("unzip", ["-o", "-q", zipPath, "cards.json", "-d", dir]);
        return await readCatalogBytes(join(dir, "cards.json"));
    }
    finally {
        await rm(dir, { recursive: true, force: true });
    }
}

export function md5Hex(buffer) {
    return createHash("md5").update(buffer).digest("hex");
}

export async function writeAtomically(path, content) {
    await mkdir(dirname(path), { recursive: true });
    const temporaryPath = `${path}.tmp`;
    try {
        await writeFile(temporaryPath, content);
        await rename(temporaryPath, path);
    }
    finally {
        await rm(temporaryPath, { force: true });
    }
}

function parseArguments(args) {
    const options = { flags: new Set() };
    for (let index = 0; index < args.length; index++) {
        const key = args[index];
        if (key === "--check-update") {
            options.flags.add("check-update");
            continue;
        }
        if (!["--catalog", "--output", "--md5-file", "--save-source"].includes(key)) {
            throw new Error(`Unknown argument: ${key}`);
        }
        const value = args[++index];
        if (!value) {
            throw new Error(`Missing value for ${key}`);
        }
        options[key.slice(2)] = value;
    }
    return options;
}

async function checkUpdate(options) {
    const md5Path = resolve(options["md5-file"] || "infrastructure/card-catalog/cards.zip.md5");
    const remote = (await fetchText(YGOCDB_MD5_URL)).replace(/[^a-fA-F0-9]/g, "").toLowerCase();
    if (!/^[a-f0-9]{32}$/.test(remote)) {
        throw new Error(`Unexpected remote md5: ${remote}`);
    }
    let local = "";
    try {
        local = (await readFile(md5Path, "utf8")).trim().replace(/[^a-fA-F0-9]/g, "").toLowerCase();
    }
    catch (error) {
        if (error?.code !== "ENOENT") {
            throw error;
        }
    }
    if (local === remote) {
        process.stdout.write(`Catalog up to date (md5=${remote})\n`);
        return { changed: false, remote };
    }
    process.stdout.write(`Catalog update available (local=${local || "none"} remote=${remote})\n`);
    return { changed: true, remote, md5Path };
}

async function main() {
    const options = parseArguments(process.argv.slice(2));

    if (options.flags.has("check-update") && !options.output && !options.catalog) {
        const result = await checkUpdate(options);
        process.exitCode = result.changed ? 2 : 0;
        return;
    }

    if (!options.output) {
        throw new Error("--output is required (unless --check-update alone)");
    }

    let cardsJsonBytes;
    let remoteMd5 = null;

    if (options.catalog) {
        cardsJsonBytes = await readCatalogBytes(resolve(options.catalog));
    }
    else {
        const update = await checkUpdate(options);
        remoteMd5 = update.remote;
        if (options.flags.has("check-update") && !update.changed) {
            process.stdout.write("Skipping rebuild; md5 unchanged.\n");
            return;
        }
        const zipBytes = await downloadYgocdbZip();
        cardsJsonBytes = await extractCardsJsonFromZip(zipBytes);
        const actual = md5Hex(cardsJsonBytes);
        if (actual !== remoteMd5) {
            throw new Error(`Downloaded cards.json md5 ${actual} does not match remote ${remoteMd5}`);
        }
    }

    const text = cardsJsonBytes.toString("utf8").replace(/^\uFEFF/, "");
    const source = JSON.parse(text);
    const catalog = buildCatalog(source, options.catalog ? 1 : MIN_FULL_CATALOG_CARDS);
    const output = serializeCatalog(catalog);
    await writeAtomically(resolve(options.output), output);

    if (options["save-source"]) {
        await writeAtomically(resolve(options["save-source"]), cardsJsonBytes);
    }

    const md5Path = resolve(options["md5-file"] || "infrastructure/card-catalog/cards.zip.md5");
    const hash = remoteMd5 || md5Hex(cardsJsonBytes);
    await writeAtomically(md5Path, `${hash}\n`);

    process.stdout.write(
        `Wrote ${catalog.cards.length.toLocaleString("en-US")} cards (${output.length.toLocaleString("en-US")} bytes) to ${options.output}\n`
    );
}

function isRecord(value) {
    return value !== null && typeof value === "object" && !Array.isArray(value);
}

function compareText(left, right) {
    return left < right ? -1 : left > right ? 1 : 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    main().catch(error => {
        process.stderr.write(`${error.message}\n`);
        process.exitCode = 1;
    });
}
