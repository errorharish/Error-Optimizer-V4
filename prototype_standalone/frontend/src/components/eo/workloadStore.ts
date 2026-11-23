// Wires the workload services into one shared, event-driven UI store.
// Custom apps persist in localStorage; running state comes only from process events.
import { useSyncExternalStore } from "react";
import { toast } from "sonner";
import { hasNativeBackend } from "@/lib/eo/nativeBridge";
import { isBackendConnected, subscribeConnection } from "@/lib/eo/connectionStore";
import {
  createWorkloadBackend,
  type WorkloadBackend,
} from "@/lib/eo/workload/backend";
import {
  addCustomApp,
  deleteCustomApp,
  makeCustomApp,
  parseStoredCustomApps,
  updateCustomApp,
  validateCustomApp,
  type CustomAppInput,
} from "@/lib/eo/workload/customApps";
import { resolveWorkloads } from "@/lib/eo/workload/optimizer";
import {
  ApplicationLauncher,
  InstalledApplicationDiscovery,
  RunningProcessMonitor,
  WorkloadOptimizer,
  reduceProcessEvent,
  type MonitorState,
} from "@/lib/eo/workload/services";
import type { EligibleApp, ProcessEvent, ResolvedWorkload } from "@/lib/eo/workload/types";

const CUSTOM_KEY = "eo-workload-custom-apps:v1";

export interface WorkloadView {
  mode: "NATIVE" | "SIMULATED";
  discovered: EligibleApp[];
  custom: EligibleApp[];
  inventory: EligibleApp[];
  workloads: ResolvedWorkload[];
  foregroundPid: number | null;
  launching: string[];
  scanning: boolean;
  lastScanAt: number | null;
  /** PIDs whose optimization profile is being applied right now. */
  optimizing: number[];
  /** Methods the backend reported as applied, per PID. Missing = not reported by the host. */
  applied: Record<number, string[]>;
  /** Workflows whose process just exited; shown as stopped briefly, then removed. */
  ended: EndedWorkflow[];
}

export interface EndedWorkflow {
  pid: number;
  name: string;
  exe: string;
  endedAt: number;
}

/** How long an exited workflow stays visible as STOPPED before it leaves the list. */
export const ENDED_VISIBLE_MS = 4000;

const EMPTY: WorkloadView = {
  mode: "SIMULATED",
  discovered: [],
  custom: [],
  inventory: [],
  workloads: [],
  foregroundPid: null,
  launching: [],
  scanning: false,
  lastScanAt: null,
  optimizing: [],
  applied: {},
  ended: [],
};

let view: WorkloadView = EMPTY;
let monitorState: MonitorState = { running: [], foregroundPid: null };
let backend: WorkloadBackend | null = null;
let discovery: InstalledApplicationDiscovery;
let launcher: ApplicationLauncher;
let optimizer: WorkloadOptimizer;
const listeners = new Set<() => void>();

function emit() {
  listeners.forEach((l) => l());
}

function set(patch: Partial<WorkloadView>) {
  const next = { ...view, ...patch };
  next.inventory = [...next.discovered, ...next.custom];
  view = next;
  emit();
}

function recompute() {
  const workloads = resolveWorkloads(monitorState.running, monitorState.foregroundPid);
  set({ workloads, foregroundPid: monitorState.foregroundPid });
  void optimizer.sync(workloads).catch(() => {});
}

function onProcessEvent(e: ProcessEvent) {
  if (e.type === "process-exit") {
    const gone = monitorState.running.find((w) => w.pid === e.pid);
    if (gone) {
      const applied = { ...view.applied };
      delete applied[e.pid];
      set({
        applied,
        ended: [
          ...view.ended.filter((x) => x.pid !== e.pid),
          { pid: e.pid, name: gone.name, exe: gone.exe, endedAt: e.at },
        ],
      });
      window.setTimeout(
        () => set({ ended: view.ended.filter((x) => x.pid !== e.pid) }),
        ENDED_VISIBLE_MS,
      );
    }
  }
  monitorState = reduceProcessEvent(monitorState, e, view.inventory);
  if (e.type === "process-start" && e.exe) {
    set({ launching: view.launching.filter((x) => x.toLowerCase() !== e.exe!.toLowerCase()) });
  }
  recompute();
}

function saveCustom(list: EligibleApp[]) {
  try {
    window.localStorage.setItem(CUSTOM_KEY, JSON.stringify(list));
  } catch {
    /* storage unavailable */
  }
}

