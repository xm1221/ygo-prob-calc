// Decode YGOPro / ocgcore bitmask fields into YGOPRODeck-compatible strings
// so CardPropertyProvider keeps working unchanged.

export const TYPE = {
    MONSTER: 0x1,
    SPELL: 0x2,
    TRAP: 0x4,
    NORMAL: 0x10,
    EFFECT: 0x20,
    FUSION: 0x40,
    RITUAL: 0x80,
    TRAPMONSTER: 0x100,
    SPIRIT: 0x200,
    UNION: 0x400,
    DUAL: 0x800,
    TUNER: 0x1000,
    SYNCHRO: 0x2000,
    TOKEN: 0x4000,
    QUICKPLAY: 0x10000,
    CONTINUOUS: 0x20000,
    EQUIP: 0x40000,
    FIELD: 0x80000,
    COUNTER: 0x100000,
    FLIP: 0x200000,
    TOON: 0x400000,
    XYZ: 0x800000,
    PENDULUM: 0x1000000,
    SPSUMMON: 0x2000000,
    LINK: 0x4000000,
};

const ATTRIBUTES = [
    [0x01, "EARTH"],
    [0x02, "WATER"],
    [0x04, "FIRE"],
    [0x08, "WIND"],
    [0x10, "LIGHT"],
    [0x20, "DARK"],
    [0x40, "DIVINE"],
];

const RACES = [
    [0x1, "Warrior"],
    [0x2, "Spellcaster"],
    [0x4, "Fairy"],
    [0x8, "Fiend"],
    [0x10, "Zombie"],
    [0x20, "Machine"],
    [0x40, "Aqua"],
    [0x80, "Pyro"],
    [0x100, "Rock"],
    [0x200, "Winged Beast"],
    [0x400, "Plant"],
    [0x800, "Insect"],
    [0x1000, "Thunder"],
    [0x2000, "Dragon"],
    [0x4000, "Beast"],
    [0x8000, "Beast-Warrior"],
    [0x10000, "Dinosaur"],
    [0x20000, "Fish"],
    [0x40000, "Sea Serpent"],
    [0x80000, "Reptile"],
    [0x100000, "Psychic"],
    [0x200000, "Divine-Beast"],
    [0x400000, "Creator God"],
    [0x800000, "Wyrm"],
    [0x1000000, "Cyberse"],
    [0x2000000, "Illusion"],
];

function has(type, flag) {
    return (type & flag) === flag;
}

export function decodeAttribute(attribute) {
    if (!Number.isInteger(attribute) || attribute <= 0) {
        return undefined;
    }
    for (const [bit, name] of ATTRIBUTES) {
        if ((attribute & bit) === bit) {
            return name;
        }
    }
    return undefined;
}

export function decodeRace(race, type) {
    if (has(type, TYPE.SPELL) || has(type, TYPE.TRAP)) {
        if (has(type, TYPE.FIELD)) return "Field";
        if (has(type, TYPE.EQUIP)) return "Equip";
        if (has(type, TYPE.QUICKPLAY)) return "Quick-Play";
        if (has(type, TYPE.CONTINUOUS)) return "Continuous";
        if (has(type, TYPE.COUNTER)) return "Counter";
        if (has(type, TYPE.RITUAL) && has(type, TYPE.SPELL)) return "Ritual";
        return "Normal";
    }
    if (!Number.isInteger(race) || race <= 0) {
        return undefined;
    }
    for (const [bit, name] of RACES) {
        if ((race & bit) === bit) {
            return name;
        }
    }
    return undefined;
}

export function decodeLevelFields(level, type) {
    if (!Number.isInteger(level) || level < 0) {
        return {};
    }
    const result = {};
    const base = level & 0xff;
    if (has(type, TYPE.LINK)) {
        if (base > 0) {
            result.linkVal = base;
        }
    }
    else if (base > 0 || has(type, TYPE.XYZ)) {
        result.level = base;
    }
    if (has(type, TYPE.PENDULUM)) {
        const left = (level >> 16) & 0xff;
        const right = (level >> 24) & 0xff;
        // Prefer left scale when both present; UI uses a single scale value.
        if (left > 0 || right > 0) {
            result.scale = left || right;
        }
    }
    return result;
}

