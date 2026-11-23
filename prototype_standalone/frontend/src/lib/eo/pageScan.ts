// Page scan store for optimization pages: one automatic scan when a page opens, REFRESH repeats
// it. Pure module (no React) so the rules are unit-tested:
// - a page scans automatically only when it has no result yet (idle or failed);
// - a finished result is cached for the session, so revisiting a page or re-rendering never
//   starts another scan;
// - only one scan per page runs at a time — a second request joins the running one;
// - REFRESH (force) always repeats the scan and keeps the previous result visible meanwhile;
// - a page that closes mid-scan abandons it, and the late answer is ignored.
export type PageScanPhase = "idle" | "scanning" | "done" | "error";

export interface PageScanState {
  phase: PageScanPhase;
  /** Error text from the last failed scan (null otherwise). */
  error: string | null;
  /** Local time of the last successful scan. */
  at: string | null;
  /** Number of successful scans this session; > 0 means results are available. */
  runs: number;
}

export const IDLE_SCAN: PageScanState = Object.freeze({
  phase: "idle",
  error: null,
  at: null,
  runs: 0,
}) as PageScanState;

const states = new Map<string, PageScanState>();
const inflight = new Map<string, { gen: number; before: PageScanState; promise: Promise<void> }>();
const listeners = new Set<() => void>();
let generation = 0;

const emit = () => listeners.forEach((l) => l());
const set = (key: string, s: PageScanState) => {
  states.set(key, s);
  emit();
};

export const getPageScan = (key: string): PageScanState => states.get(key) ?? IDLE_SCAN;

export function subscribePageScan(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** True when opening the page should start its scan automatically. */
export const needsAutoScan = (s: PageScanState) => s.phase === "idle" || s.phase === "error";

export interface RunOptions {
  /** REFRESH: repeat even when a cached result exists. */
  force?: boolean;
}

/**
 * Starts (or joins) the scan for `key`. Resolves with the generation that ran, or null when
 * nothing was started because a cached result was kept.
 */
export function runPageScan(
  key: string,
  task: () => unknown,
  { force = false }: RunOptions = {},
): { gen: number | null; promise: Promise<void> } {
  const running = inflight.get(key);
  if (running) return { gen: running.gen, promise: running.promise };
  const before = getPageScan(key);
  if (!force && !needsAutoScan(before)) return { gen: null, promise: Promise.resolve() };

  const gen = ++generation;
  set(key, { ...before, phase: "scanning", error: null });
  const promise = (async () => {
    try {
      await task();
      if (inflight.get(key)?.gen !== gen) return; // abandoned
      set(key, {
        phase: "done",
        error: null,
        at: new Date().toLocaleTimeString(),
        runs: before.runs + 1,
      });
    } catch (e) {
      if (inflight.get(key)?.gen !== gen) return;
      const msg = e instanceof Error && e.message ? e.message : "The scan did not finish.";
      set(key, { ...before, phase: "error", error: msg });
    } finally {
      if (inflight.get(key)?.gen === gen) inflight.delete(key);
    }
  })();
  inflight.set(key, { gen, before, promise });
  return { gen, promise };
}

/** Drops a running scan (the page closed); the page keeps its previous result or none. */
export function abandonPageScan(key: string, gen: number) {
  const running = inflight.get(key);
  if (!running || running.gen !== gen) return;
  inflight.delete(key);
  set(key, running.before);
}

/** Test helper: forget every cached scan. */
export function resetPageScans() {
  states.clear();
  inflight.clear();
  emit();
}
