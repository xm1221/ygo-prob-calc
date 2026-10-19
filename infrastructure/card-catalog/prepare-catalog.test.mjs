import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { buildCatalog, serializeCatalog, projectYgocdbCard, md5Hex } from "./prepare-catalog.mjs";
import { decodeTypeAndFrame, decodeLevelFields, pickPrimaryName } from "./ygopro-decode.mjs";

const here = dirname(fileURLToPath(import.meta.url));

function miniCard(overrides) {
    return {
        cid: 1,
        id: 100,
        sc_name: "测试卡",
        cn_name: "测试卡",
        en_name: "Test Card",
        jp_name: "テストカード",
        data: { ot: 3, type: 17, atk: 0, def: 0, level: 1, race: 1, attribute: 1 },
        text: { types: "", desc: "" },
        ...overrides,
    };
}

test("projects ygocdb objective metadata and Chinese primary name with aliases", () => {
    const ash = {
        cid: 12950,
        id: 14558127,
        sc_name: "灰流丽",
        cn_name: "灰流丽",
        md_name: "灰流丽",
        nwbbs_n: "灰流丽",
        cnocg_n: "灰流丽",
        jp_name: "灰流うらら",
        en_name: "Ash Blossom & Joyous Spring",
        data: { ot: 11, type: 4129, atk: 0, def: 1800, level: 3, race: 16, attribute: 4 },
        text: { types: "", desc: "中文效果" },
    };
    const projected = projectYgocdbCard(ash);
    assert.equal(projected.name, "灰流丽");
    assert.equal(projected.type, "Tuner Monster");
    assert.equal(projected.frameType, "effect");
    assert.equal(projected.race, "Zombie");
    assert.equal(projected.attribute, "FIRE");
    assert.equal(projected.level, 3);
    assert.equal(projected.cid, 12950);
    assert.equal(projected.ot, 11);
    assert.ok(projected.names.includes("Ash Blossom & Joyous Spring"));
    assert.ok(projected.names.includes("灰流うらら"));
    assert.equal(projected.resolvedArchetypes, null);
    assert.deepEqual(projected.artworkImageIds, [14558127]);
});

test("decodes pendulum scale and link rating from YGOPro level packing", () => {
    const pend = decodeLevelFields(67371015, 0x1000021);
    assert.equal(pend.level, 7);
    assert.equal(pend.scale, 4);
    const link = decodeTypeAndFrame(0x4000021);
    assert.equal(link.typeName, "Link Monster");
    assert.equal(link.frameType, "link");
    assert.equal(decodeLevelFields(3, 0x4000021).linkVal, 3);
});

test("buildCatalog from fixture mini-cards.json is deterministic and Chinese-sorted", async () => {
    const raw = await readFile(join(here, "fixtures/mini-cards.json"), "utf8");
    const source = JSON.parse(raw);
    const first = serializeCatalog(buildCatalog(source));
    const second = serializeCatalog(buildCatalog(source));
    assert.deepEqual(first, second);
    const catalog = JSON.parse(first);
    assert.equal(catalog.version, 3);
    assert.equal(catalog.source, "ygocdb");
    assert.ok(catalog.cards.length >= 5);
    const ash = catalog.cards.find(c => c.id === 14558127);
    assert.equal(ash.name, "灰流丽");
    const blue = catalog.cards.find(c => c.id === 89631139);
    assert.equal(blue.name, "青眼白龙");
    assert.ok(blue.names.includes("Blue-Eyes White Dragon"));
});

test("rejects empty or malformed catalogs", () => {
    for (const response of [null, [], {}, { data: [] }]) {
        assert.throws(() => buildCatalog(response));
    }
    assert.throws(() => projectYgocdbCard(miniCard({ id: 0 })));
    assert.throws(() => projectYgocdbCard(miniCard({ sc_name: "", cn_name: "", en_name: "", jp_name: "", md_name: "" })));
});

test("pickPrimaryName prefers sc_name then cn_name", () => {
    assert.equal(pickPrimaryName({ sc_name: "简中", cn_name: "旧译", en_name: "EN" }), "简中");
    assert.equal(pickPrimaryName({ cn_name: "旧译", en_name: "EN" }), "旧译");
    assert.equal(pickPrimaryName({ en_name: "EN" }), "EN");
});

test("md5Hex matches known fixture digest shape", async () => {
    const raw = await readFile(join(here, "fixtures/mini-cards.json"));
    const digest = md5Hex(raw);
    assert.match(digest, /^[a-f0-9]{32}$/);
});