let connSubAttached = false;
function init() {
  if (typeof window === "undefined") return;
  if (!connSubAttached) {
    connSubAttached = true;
    subscribeConnection(() => {
      if (!backend && isBackendConnected()) init();
    });
  }
  if (backend) return;
  let custom: EligibleApp[] = [];
  try {
    custom = parseStoredCustomApps(JSON.parse(window.localStorage.getItem(CUSTOM_KEY) ?? "[]"));
  } catch {
    /* ignore */
  }
  if (!hasNativeBackend() && !isBackendConnected()) {
    set({ mode: "SIMULATED", custom, discovered: [] });
    return;
  }
  backend = createWorkloadBackend();
  discovery = new InstalledApplicationDiscovery(backend);
  launcher = new ApplicationLauncher(backend);
  optimizer = new WorkloadOptimizer(backend, (pid, phase, appliedNow) => {
    const rest = view.optimizing.filter((p) => p !== pid);
    const applied = { ...view.applied };
    if (phase === "OPTIMIZED" && appliedNow) applied[pid] = appliedNow;
    else delete applied[pid];
    set({ optimizing: phase === "OPTIMIZING" ? [...rest, pid] : rest, applied });
  });
  set({ mode: backend.mode, custom });
  new RunningProcessMonitor(backend, onProcessEvent).start();
  void workloadActions.rescan();
}

function subscribe(l: () => void) {
  listeners.add(l);
  init();
  return () => {
    listeners.delete(l);
  };
}

export function useWorkloads(): WorkloadView {
  return useSyncExternalStore(
    subscribe,
    () => view,
    () => EMPTY,
  );
}

export const workloadActions = {
  async rescan() {
    if (!backend) {
      toast.info("Backend connection required", {
        description: "Application inventory discovery requires the ERROR OPTIMIZER background service.",
      });
      return;
    }
    set({ scanning: true });
    try {
      const discovered = await discovery.scan();
      // Custom entries win over a discovered duplicate executable.
      const customExes = new Set(view.custom.map((c) => c.exe.toLowerCase()));
      set({
        discovered: discovered.filter((d) => !customExes.has(d.exe.toLowerCase())),
        lastScanAt: Date.now(),
      });
    } finally {
      set({ scanning: false });
    }
  },
  async launch(app: EligibleApp) {
    if (!backend) {
      toast.info("Backend connection required", {
        description: "Launching and monitoring workloads requires the ERROR OPTIMIZER background service.",
      });
      return;
    }
    if (view.launching.includes(app.exe)) return;
    set({ launching: [...view.launching, app.exe] });
    try {
      await launcher.launch(app);
    } catch (e) {
      set({ launching: view.launching.filter((x) => x !== app.exe) });
      throw e;
    }
  },
  /** Brings the app to the front through the backend (real focus on the Windows host). */
  focus(pid: number) {
    return backend ? backend.focus(pid) : Promise.resolve();
  },
  stopOptimization(pid: number) {
    monitorState = {
      ...monitorState,
      running: monitorState.running.map((w) => (w.pid === pid ? { ...w, paused: true } : w)),
    };
    recompute();
  },
  resumeOptimization(pid: number) {
    monitorState = {
      ...monitorState,
      running: monitorState.running.map((w) => (w.pid === pid ? { ...w, paused: false } : w)),
    };
    recompute();
  },
  custom: {
    validate(input: CustomAppInput, editingId?: string) {
      return validateCustomApp(input, view.inventory, editingId);
    },
    add(input: CustomAppInput) {
      const err = validateCustomApp(input, view.inventory);
      if (err) return err;
      const custom = addCustomApp(
        view.custom,
        makeCustomApp(input, `custom:${Date.now().toString(36)}`),
      );
      saveCustom(custom);
      set({ custom });
      return null;
    },
    update(id: string, input: CustomAppInput) {
      const err = validateCustomApp(input, view.inventory, id);
      if (err) return err;
      const custom = updateCustomApp(view.custom, id, input);
      saveCustom(custom);
      const app = custom.find((c) => c.id === id)!;
      monitorState = {
        ...monitorState,
        running: monitorState.running.map((w) =>
          w.appId === id ? { ...w, name: app.name, category: app.category } : w,
        ),
      };
      set({ custom });
      recompute();
      return null;
    },
    remove(id: string) {
      const custom = deleteCustomApp(view.custom, id);
      saveCustom(custom);
      // Stop tracking it; the process itself is left running.
      monitorState = {
        ...monitorState,
        running: monitorState.running.filter((w) => w.appId !== id),
      };
      set({ custom });
      recompute();
    },
    pickExecutable() {
      return backend?.mode === "NATIVE" ? backend.pickExecutable() : Promise.resolve(null);
    },
  },
  preview: {
    available() {
      return false;
    },
    exit(_pid: number) {},
  },
};
