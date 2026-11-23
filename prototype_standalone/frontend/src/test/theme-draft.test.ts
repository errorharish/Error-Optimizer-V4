// Custom Theme Studio: edits only change the draft (live preview); the app shows the saved theme
// until Save; Discard brings back the saved theme; invalid drafts are never saved.
import { beforeEach, describe, expect, it, vi } from "vitest";
import { DEFAULT_THEME_STATE, editColor, validateThemeDraft } from "@/lib/eo/theme";

async function freshStore() {
  vi.resetModules();
  return import("@/components/eo/themeStore");
}
const rootPrimary = () => document.documentElement.style.getPropertyValue("--theme-primary");

beforeEach(() => {
  window.localStorage.clear();
  document.documentElement.removeAttribute("style");
});

describe("theme draft vs saved theme", () => {
  it("only the LIVE PREVIEW button turns preview on; edits then recolour the app without saving", async () => {
    const t = await freshStore();
    t.initTheme();
    const before = rootPrimary();
    t.setThemeColor("primary", "#E11D48");
    expect(t.isLivePreviewActive()).toBe(false);
    expect(rootPrimary()).toBe(before);
    t.livePreviewTheme("custom");
    t.setThemeColor("primary", "#E11D48");
    expect(t.isLivePreviewActive()).toBe(true);
    expect(rootPrimary()).toBe("#E11D48");
    expect(t.hasThemeDraft()).toBe(true);
    expect(window.localStorage.getItem(t.THEME_VARS_KEY) ?? "").not.toContain("#E11D48");
  });

  it("Turn off restores the saved theme, ends Live Preview and saves nothing", async () => {
    const t = await freshStore();
    t.initTheme();
    const before = rootPrimary();
    t.startLivePreview();
    t.setThemeColor("primary", "#E11D48");
    t.stopLivePreview();
    expect(t.isLivePreviewActive()).toBe(false);
    expect(rootPrimary()).toBe(before);
    expect(window.localStorage.getItem(t.THEME_VARS_KEY) ?? "").not.toContain("#E11D48");
  });

  it("switching presets, editing colours and saving a custom theme never turn Live Preview off", async () => {
    const t = await freshStore();
    t.initTheme();
    const saved = rootPrimary();
    t.livePreviewTheme("error-default");
    t.previewThemePreset("custom");
    t.setThemeColor("accent", "#22C55E");
    t.setThemeColor("background", "#0B1220");
    t.saveCurrentAsCustomTheme("Mine");
    expect(t.isLivePreviewActive()).toBe(true);
    t.stopLivePreview();
    expect(rootPrimary()).toBe(saved);
  });

  it("a reload starts from the saved theme with Live Preview off", async () => {
    let t = await freshStore();
    t.initTheme();
    t.startLivePreview();
    t.setThemeColor("primary", "#E11D48");
    t = await freshStore();
    t.initTheme();
    expect(t.isLivePreviewActive()).toBe(false);
    expect(t.hasThemeDraft()).toBe(false);
  });

  it("Save applies the draft to the whole app and persists it", async () => {
    const t = await freshStore();
    t.initTheme();
    t.setThemeColor("primary", "#E11D48");
    const r = t.saveThemeDraft();
    expect(r.ok).toBe(true);
    expect(rootPrimary()).toBe("#E11D48");
    expect(t.hasThemeDraft()).toBe(false);
    expect(t.isLivePreviewActive()).toBe(false);
    expect(window.localStorage.getItem(t.THEME_VARS_KEY)).toContain("#E11D48");
  });

  it("Discard restores the saved theme and keeps nothing from the preview", async () => {
    const t = await freshStore();
    t.initTheme();
    const before = rootPrimary();
    t.setThemeColor("primary", "#E11D48");
    t.discardThemeDraft();
    expect(t.hasThemeDraft()).toBe(false);
    expect(rootPrimary()).toBe(before);
    expect(window.localStorage.getItem(t.THEME_VARS_KEY) ?? "").not.toContain("#E11D48");
  });

  it("previewing a preset shows it app-wide but keeps the saved theme in storage", async () => {
    const t = await freshStore();
    t.initTheme();
    t.livePreviewTheme("custom");
    t.setThemeColor("accent", "#22C55E");
    expect(document.documentElement.style.getPropertyValue("--theme-accent")).toBe("#22C55E");
    expect(window.localStorage.getItem(t.THEME_VARS_KEY) ?? "").not.toContain("#22C55E");
  });
});

describe("draft validation", () => {
  it("rejects text that is unreadable on the background", () => {
    let s = editColor(DEFAULT_THEME_STATE, "background", "#050811");
    s = editColor(s, "text", "#0A0F1E");
    expect(validateThemeDraft(s).ok).toBe(false);
  });

  it("accepts the default theme", () => {
    expect(validateThemeDraft(DEFAULT_THEME_STATE).ok).toBe(true);
  });

  it("an unreadable draft is not saved", async () => {
    const t = await freshStore();
    t.initTheme();
    t.startLivePreview();
    t.setThemeColor("text", "#060912");
    const r = t.saveThemeDraft();
    expect(r.ok).toBe(false);
    expect(t.hasThemeDraft()).toBe(true);
    expect(t.isLivePreviewActive()).toBe(true);
  });
});
