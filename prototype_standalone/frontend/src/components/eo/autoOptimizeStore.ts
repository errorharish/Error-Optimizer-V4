// Auto Optimize — scheduler store shared by the Dashboard panel, the Auto Optimize page, and
// the title-bar chip.
import { useEffect, useSyncExternalStore } from "react";
import { toast } from "sonner";
import { hasNativeBackend } from "@/lib/eo/nativeBridge";
import { isBackendConnected, subscribeConnection } from "@/lib/eo/connectionStore";
import { sendHostRequest } from "@/lib/eo/hostRpc";
import { getSettingsSnapshot, setSettings, subscribeSettings } from "./settingsStore";
import {
  DEFAULT_CONFIG,
  STARTUP_DELAY_MIN,
  nextRunAt,
  onScheduleDue,
  planRun,
  sanitizeConfig,
  summarizeOutcomes,
  taskById,
  type AutoConfig,
  type RunSummary,
  type TaskOutcome,
  type Trigger,
} from "@/lib/eo/autoOptimize";

export interface RunItem {
  taskId: string;
  name: string;
  state: "Pending" | "Running" | TaskOutcome;
  note?: string;
}
export interface ActiveRun {
  id: number;
  trigger: Trigger;
  startedAt: number;
  items: RunItem[];
}
export type HistoryEntry =
  | {
      kind: "run";
      id: number;
      at: number;
      trigger: Trigger;
      summary: RunSummary;
      items: RunItem[];
      durationMs: number;
    }
  | {
      kind: "event";
      id: number;
      at: number;
      title: string;
      detail: string;
      tone: "muted" | "warning" | "cyan";
    };

/** on = app open · background = UI closed, simulated agent keeps scheduling ·
 *  app-closed = UI closed, nothing runs · pc-off = Windows shut down, nothing runs. */
export type Power = "on" | "background" | "app-closed" | "pc-off";

export interface AutoState {
  loaded: boolean;
  config: AutoConfig;
  nextAt: number | null;
  /** Remaining time captured when paused, restored on resume. */
  pausedRemaining: number | null;
  lastRunAt: number | null;
  run: ActiveRun | null;
  queued: boolean;
  history: HistoryEntry[];
  power: Power;
  /** Simulated: browsers the prototype pretends are open (their cache is skipped). */
  openBrowsers: string[];
}

const KEY = "eo.autoOptimize.v1";
const STEP_MS = 650;
const HISTORY_MAX = 40;

const INITIAL: AutoState = {
  loaded: false,
  config: DEFAULT_CONFIG,
  nextAt: null,
  pausedRemaining: null,
  lastRunAt: null,
  run: null,
  queued: false,
  history: [],
  power: "on",
  openBrowsers: [],
};

let state: AutoState = INITIAL;

let settingsSubscribed = false;
function ensureSettingsSubscription() {
  if (settingsSubscribed || typeof window === "undefined") return;
  settingsSubscribed = true;
  subscribeSettings(() => {
    const cur = getSettingsSnapshot();
    if (state.loaded && state.config.enabled !== cur.autoOptimizationEnabled) {
      const nextCfg = { ...state.config, enabled: cur.autoOptimizationEnabled };
      set({
        config: nextCfg,
        nextAt: cur.autoOptimizationEnabled && !state.config.paused ? nextRunAt(Date.now(), nextCfg.intervalMin) : null,
        pausedRemaining: !cur.autoOptimizationEnabled ? null : state.pausedRemaining,
        queued: false,
      });
    }
  });
}
const listeners = new Set<() => void>();
let seq = 1;
let stepTimer: ReturnType<typeof setTimeout> | null = null;
let ticker: ReturnType<typeof setInterval> | null = null;

function set(patch: Partial<AutoState> | ((s: AutoState) => Partial<AutoState>)) {
  state = { ...state, ...(typeof patch === "function" ? patch(state) : patch) };
  if (state.loaded) {
    try {
      const { config, nextAt, pausedRemaining, lastRunAt, history, openBrowsers } = state;
      window.localStorage.setItem(
        KEY,
        JSON.stringify({ config, nextAt, pausedRemaining, lastRunAt, history, openBrowsers }),
      );
    } catch {
      /* storage unavailable — keep in memory */
    }
  }
  listeners.forEach((l) => l());
}

