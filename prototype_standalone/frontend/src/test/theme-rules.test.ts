import { describe, expect, it } from "vitest";
import {
  CUSTOM_ID,
  DEFAULT_COLORS,
  DEFAULT_THEME_STATE,
  THEME_CATEGORIES,
  THEME_PRESETS,
  applyTheme,
  canRestore,
  colorsOf,
  contrastRatio,
  deleteUserTheme,
  editColor,
  filterThemes,
  isDirty,
  isValidHex,
  normalizeHex,
  previewTheme,
  readableOn,
  resetColor,
  resetToDefault,
  resolveTheme,
  restorePrevious,
  sanitizeThemeState,
  saveCustomTheme,
  themeVars,
  type ThemeState,
} from "@/lib/eo/theme";

const pick = (s: ThemeState, id: string) => applyTheme(previewTheme(s, id));

describe("theme collection", () => {
  it("keeps the classic presets and adds Cyber Cyan and Graphite", () => {
    expect(THEME_PRESETS.filter((p) => p.category === "classic").map((p) => p.name)).toEqual([
      "ERROR DEFAULT", "Midnight Blue", "Cyber Cyan", "Crimson Red", "Emerald Green", "Purple Neon",
      "Royal Violet", "Arctic Ice", "Amber Gold", "Obsidian", "Graphite", "Ocean Teal", "Rose Pink",
    ]);
  });

  it("contains no superhero, anime or cartoon themes", () => {
    expect(THEME_PRESETS.every((p) => p.category === "classic")).toBe(true);
    for (const id of ["batman", "thor", "iron-man", "ben-10", "rengoku", "gojo", "itachi"])
      expect(THEME_PRESETS.some((p) => p.id === id)).toBe(false);
    expect(THEME_CATEGORIES.map((c) => c.id)).toEqual(["all", "classic", "custom"]);
  });

  it("themes carry colours only, no effect identity", () => {
    expect(Object.keys(resolveTheme(DEFAULT_THEME_STATE.live)).sort()).toEqual(["category", "colors", "id", "name"]);
  });

  it("primary text stays readable even when a custom primary is near black", () => {
    const c = colorsOf(editColor(DEFAULT_THEME_STATE, "primary", "#050506").live);
    expect(contrastRatio(themeVars(c)["--theme-primary-ink"]!, c.background)).toBeGreaterThanOrEqual(3);
  });

  it("filters by category and search", () => {
    expect(filterThemes(DEFAULT_THEME_STATE, "classic", "").length).toBe(13);
    expect(filterThemes(DEFAULT_THEME_STATE, "all", "blue").map((t) => t.name)).toEqual(["Midnight Blue"]);
    expect(filterThemes(DEFAULT_THEME_STATE, "custom", "").map((t) => t.name)).toEqual(["Custom Theme"]);
  });
});

describe("eleven customizable colours", () => {
  it("include Elevated Surface and Secondary Text", () => {
    expect(themeVars(DEFAULT_COLORS)["--theme-elevated"]).toBe(DEFAULT_COLORS.elevated);
    expect(themeVars(DEFAULT_COLORS)["--theme-text-muted"]).toBe(DEFAULT_COLORS.muted);
  });
  it("older saved themes without them derive them from their own colours", () => {
    const s = sanitizeThemeState({ saved: { preset: "custom", custom: { surface: "#101010", border: "#303030", text: "#FFFFFF" } } });
    expect(s.saved.custom.elevated).not.toBe(DEFAULT_COLORS.elevated);
  });
});

describe("preview and apply", () => {
  it("selecting a theme previews it instantly without applying", () => {
    const s = previewTheme(DEFAULT_THEME_STATE, "midnight-blue");
    expect(colorsOf(s.live).primary).toBe("#3B5BDB");
    expect(s.saved.preset).toBe("error-default");
    expect(isDirty(s)).toBe(true);
  });

  it("Apply Theme keeps it", () => {
    const s = pick(DEFAULT_THEME_STATE, "crimson-red");
    expect(s.saved.preset).toBe("crimson-red");
    expect(isDirty(s)).toBe(false);
  });

  it("ignores unknown theme ids", () => {
    expect(previewTheme(DEFAULT_THEME_STATE, "laser")).toBe(DEFAULT_THEME_STATE);
    expect(previewTheme(DEFAULT_THEME_STATE, "batman")).toBe(DEFAULT_THEME_STATE);
  });
});

describe("hex validation", () => {
  it("accepts #RGB and #RRGGBB, with or without #", () => {
    expect(isValidHex("#abc")).toBe(true);
    expect(isValidHex("12ab9F")).toBe(true);
    expect(normalizeHex("#abc")).toBe("#AABBCC");
  });
  it("rejects anything else", () => {
    expect(isValidHex("#12345")).toBe(false);
    expect(normalizeHex("red")).toBeNull();
  });
});

