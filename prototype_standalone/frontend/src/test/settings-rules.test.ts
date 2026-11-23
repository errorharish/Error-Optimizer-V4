import { describe, expect, it } from "vitest";
import {
  DEFAULT_SETTINGS,
  SETTINGS_CATEGORIES,
  needsSafetyConfirm,
  restoreDefaults,
  safetyStatus,
  sanitizeSettings,
  searchSettings,
} from "@/lib/eo/settings";

describe("control center categories", () => {
  it("has the eight requested categories in order", () => {
    expect(SETTINGS_CATEGORIES.map((c) => c.label)).toEqual([
      "Overview", "Appearance & Themes", "Animations & Motion", "Performance & Telemetry",
      "Startup & Automation", "Safety & Recovery", "Logs & Diagnostics", "System Information",
    ]);
  });
});

describe("defaults", () => {
  it("keep every safeguard on, Auto mode, 3 s updates and 7-day logs", () => {
    expect(safetyStatus(DEFAULT_SETTINGS)).toBe("protected");
    expect(DEFAULT_SETTINGS.performanceMode).toBe("auto");
    expect(DEFAULT_SETTINGS.monitorIntervalSec).toBe(3);
    expect(DEFAULT_SETTINGS.logRetentionDays).toBe(7);
    expect(DEFAULT_SETTINGS.startWithWindows).toBe(false);
  });
  it("Restore Defaults resets preferences but keeps the last used suite", () => {
    const s = restoreDefaults({ ...DEFAULT_SETTINGS, confirmHigh: false, lastSuite: "V4" });
    expect(s.confirmHigh).toBe(true);
    expect(s.lastSuite).toBe("V4");
  });
  it("bad saved values fall back to defaults", () => {
    const s = sanitizeSettings({ monitorIntervalSec: 4, logRetentionDays: 30, performanceMode: "turbo", confirmMedium: false });
    expect(s.monitorIntervalSec).toBe(3);
    expect(s.logRetentionDays).toBe(30);
    expect(s.performanceMode).toBe("auto");
    expect(s.confirmMedium).toBe(false);
  });
});

describe("safety", () => {
  it("turning a safeguard off needs confirmation, turning it on does not", () => {
    expect(needsSafetyConfirm("confirmHigh", false)).toBe(true);
    expect(needsSafetyConfirm("autoBackup", true)).toBe(false);
  });
  it("any safeguard off reduces protection", () => {
    expect(safetyStatus({ ...DEFAULT_SETTINGS, autoBackup: false })).toBe("reduced");
  });
});

describe("settings search", () => {
  it("finds settings by name and opens their category", () => {
    expect(searchSettings("retention")[0]).toMatchObject({ id: "logs-retention", category: "logs" });
    expect(searchSettings("graphite")[0]?.category).toBe("appearance");
    expect(searchSettings("gojo")).toEqual([]);
    expect(searchSettings("cursor")).toEqual([]);
  });
  it("finds settings by category name", () => {
    expect(searchSettings("safety recovery").map((e) => e.category)).toContain("safety");
  });
  it("empty search shows nothing", () => {
    expect(searchSettings("  ")).toEqual([]);
  });
});
