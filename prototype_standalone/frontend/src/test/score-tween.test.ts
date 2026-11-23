import { describe, expect, it } from "vitest";
import {
  SCORE_TWEEN_FULL_MS,
  SCORE_TWEEN_MIN_MS,
  easeScore,
  scoreAt,
  scoreTweenDuration,
} from "@/lib/eo/scoreTween";

describe("score gauge animation", () => {
  it("first show counts up from 0 and lands exactly on the calculated score", () => {
    expect(scoreAt(0, 87, 0)).toBe(0);
    expect(scoreAt(0, 87, 1)).toBe(87);
    expect(scoreAt(0, 87, 1.4)).toBe(87);
  });

  it("a new scan result moves from the previous score, not from 0", () => {
    expect(scoreAt(72, 91, 0)).toBe(72);
    const mid = scoreAt(72, 91, 0.5);
    expect(mid).toBeGreaterThan(72);
    expect(mid).toBeLessThan(91);
    expect(scoreAt(72, 91, 1)).toBe(91);
  });

  it("never overshoots the real score while moving", () => {
    for (let t = 0; t <= 1; t += 0.05) {
      const v = scoreAt(0, 64, t);
      expect(v).toBeGreaterThanOrEqual(0);
      expect(v).toBeLessThanOrEqual(64);
    }
  });

  it("eases (not linear) and only ever moves forward", () => {
    expect(easeScore(0)).toBe(0);
    expect(easeScore(1)).toBeCloseTo(1, 10);
    expect(easeScore(0.5)).not.toBeCloseTo(0.5, 2);
    let prev = 0;
    for (let t = 0.01; t <= 1; t += 0.01) {
      const e = easeScore(t);
      expect(e).toBeGreaterThanOrEqual(prev);
      prev = e;
    }
  });

  it("does not animate when the score is unchanged (re-render with the same value)", () => {
    expect(scoreTweenDuration(83, 83)).toBe(0);
  });

  it("a full sweep takes the full time; small re-scan changes are quicker", () => {
    expect(scoreTweenDuration(0, 100)).toBe(SCORE_TWEEN_FULL_MS);
    const small = scoreTweenDuration(82, 86);
    expect(small).toBeGreaterThanOrEqual(SCORE_TWEEN_MIN_MS);
    expect(small).toBeLessThan(scoreTweenDuration(0, 86));
  });
});
