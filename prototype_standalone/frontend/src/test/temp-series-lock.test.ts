import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

// Temperature series keep their own colours in every theme: CPU cyan, GPU blue, GPU hotspot amber,
// SSD green, declared as fixed values and never read from the theme.
const css = readFileSync(resolve(__dirname, "../styles.css"), "utf8");
const series = css.match(/\.eo-temp-card \[data-series="[a-zA-Z]+"\] \{[^}]*\}/g) ?? [];

describe("temperature series colours are theme-independent", () => {
  it("declares the fixed cyan, blue, amber and green series palette", () => {
    expect(css).toContain("--temp-cpu: #22d3ee;");
    expect(css).toContain("--temp-gpu: #60a5fa;");
    expect(css).toContain("--temp-hotspot: #fbbf24;");
    expect(css).toContain("--temp-ssd: #4ade80;");
  });

  it("maps each series only to its fixed colour", () => {
    expect(series).toHaveLength(4);
    for (const rule of series) expect(rule).toMatch(/--series: var\(--temp-(cpu|gpu|hotspot|ssd)\);/);
  });
});