const subscribe = (l: () => void) => {
  listeners.add(l);
  return () => listeners.delete(l);
};

function addHistory(e: HistoryEntry) {
  set((s) => ({ history: [e, ...s.history].slice(0, HISTORY_MAX) }));
}
function event(title: string, detail: string, tone: "muted" | "warning" | "cyan" = "muted") {
  addHistory({ kind: "event", id: seq++, at: Date.now(), title, detail, tone });
}

const scheduling = (s: AutoState) =>
  s.config.enabled && !s.config.paused && (s.power === "on" || s.power === "background");

function mapBackendHistoryEntry(entry: Record<string, unknown>, fallbackId: number): HistoryEntry {
  const rawTasks = Array.isArray(entry["tasks"]) ? entry["tasks"] : [];
  const items: RunItem[] = rawTasks.map((t) => {
    const o = (t && typeof t === "object" ? t : {}) as Record<string, unknown>;
    const id = typeof o["id"] === "string" ? o["id"] : "task";
    const meta = taskById(id);
    const outStr = typeof o["outcome"] === "string" ? o["outcome"] : "Completed";
    const outcome: TaskOutcome =
      outStr === "Skipped"
        ? "Skipped"
        : outStr === "Cancelled"
          ? "Cancelled"
          : outStr === "Failed"
            ? "Skipped"
            : "Completed";
    const rawBytes = o["reclaimedBytes"];
    const bytes = typeof rawBytes === "number" && rawBytes > 0
      ? `${(rawBytes / (1024 * 1024)).toFixed(1)} MB freed`
      : "";
    const reason = typeof o["reason"] === "string" ? o["reason"] : "";
    const noteText = [reason, bytes].filter(Boolean).join(" · ");
    return {
      taskId: id,
      name: meta?.name ?? id,
      state: outcome,
      ...(noteText ? { note: noteText } : {}),
    };
  });
  const summary = summarizeOutcomes(items.map((i) => i.state as TaskOutcome));
  const rawTs = entry["timestampUtc"];
  const atMs = typeof rawTs === "string" ? Date.parse(rawTs) : Date.now();
  const rawTrig = entry["trigger"];
  const rawDur = entry["durationMs"];
  return {
    kind: "run",
    id: fallbackId,
    at: Number.isFinite(atMs) ? atMs : Date.now(),
    trigger: (typeof rawTrig === "string" ? rawTrig : "Manual") as Trigger,
    summary,
    items,
    durationMs: typeof rawDur === "number" ? rawDur : 0,
  };
}

function applyBackendSnapshot(snap: unknown) {
  if (!snap || typeof snap !== "object") return;
  const s = snap as Record<string, unknown>;
  const config = sanitizeConfig((s["config"] as Partial<AutoConfig>) ?? state.config);
  const rawHist = Array.isArray(s["history"]) ? s["history"] : [];
  const mappedHist: HistoryEntry[] = rawHist
    .map((h, i) => mapBackendHistoryEntry((h as Record<string, unknown>) ?? {}, rawHist.length - i))
    .slice(0, HISTORY_MAX);
  seq = Math.max(seq, mappedHist.reduce((m, h) => Math.max(m, h.id), 0) + 1);
  const nextRaw = s["nextRunUtc"];
  const lastRaw = s["lastRunUtc"];
  const nextAtMs = typeof nextRaw === "string" ? Date.parse(nextRaw) : null;
  const lastAtMs = typeof lastRaw === "string" ? Date.parse(lastRaw) : null;
  set({
    loaded: true,
    config,
    nextAt: nextAtMs && Number.isFinite(nextAtMs) ? nextAtMs : (config.enabled && !config.paused ? state.nextAt : null),
    lastRunAt: lastAtMs && Number.isFinite(lastAtMs) ? lastAtMs : state.lastRunAt,
    queued: Boolean(s["queued"]),
    history: mappedHist.length > 0 || rawHist.length === 0 ? mappedHist : state.history,
  });
}

async function refreshBackendState() {
  if (!hasNativeBackend()) return;
  try {
    const snap = await sendHostRequest("auto:get-state", undefined, 15000);
    applyBackendSnapshot(snap);
  } catch {
    /* keep current state cleanly if service is temporarily restarting */
  }
}

