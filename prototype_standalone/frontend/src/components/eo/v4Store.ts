// ERROR OPTIMIZER — Core Optimization session state and activity log.
import { useSyncExternalStore } from "react";

export const OPTIMIZATION_PROFILES = ["BALANCED PROFILE", "PERFORMANCE PROFILE"] as const;
export type OptimizationProfile = (typeof OPTIMIZATION_PROFILES)[number];
export type EmuProfile = OptimizationProfile;
export const EMU_PROFILES = OPTIMIZATION_PROFILES;

export interface CoreOptimizationState {
  adb: boolean;
  applied: number[];
  profile: OptimizationProfile;
  activity: OptimizationActivity[];
}

export type EmuState = CoreOptimizationState;

export interface OptimizationActivity {
  module: string;
  status: "APPLIED" | "RESTORED";
  at: string;
  summary: string;
}

export type V4Activity = OptimizationActivity;

const PROFILE_KEY = "eo-optimization-profile";
let state: CoreOptimizationState = { adb: false, applied: [], profile: "PERFORMANCE PROFILE", activity: [] };
let hydrated = false;
const listeners = new Set<() => void>();

function hydrate() {
  if (hydrated || typeof window === "undefined") return;
  hydrated = true;
  const p = window.localStorage.getItem(PROFILE_KEY);
  if (p && (OPTIMIZATION_PROFILES as readonly string[]).includes(p)) {
    state = { ...state, profile: p as OptimizationProfile };
  }
}

export function setOptimizationState(patch: Partial<CoreOptimizationState>) {
  hydrate();
  state = { ...state, ...patch };
  if (patch.profile) {
    try {
      window.localStorage.setItem(PROFILE_KEY, patch.profile);
    } catch {
      /* ignore */
    }
  }
  listeners.forEach((l) => l());
}

export const setEmu = setOptimizationState;

const SERVER: CoreOptimizationState = { adb: false, applied: [], profile: "PERFORMANCE PROFILE", activity: [] };

/** Records an activity entry for the optimization session (keeps the latest 8). */
export function logOptimizationActivity(module: string, summary: string, status: OptimizationActivity["status"] = "APPLIED") {
  hydrate();
  const at = new Date().toLocaleTimeString();
  setOptimizationState({ activity: [{ module, status, at, summary }, ...state.activity].slice(0, 8) });
}

export const logV4 = logOptimizationActivity;

export function useOptimizationState(): CoreOptimizationState {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
    () => {
      hydrate();
      return state;
    },
    () => SERVER,
  );
}

export const useEmu = useOptimizationState;

/** Current snapshot for event handlers that must not use a stale render value. */
export function getOptimizationState(): CoreOptimizationState {
  hydrate();
  return state;
}

export const getEmu = getOptimizationState;
