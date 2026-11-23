import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { StartupSkeleton } from "@/components/eo/StartupSkeleton";
import {
  onSettingsCategoryRequest,
  requestSettingsCategory,
  takeSettingsCategoryRequest,
} from "@/components/eo/settingsStore";

describe("Appearance shortcut", () => {
  it("hands the Appearance request to Settings exactly once", () => {
    const seen: string[] = [];
    const off = onSettingsCategoryRequest((c) => seen.push(c));
    requestSettingsCategory("appearance");
    expect(seen).toEqual(["appearance"]);
    expect(takeSettingsCategoryRequest()).toBe("appearance");
    expect(takeSettingsCategoryRequest()).toBeNull();
    off();
  });
});

describe("startup skeleton", () => {
  it("shows no loading, scanning or progress text", () => {
    const { container } = render(<StartupSkeleton />);
    // The only text outside the hidden failure card is the brand name in the header.
    const clone = container.cloneNode(true) as HTMLElement;
    clone.querySelector(".eob-status")?.remove();
    expect(clone.textContent?.trim()).toBe("ERROR OPTIMIZER");
    expect(container.textContent).not.toMatch(/loading UI|rendering|scanning|initializ|connecting|preparing|\d+%/i);
  });

  it("keeps the failure card hidden unless startup stalls", () => {
    const css = readFileSync("src/components/eo/startup.ts", "utf8");
    expect(css).toMatch(/\.eob-status\{[^}]*display:none/);
    expect(css).toContain('html[${BOOT_ATTR}="slow"] .eob-status{display:flex}');
  });
});

describe("LIVE PREVIEW button", () => {
  it("never uses pressed styling, so ON and OFF look the same", () => {
    const src = readFileSync("src/components/eo/ThemeSettings.tsx", "utf8");
    const fn = src.slice(src.indexOf("function LivePreviewButton"), src.indexOf("function ColorField"));
    expect(fn).not.toMatch(/pressed=/);
    expect(fn).toContain("eo-brand-lock");
  });
});