/* ── Run engine ── */
async function startRun(trigger: Trigger) {
  if (!hasNativeBackend()) {
    toast.info("Backend connection required", {
      description: "Scheduled and manual Auto Optimize runs require the ERROR OPTIMIZER background service.",
    });
    return;
  }
  if (state.run) return;
  const plan = planRun(state.config, trigger, new Set(state.openBrowsers));
  if (!plan.length) {
    event("Run skipped", "No maintenance tasks are selected.", "warning");
    return;
  }
  const runId = seq++;
  const startedAt = Date.now();
  const pendingItems: RunItem[] = plan.map((p) =>
    p.run
      ? { taskId: p.task.id, name: p.task.name, state: "Running" }
      : { taskId: p.task.id, name: p.task.name, state: "Skipped", note: p.reason ?? "Skipped" },
  );
  set({ run: { id: runId, trigger, startedAt, items: pendingItems } });

  try {
    const res = (await sendHostRequest(
      "auto:run-now",
      {
        trigger,
        tasks: state.config.tasks,
        recycleConfirmed: state.config.recycleConfirm,
      },
      120000,
    )) as { entry?: Record<string, unknown>; state?: unknown };

    if (res?.state) applyBackendSnapshot(res.state);

    const mapped = res?.entry
      ? mapBackendHistoryEntry(res.entry, runId)
      : {
          kind: "run" as const,
          id: runId,
          at: Date.now(),
          trigger,
          summary: summarizeOutcomes(pendingItems.map(() => "Completed")),
          items: pendingItems.map((i) => ({ ...i, state: "Completed" as const })),
          durationMs: Date.now() - startedAt,
        };

    set({ run: null, queued: false, lastRunAt: Date.now() });
    if (mapped.kind === "run") {
      if (!res?.state) addHistory(mapped);
      toast(`Auto Optimize · ${mapped.summary.status}`, {
        description: `${mapped.summary.completed} completed · ${mapped.summary.skipped} skipped.`,
      });
    }
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    set({ run: null, queued: false });
    event("Auto Optimize run failed", msg, "warning");
    toast.error("Auto Optimize failed", { description: msg });
  }
}

function finish(cancelReason?: string) {
  if (stepTimer) clearTimeout(stepTimer);
  stepTimer = null;
  if (hasNativeBackend()) {
    void sendHostRequest("auto:cancel", undefined, 10000).then((snap) => {
      applyBackendSnapshot(snap);
    }).catch(() => undefined);
  }
  const run = state.run;
  if (!run) return;
  const items: RunItem[] = run.items.map((i) =>
    i.state === "Running"
      ? { ...i, state: "Cancelled", note: cancelReason ?? "Cancelled" }
      : i.state === "Pending"
        ? { ...i, state: "Not run", note: cancelReason ?? "Cancelled before it started" }
        : i,
  );
  const summary = summarizeOutcomes(items.map((i) => i.state as TaskOutcome));
  set({ run: null, queued: false });
  addHistory({
    kind: "run",
    id: run.id,
    at: Date.now(),
    trigger: run.trigger,
    summary,
    items,
    durationMs: Date.now() - run.startedAt,
  });
}

let lastPollAt = 0;
function tick() {
  if (!hasNativeBackend()) return;
  const s = state;
  if (scheduling(s) && s.nextAt !== null && Date.now() >= s.nextAt && !s.run) {
    // Allow backend worker (15s tick) to fire scheduled run first without colliding; fall back after 20s grace.
    if (Date.now() < s.nextAt + 20_000) {
      if (Date.now() - lastPollAt >= 3000) {
        lastPollAt = Date.now();
        void refreshBackendState();
      }
      return;
    }
    const decision = onScheduleDue(!!s.run, s.queued, s.config.overlap);
    set({ nextAt: nextRunAt(Date.now(), s.config.intervalMin) });
    if (decision === "start") {
      void startRun("Schedule");
      return;
    }
    if (decision === "queue") set({ queued: true });
  }
  if (Date.now() - lastPollAt >= 30000) {
    lastPollAt = Date.now();
    void refreshBackendState();
  }
}

