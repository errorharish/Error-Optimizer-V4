import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { DEFAULT_SETTINGS, SETTINGS_INDEX, sanitizeSettings } from "@/lib/eo/settings";
import { PAGE_COMPONENTS } from "@/components/eo/pageMap";
import { PAGES } from "@/lib/eo/registry";

describe("Lightweight rendering", () => {
  it("is off by default so nobody is switched to a lighter look", () => {
    expect(DEFAULT_SETTINGS.lightweight).toBe(false);
  });
  it("survives a save/load round trip and rejects junk", () => {
    expect(sanitizeSettings({ lightweight: true }).lightweight).toBe(true);
    expect(sanitizeSettings({ lightweight: "yes" }).lightweight).toBe(false);
  });
  it("is searchable under Animations & Motion", () => {
    expect(SETTINGS_INDEX.find((e) => e.id === "fx-lite")?.category).toBe("effects");
  });
  it("never touches the status indicator breathing", () => {
    const css = readFileSync("src/styles.css", "utf8");
    const lite = css.slice(css.indexOf("/* ── Lightweight rendering"));
    expect(lite).not.toMatch(/eo-st/);
  });
});

describe("Page map", () => {
  it("has a component for every registry page", () => {
    for (const p of PAGES) expect(PAGE_COMPONENTS[p.slug], p.slug).toBeTypeOf("function");
  });
});
