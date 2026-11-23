// Score gauge tween maths (pure, no DOM). The gauge number and arc are both driven by the
// value this returns, so they always move together and always land exactly on the real score.

/** Time for a full 0 → 100 sweep; shorter moves (e.g. 82 → 86 after a re-scan) take less. */
export const SCORE_TWEEN_FULL_MS = 1400;
export const SCORE_TWEEN_MIN_MS = 520;

export const clampScore = (v: number) => Math.min(100, Math.max(0, Number.isFinite(v) ? v : 0));

/** Duration grows with the distance travelled (square-root curve so small moves still feel smooth). */
export function scoreTweenDuration(from: number, to: number): number {
  const d = Math.abs(clampScore(to) - clampScore(from)) / 100;
  if (d === 0) return 0;
  return Math.round(SCORE_TWEEN_MIN_MS + (SCORE_TWEEN_FULL_MS - SCORE_TWEEN_MIN_MS) * Math.sqrt(d));
}

/** Gentle start, long soft landing: a blend of ease-in-out and ease-out cubic (both monotonic). */
export function easeScore(t: number): number {
  const x = Math.min(1, Math.max(0, t));
  const inOut = x < 0.5 ? 4 * x * x * x : 1 - Math.pow(-2 * x + 2, 3) / 2;
  const out = 1 - Math.pow(1 - x, 3);
  return 0.35 * inOut + 0.65 * out;
}

/** Value shown at progress t (0..1). t >= 1 returns the exact target — never a rounded guess. */
export function scoreAt(from: number, to: number, t: number): number {
  if (t >= 1) return clampScore(to);
  if (t <= 0) return clampScore(from);
  return clampScore(from + (to - from) * easeScore(t));
}