export function decodeTypeAndFrame(type) {
    if (!Number.isInteger(type) || type <= 0) {
        return { typeName: "Unknown", frameType: undefined };
    }
    if (has(type, TYPE.SPELL)) {
        return { typeName: "Spell Card", frameType: "spell" };
    }
    if (has(type, TYPE.TRAP)) {
        return { typeName: "Trap Card", frameType: "trap" };
    }

    const pendulum = has(type, TYPE.PENDULUM);
    let frameType = "effect";
    const parts = [];

    if (has(type, TYPE.NORMAL) && !has(type, TYPE.EFFECT)) {
        parts.push("Normal");
        frameType = "normal";
    }
    if (has(type, TYPE.EFFECT) && !has(type, TYPE.NORMAL)) {
        // Effect alone is often omitted when combined with Tuner/Flip/etc.
    }
    if (has(type, TYPE.FUSION)) {
        parts.push("Fusion");
        frameType = "fusion";
    }
    if (has(type, TYPE.RITUAL) && has(type, TYPE.MONSTER)) {
        parts.push("Ritual");
        frameType = "ritual";
    }
    if (has(type, TYPE.SYNCHRO)) {
        parts.push("Synchro");
        frameType = "synchro";
    }
    if (has(type, TYPE.XYZ)) {
        parts.push("XYZ");
        frameType = "xyz";
    }
    if (has(type, TYPE.LINK)) {
        parts.push("Link");
        frameType = "link";
    }
    if (has(type, TYPE.TOKEN)) {
        parts.push("Token");
    }
    if (has(type, TYPE.TUNER)) {
        parts.push("Tuner");
    }
    if (has(type, TYPE.FLIP)) {
        parts.push("Flip");
    }
    if (has(type, TYPE.TOON)) {
        parts.push("Toon");
    }
    if (has(type, TYPE.SPIRIT)) {
        parts.push("Spirit");
    }
    if (has(type, TYPE.UNION)) {
        parts.push("Union");
    }
    if (has(type, TYPE.DUAL)) {
        parts.push("Gemini");
    }
    if (pendulum) {
        parts.push("Pendulum");
        frameType = `${frameType}_pendulum`;
    }

    // Build YGOPRODeck-style type string.
    let typeName;
    if (parts.includes("Link")) {
        typeName = "Link Monster";
    }
    else if (parts.includes("XYZ")) {
        typeName = pendulum ? "XYZ Pendulum Effect Monster" : "XYZ Monster";
    }
    else if (parts.includes("Synchro")) {
        typeName = pendulum ? "Synchro Pendulum Effect Monster" : (parts.includes("Tuner") ? "Synchro Tuner Monster" : "Synchro Monster");
    }
    else if (parts.includes("Fusion")) {
        typeName = pendulum ? "Fusion Pendulum Effect Monster" : "Fusion Monster";
    }
    else if (parts.includes("Ritual")) {
        typeName = pendulum ? "Ritual Pendulum Effect Monster" : "Ritual Monster";
    }
    else if (parts.includes("Pendulum") && parts.includes("Normal")) {
        typeName = "Pendulum Normal Monster";
        frameType = "normal_pendulum";
    }
    else if (parts.includes("Pendulum")) {
        typeName = "Pendulum Effect Monster";
        frameType = "effect_pendulum";
    }
    else if (parts.includes("Tuner") && parts.includes("Normal")) {
        typeName = "Normal Tuner Monster";
    }
    else if (parts.includes("Tuner")) {
        typeName = "Tuner Monster";
    }
    else if (parts.includes("Flip")) {
        typeName = "Flip Effect Monster";
    }
    else if (parts.includes("Gemini")) {
        typeName = "Gemini Monster";
    }
    else if (parts.includes("Spirit")) {
        typeName = "Spirit Monster";
    }
    else if (parts.includes("Toon")) {
        typeName = "Toon Monster";
    }
    else if (parts.includes("Union")) {
        typeName = "Union Effect Monster";
    }
    else if (parts.includes("Normal")) {
        typeName = "Normal Monster";
    }
    else if (has(type, TYPE.EFFECT) || has(type, TYPE.MONSTER)) {
        typeName = "Effect Monster";
        frameType = "effect";
    }
    else {
        typeName = "Monster";
    }

    return { typeName, frameType };
}

export function pickPrimaryName(card) {
    const text = isRecord(card.text) ? card.text : {};
    const candidates = [
        card.sc_name, card.cn_name, card.md_name,
        text.sc_name, text.cn_name, text.md_name, text.name,
        card.jp_name, text.jp_name,
        card.en_name, text.en_name,
        card.nwbbs_n, card.cnocg_n,
    ];
    for (const value of candidates) {
        if (typeof value === "string" && value.trim()) {
            return value.trim();
        }
    }
    return null;
}

export function collectAliases(card) {
    const text = isRecord(card.text) ? card.text : {};
    const keys = [
        "sc_name", "cn_name", "md_name", "jp_name", "en_name",
        "nwbbs_n", "cnocg_n", "name",
    ];
    const names = new Set();
    for (const key of keys) {
        for (const source of [card, text]) {
            const value = source[key];
            if (typeof value === "string" && value.trim()) {
                names.add(value.trim());
            }
        }
    }
    return [...names];
}

function isRecord(value) {
    return value !== null && typeof value === "object" && !Array.isArray(value);
}
