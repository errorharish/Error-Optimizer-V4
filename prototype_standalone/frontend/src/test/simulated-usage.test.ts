import { describe, expect, it } from "vitest";
import { initialSimUsage, nextSimUsage } from "@/lib/eo/simulatedUsage";

describe("simulated system usage", () => {
  it("stays within 0–100% over many steps", () => {
    let s = initialSimUsage();
    for (let i = 0; i < 500; i++) {
      s = nextSimUsage(s);
      for (const v of Object.values(s)) {
        expect(v).toBeGreaterThanOrEqual(0);
        expect(v).toBeLessThanOrEqual(100);
      }
    }
  });
  it("changes values between ticks", () => {
    expect(nextSimUsage({ cpu: 30, gpu: 30, ram: 50 }, () => 1)).not.toEqual({ cpu: 30, gpu: 30, ram: 50 });
  });
});