/* ── Boot: load persisted state once per page load (= "application launch") ── */
let connSubAttached = false;
function boot() {
  if (!connSubAttached && typeof window !== "undefined") {
    connSubAttached = true;
    subscribeConnection(() => {
      if (isBackendConnected()) {
        lastPollAt = Date.now();
        void refreshBackendState();
      }
    });
  }
  if (state.loaded || typeof window === "undefined") return;
  let saved: Partial<AutoState> & { config?: Partial<AutoConfig> } = {};
  try {
    saved = JSON.parse(window.localStorage.getItem(KEY) ?? "{}");
  } catch {
    saved = {};
  }
  const config = sanitizeConfig(saved.config);
  const history = Array.isArray(saved.history) ? saved.history.slice(0, HISTORY_MAX) : [];
  seq = history.reduce((m, h) => Math.max(m, h.id), 0) + 1;
  state = {
    ...INITIAL,
    loaded: true,
    config,
    history,
    lastRunAt: typeof saved.lastRunAt === "number" ? saved.lastRunAt : null,
    pausedRemaining: typeof saved.pausedRemaining === "number" ? saved.pausedRemaining : null,
    nextAt: typeof saved.nextAt === "number" ? saved.nextAt : null,
    openBrowsers: Array.isArray(saved.openBrowsers) ? saved.openBrowsers : [],
  };
  listeners.forEach((l) => l());
  if (!ticker) ticker = setInterval(tick, 1000);
  if (hasNativeBackend()) {
    lastPollAt = Date.now();
    void refreshBackendState();
  }
}

/* ── Public actions ── */
export const autoOptimize = {
  saveConfig(next: AutoConfig) {
    const prev = state.config;
    const cfg = sanitizeConfig(next);
    const resched =
      cfg.enabled &&
      !cfg.paused &&
      (!prev.enabled || prev.intervalMin !== cfg.intervalMin || prev.paused);
    set({
      config: cfg,
      nextAt:
        cfg.enabled && !cfg.paused
          ? resched
            ? nextRunAt(Date.now(), cfg.intervalMin)
            : state.nextAt
          : null,
      pausedRemaining: cfg.paused ? state.pausedRemaining : null,
    });
    if (hasNativeBackend()) {
      void sendHostRequest("auto:save-config", cfg, 15000).then(applyBackendSnapshot).catch(() => undefined);
    }
    event(
      "Configuration saved",
      `${cfg.tasks.length} tasks · interval ${cfg.intervalMin} min.`,
      "cyan",
    );
  },
  /** Starts the schedule only (never runs maintenance now). Returns false when it cannot start. */
  enable(): boolean {
    if (!state.config.tasks.length) {
      toast.error("Auto Optimize could not be enabled", {
        description: "Select at least one maintenance task first.",
      });
      return false;
    }
    const nextCfg = { ...state.config, enabled: true, paused: false };
    setSettings({ autoOptimizationEnabled: true });
    set({
      config: nextCfg,
      nextAt: nextRunAt(Date.now(), nextCfg.intervalMin),
      pausedRemaining: null,
    });
    if (hasNativeBackend()) {
      void sendHostRequest("auto:save-config", nextCfg, 15000).then(applyBackendSnapshot).catch(() => undefined);
    }
    event(
      "Auto Optimize enabled",
      hasNativeBackend()
        ? "Schedule started."
        : "Schedule armed — waiting for background service connection.",
      "cyan",
    );
    return true;
  },
  disable() {
    const nextCfg = { ...state.config, enabled: false, paused: false };
    setSettings({ autoOptimizationEnabled: false });
    set({
      config: nextCfg,
      nextAt: null,
      pausedRemaining: null,
      queued: false,
    });
    if (hasNativeBackend()) {
      void sendHostRequest("auto:save-config", nextCfg, 15000).then(applyBackendSnapshot).catch(() => undefined);
    }
    event("Auto Optimize disabled", "No further maintenance is scheduled.");
  },
  pause() {
    set((s) => ({
      config: { ...s.config, paused: true },
      pausedRemaining: s.nextAt ? Math.max(0, s.nextAt - Date.now()) : null,
      nextAt: null,
    }));
    if (hasNativeBackend()) {
      void sendHostRequest("auto:pause", { paused: true }, 15000).then(applyBackendSnapshot).catch(() => undefined);
    }
    event("Schedule paused", "The countdown is held until you resume.");
  },
  resume() {
    set((s) => ({
      config: { ...s.config, paused: false },
      nextAt: Date.now() + (s.pausedRemaining ?? s.config.intervalMin * 60_000),
      pausedRemaining: null,
    }));
    if (hasNativeBackend()) {
      void sendHostRequest("auto:pause", { paused: false }, 15000).then(applyBackendSnapshot).catch(() => undefined);
    }
    event("Schedule resumed", "The countdown continues from where it was paused.");
  },
  runNow() {
    void startRun("Manual");
  },
  cancel() {
    finish("Cancelled by you");
  },
  clearHistory() {
    set({ history: [] });
    if (hasNativeBackend()) {
      void sendHostRequest("auto:clear-history", undefined, 15000).then(applyBackendSnapshot).catch(() => undefined);
    }
  },
  setOpenBrowsers(list: string[]) {
    set({ openBrowsers: list });
  },
  /* Lifecycle simulation — these do not close the tab or touch Windows. */
  simulateLaunch() {
    set({ power: "on" });
    const c = state.config;
    if (c.enabled && !c.paused) {
      set({ nextAt: nextRunAt(Date.now(), c.intervalMin) });
      if (c.runOnLaunch) startRun("App launch");
    }
    event(
      "Application launched",
      c.enabled
        ? c.runOnLaunch
          ? "Schedule restored; launch maintenance started."
          : "Schedule restored."
        : "Auto Optimize is disabled.",
      "cyan",
    );
  },
  simulateClose() {
    const c = state.config;
    if (c.closeBehavior === "background" && c.enabled) {
      set({ power: "background" });
      event(
        "Application closed",
        "Background agent keeps the schedule running.",
        "cyan",
      );
      return;
    }
    if (c.closeBehavior === "exit-run" && c.enabled && !state.run) startRun("App exit");
    set({ power: "app-closed" });
    event(
      "Application closed",
      c.closeBehavior === "exit-run" && c.enabled
        ? "Exit maintenance runs once, then scheduling stops until the next launch."
        : "Scheduling stops until the next launch.",
    );
  },
  simulateWindowsStartup() {
    const c = state.config;
    set({ power: c.runOnWindowsStartup ? "background" : "pc-off" });
    if (!c.runOnWindowsStartup) {
      event(
        "Windows started",
        "Windows Startup integration is off — Auto Optimize waits until you open the app.",
      );
      return;
    }
    if (c.enabled && !c.paused)
      set({
        nextAt:
          c.startupPolicy === "after-delay"
            ? Date.now() + STARTUP_DELAY_MIN * 60_000
            : nextRunAt(Date.now(), c.intervalMin),
      });
    event(
      "Windows started",
      c.startupPolicy === "after-delay"
        ? `Background agent started; first maintenance ${STARTUP_DELAY_MIN} minutes after sign-in.`
        : "Background agent started; first maintenance at the next interval.",
      "cyan",
    );
  },
  simulateShutdown() {
    if (state.run) finish("Stopped for Windows shutdown — shutdown is never delayed");
    set({ power: "pc-off", queued: false });
    event(
      "Windows shut down",
      "Nothing runs while the PC is off. The schedule resumes after the next Windows startup.",
    );
  },
};

