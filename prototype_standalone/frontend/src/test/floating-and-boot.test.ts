import { describe, expect, it } from "vitest";
import { placeFloating } from "@/lib/eo/floatingPlacement";
import { BOOT_ROUTE_FN } from "@/components/eo/startup";

const rect = (left: number, top: number, w: number, h: number) => ({
  left,
  top,
  width: w,
  height: h,
  right: left + w,
  bottom: top + h,
});
const vp = { width: 1208, height: 690 };
const tip = { width: 280, height: 180 };

describe("status tooltip placement", () => {
  it("opens below the status, right-aligned, when there is room", () => {
    const p = placeFloating(rect(600, 100, 120, 28), tip, vp, "end");
    expect(p.side).toBe("bottom");
    expect(p.top).toBe(136);
    expect(p.left).toBe(440);
  });

  it("flips above near the bottom edge instead of covering the status", () => {
    const t = rect(600, 600, 120, 28);
    const p = placeFloating(t, tip, vp, "end");
    expect(p.side).toBe("top");
    expect(p.top + tip.height).toBeLessThanOrEqual(t.top);
  });

  it("stays inside the window at the left and right edges", () => {
    expect(placeFloating(rect(4, 100, 100, 28), tip, vp, "end").left).toBe(8);
    expect(placeFloating(rect(1100, 100, 100, 28), tip, vp, "start").left).toBe(1208 - 280 - 8);
  });
});

const route = new Function(`return ${BOOT_ROUTE_FN}`)() as (
  path: string,
  last: string | null,
) => { view: string; suite: string };

describe("startup skeleton picks the first route's layout", () => {
  it("V3 Dashboard on /", () => expect(route("/", null)).toEqual({ view: "dash", suite: "V3" }));
  it("V4 Dashboard", () =>
    expect(route("/v4-dashboard", "V3")).toEqual({ view: "v4dash", suite: "V4" }));
  it("V4 pages use the V4 dock", () =>
    expect(route("/emulator-debloater/", null)).toEqual({ view: "page", suite: "V4" }));
  it("V3 pages use the V3 dock", () =>
    expect(route("/startup-manager", "V4")).toEqual({ view: "page", suite: "V3" }));
  it("shared pages keep the remembered suite", () => {
    expect(route("/settings", "V4")).toEqual({ view: "settings", suite: "V4" });
    expect(route("/about", "V3")).toEqual({ view: "page", suite: "V3" });
  });
});
