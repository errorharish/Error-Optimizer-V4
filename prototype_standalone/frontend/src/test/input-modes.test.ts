import { describe, expect, it } from "vitest";
import {
  INPUT_ITEMS,
  exclusionReason,
  itemsForMode,
  modeSelection,
  parseInputMode,
} from "@/lib/eo/inputModes";
import { isDefaultEligible } from "@/lib/eo/selection";

describe("Input Optimization modes", () => {
  it("Normal selects every applicable, recommended normal item", () => {
    const normal = modeSelection("NORMAL");
    expect(normal.has("kb-repeat-delay")).toBe(true);
    expect(normal.has("kb-repeat-rate")).toBe(true);
    expect(normal.has("mouse-hover")).toBe(true);
    expect(normal.has("mouse-cursor-shadow")).toBe(true);
    expect(normal.has("mouse-sensitivity-cal")).toBe(true);
    expect(normal.has("mouse-snap-default")).toBe(true);
  });

  it("Advanced adds the recommended advanced items to the normal set, including high-risk eligible items", () => {
    const adv = modeSelection("ADVANCED");
    for (const id of modeSelection("NORMAL")) expect(adv.has(id)).toBe(true);
    expect(adv.has("kb-queue")).toBe(true);
    expect(adv.has("timer-request")).toBe(true);
    expect(adv.has("usb-suspend")).toBe(true);
    expect(adv.has("csrss-priority")).toBe(true);
    expect(adv.has("kb-debounce-reduce")).toBe(true);
    expect(adv.has("queue-mouhid-length")).toBe(true);
  });

  it("never pre-selects not applicable or already-applied items", () => {
    const adv = modeSelection("ADVANCED");
    expect(adv.has("ps2-queue")).toBe(false);
    expect(adv.has("mouse-accel")).toBe(false);
    expect(exclusionReason(INPUT_ITEMS.find((i) => i.id === "ps2-queue")!)).toBe("Not applicable");
  });

  it("Normal shows only normal items", () => {
    expect(itemsForMode("NORMAL").every((i) => i.group === "normal")).toBe(true);
    expect(itemsForMode("ADVANCED")).toHaveLength(INPUT_ITEMS.length);
  });

  it("falls back to Normal for unknown saved modes", () => {
    expect(parseInputMode("ADVANCED")).toBe("ADVANCED");
    expect(parseInputMode("bogus")).toBe("NORMAL");
  });

  it("not-recommended items are excluded from defaults everywhere", () => {
    expect(isDefaultEligible({ id: "x", risk: "LOW", recommended: false })).toBe(false);
    expect(isDefaultEligible({ id: "x", risk: "LOW", incompatible: true })).toBe(false);
    expect(isDefaultEligible({ id: "x", risk: "LOW" })).toBe(true);
  });
});