describe("custom themes", () => {
  it("editing a colour starts a Custom Theme from the current theme's colours", () => {
    const s = editColor(pick(DEFAULT_THEME_STATE, "emerald-green"), "accent", "#ff00aa");
    expect(s.live.preset).toBe(CUSTOM_ID);
    expect(colorsOf(s.live).accent).toBe("#FF00AA");
    expect(colorsOf(s.live).primary).toBe("#059669");
  });

  it("invalid hex leaves the theme unchanged", () => {
    expect(editColor(DEFAULT_THEME_STATE, "primary", "#zzz")).toBe(DEFAULT_THEME_STATE);
  });

  it("per-colour reset returns that colour to ERROR DEFAULT", () => {
    const s = resetColor(editColor(DEFAULT_THEME_STATE, "text", "#000000"), "text");
    expect(colorsOf(s.live).text).toBe(DEFAULT_COLORS.text);
  });

  it("saved custom themes are named, applied and reusable", () => {
    const s = saveCustomTheme(editColor(DEFAULT_THEME_STATE, "primary", "#111111"), "Night Ops");
    expect(s.userThemes.map((u) => u.name)).toEqual(["Night Ops"]);
    expect(s.saved.preset).toBe("user:1");
    const other = pick(s, "graphite");
    const back = pick(other, "user:1");
    expect(colorsOf(back.live, back.userThemes).primary).toBe("#111111");
    expect(filterThemes(back, "custom", "").map((t) => t.name)).toContain("Night Ops");
  });

  it("deleting the theme in use keeps its colours as the working Custom Theme", () => {
    const s = deleteUserTheme(saveCustomTheme(editColor(DEFAULT_THEME_STATE, "accent", "#123456"), "X"), "1");
    expect(s.userThemes).toEqual([]);
    expect(s.saved.preset).toBe(CUSTOM_ID);
    expect(colorsOf(s.saved).accent).toBe("#123456");
  });
});

describe("reset and restore", () => {
  it("Reset Theme restores ERROR DEFAULT", () => {
    const s = resetToDefault(pick(DEFAULT_THEME_STATE, "obsidian"));
    expect(s.saved.preset).toBe("error-default");
    expect(colorsOf(s.live)).toEqual(DEFAULT_COLORS);
  });

  it("Restore previous discards an unapplied preview first", () => {
    const s = restorePrevious(previewTheme(pick(DEFAULT_THEME_STATE, "obsidian"), "rose-pink"));
    expect(s.live.preset).toBe("obsidian");
  });

  it("Restore previous returns to the theme applied before the last one", () => {
    const s = pick(pick(DEFAULT_THEME_STATE, "cyber-cyan"), "amber-gold");
    const back = restorePrevious(s);
    expect(back.saved.preset).toBe("cyber-cyan");
    expect(restorePrevious(back).saved.preset).toBe("error-default");
    expect(canRestore(DEFAULT_THEME_STATE)).toBe(false);
  });
});

describe("persistence", () => {
  it("the applied theme and saved custom themes reload after a restart", () => {
    const s = saveCustomTheme(editColor(pick(DEFAULT_THEME_STATE, "ocean-teal"), "surface", "#222222"), "Web");
    const reloaded = sanitizeThemeState(
      JSON.parse(JSON.stringify({ saved: s.saved, history: s.history, userThemes: s.userThemes })),
    );
    expect(resolveTheme(reloaded.live, reloaded.userThemes)).toMatchObject({ name: "Web", category: "custom" });
    expect(colorsOf(reloaded.live, reloaded.userThemes).surface).toBe("#222222");
  });

  it("an unapplied preview is not what reloads", () => {
    const s = previewTheme(DEFAULT_THEME_STATE, "royal-violet");
    expect(sanitizeThemeState({ saved: s.saved }).live.preset).toBe("error-default");
  });

  it("bad saved values fall back to ERROR DEFAULT", () => {
    const s = sanitizeThemeState({ saved: { preset: "nope", custom: { primary: "blue" }, customMotif: "laser" } });
    expect(s.live.preset).toBe("error-default");
    expect(s.live.custom.primary).toBe(DEFAULT_COLORS.primary);
  });

  it("a saved character theme from an older version falls back to ERROR DEFAULT", () => {
    const s = sanitizeThemeState({
      saved: { preset: "batman", custom: DEFAULT_COLORS, customMotif: "bat" },
      history: [{ preset: "gojo" }],
      userThemes: [{ id: "1", name: "Mine", colors: { primary: "#123456" }, motif: "flame" }],
    });
    expect(s.saved).toEqual({ preset: "error-default", custom: DEFAULT_COLORS });
    expect(s.history).toEqual([]);
    expect(s.userThemes[0]).toEqual({ id: "1", name: "Mine", colors: expect.objectContaining({ primary: "#123456" }) });
  });
});

describe("readable text on colours", () => {
  it("uses white on dark colours and dark text on light colours", () => {
    expect(readableOn("#2563EB")).toBe("#FFFFFF");
    expect(readableOn("#FBBF24")).toBe("#0B1020");
  });
  it("primary button text stays white on the default and Arctic Ice gradients, dark on Amber Gold", () => {
    const on = (id: string) => themeVars(THEME_PRESETS.find((p) => p.id === id)!.colors)["--theme-on-primary"];
    expect(on("error-default")).toBe("#FFFFFF");
    expect(on("arctic-ice")).toBe("#FFFFFF");
    expect(on("amber-gold")).toBe("#0B1020");
  });
});
