import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

// Status indicators must look identical in every theme: their block of styles.css may not read
// any theme colour or theme-derived token.
const css = readFileSync(resolve(__dirname, "../styles.css"), "utf8");
const start = css.indexOf("/* ── Shared status indicators");
const end = css.indexOf("/* Details popup (header service", start);
const block = css.slice(start, end);

describe("status indicators are theme-independent", () => {
  it("finds the status block", () => {
    expect(start).toBeGreaterThan(-1);
    expect(end).toBeGreaterThan(start);
  });

  it("reads no theme variable or theme-derived token", () => {
    const banned = /var\(--(theme-|cyan|primary|accent|foreground|muted|subtle|border|window|background|surface|font-sans|btn-|ease-)/g;
    expect(block.match(banned) ?? []).toEqual([]);
  });

  it("uses the fixed green, amber and red palette", () => {
    expect(block).toContain("--status-pos: #22c55e;");
    expect(block).toContain("--status-warn: #f59e0b;");
    expect(block).toContain("--status-neg: #ef4444;");
  });
});
