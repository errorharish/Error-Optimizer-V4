import { describe, expect, it } from "vitest";
import { PAGES } from "@/lib/eo/registry";
import { groupResults, highlightSegments, pushRecent, searchPages, SEARCH_ENTRIES } from "@/lib/eo/smartSearch";

const titles = (q: string) => searchPages(q).map((r) => r.entry.title);
const slugs = (q: string) => searchPages(q).map((r) => r.entry.slug);

describe("smart search", () => {
  it("only offers pages that exist in the registry", () => {
    const real = new Set(PAGES.map((p) => p.slug));
    for (const e of SEARCH_ENTRIES) expect(real.has(e.slug)).toBe(true);
  });

  it("matches from the first character", () => {
    expect(searchPages("r").length).toBeGreaterThan(0);
    expect(searchPages("e").length).toBeGreaterThan(0);
  });

  it("ram finds the RAM Limiter (AI Optimization) and RAM pages", () => {
    const s = slugs("ram");
    expect(s).toContain("ai-optimization");
    expect(s).toContain("registry-values-ram");
    expect(s.slice(0, 2)).toContain("ai-optimization");
    expect(searchPages("ram").find((r) => r.entry.slug === "ai-optimization")?.matchedKeyword).toBe("ram limiter");
  });

  it("input → Input Optimizer first", () => expect(titles("input")[0]).toBe("Input Optimizer"));

  it("emulator → all three emulator pages", () => {
    expect(slugs("emulator").slice(0, 3).sort()).toEqual(
      ["emulator-advanced", "emulator-debloater", "emulator-optimization"].sort(),
    );
  });

  it("storage, network, registry, power and cpu land on the right pages", () => {
    expect(titles("storage")[0]).toBe("Storage Optimizer");
    expect(titles("network")[0]).toBe("Network Boost");
    expect(titles("registry")[0]).toBe("Registry Intelligence");
    expect(slugs("power")[0]).toBe("ai-optimization");
    expect(searchPages("power")[0]!.matchedKeyword).toBe("power plan optimization");
    expect(slugs("cpu")[0]).toBe("performance-tuning");
    expect(slugs("cpu")).toEqual(expect.arrayContaining(["performance-tuning", "system-info", "v4-max-performance"]));
  });

  it("more letters refine the results", () => {
    expect(searchPages("emulator deb").map((r) => r.entry.slug)).toEqual(["emulator-debloater"]);
    expect(searchPages("re").length).toBeGreaterThan(searchPages("regis").length);
  });

  it("returns nothing for nonsense", () => expect(searchPages("zzqx")).toEqual([]));

  it("optimization pages outrank shared settings pages", () => {
    const r = searchPages("restore");
    expect(r.length).toBeGreaterThan(0);
    const settings = searchPages("s").findIndex((x) => x.entry.group === "Shared");
    const firstOpt = searchPages("s").findIndex((x) => x.entry.group !== "Shared");
    expect(firstOpt).toBeLessThan(settings);
  });

  it("groups by suite with the best match's group first and shared last", () => {
    const g = groupResults(searchPages("emulator"));
    expect(g[0]!.group).toBe("V4");
    const all = groupResults(searchPages("s")).map((x) => x.group);
    expect(all[all.length - 1]).toBe("Shared");
  });

  it("recent list is deduped, capped at 5 and ignores unknown pages", () => {
    let l: string[] = [];
    for (const s of ["network", "storage", "network", "tools", "pro", "ultimate", "debloat"]) l = pushRecent(l, s);
    expect(l).toEqual(["debloat", "ultimate", "pro", "tools", "network"]);
    expect(pushRecent(l, "not-a-page")).toEqual(l);
  });

  it("highlights the typed text", () => {
    expect(highlightSegments("Input Optimizer", "inp")).toEqual([
      { text: "Inp", hit: true },
      { text: "ut Optimizer", hit: false },
    ]);
  });
});