/** Mount once (in the Shell) so the scheduler starts on page load, whichever page opens first. */
export function useAutoOptimizeBoot() {
  useEffect(() => {
    boot();
    return undefined;
  }, []);
}

export function useAutoOptimize(): AutoState {
  useEffect(() => boot(), []);
  return useSyncExternalStore(
    subscribe,
    () => state,
    () => INITIAL,
  );
}

/* ── 1-second clock for countdowns (only subscribed components re-render) ── */
const clockListeners = new Set<() => void>();
let clock: ReturnType<typeof setInterval> | null = null;
let nowSec = typeof window === "undefined" ? 0 : Math.floor(Date.now() / 1000);
function subscribeClock(l: () => void) {
  clockListeners.add(l);
  if (!clock) {
    const fresh = Math.floor(Date.now() / 1000);
    if (fresh !== nowSec) {
      nowSec = fresh;
      queueMicrotask(l);
    }
    clock = setInterval(() => {
      nowSec = Math.floor(Date.now() / 1000);
      clockListeners.forEach((f) => f());
    }, 1000);
  }
  return () => {
    clockListeners.delete(l);
    if (!clockListeners.size && clock) {
      clearInterval(clock);
      clock = null;
    }
  };
}
/** Current time in ms, refreshed every second; 0 during server render. */
export function useNow() {
  const s = useSyncExternalStore(
    subscribeClock,
    () => nowSec,
    () => 0,
  );
  return s * 1000;
}
