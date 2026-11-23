// Demo-only CPU / GPU / RAM values for the V4 System Usage panel while no Windows
// telemetry exists. Kept separate from v4Telemetry so simulated numbers never mix with
// real readings; the UI labels them "SIMULATED" and drops them once real data arrives.
export interface SimUsage {
  cpu: number;
  gpu: number;
  ram: number;
}

export const SIM_INTERVAL_MS = 1500;
const RANGES: Record<keyof SimUsage, [number, number]> = {
  cpu: [8, 72],
  gpu: [4, 65],
  ram: [38, 78],
};

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

export const initialSimUsage = (): SimUsage => ({ cpu: 24, gpu: 18, ram: 52 });

/** Random walk step: small realistic changes, always within each metric's band (0–100). */
export function nextSimUsage(prev: SimUsage, rand: () => number = Math.random): SimUsage {
  const step = (k: keyof SimUsage, spread: number) => {
    const [lo, hi] = RANGES[k];
    return Math.round(clamp(prev[k] + (rand() - 0.5) * spread, lo, hi));
  };
  return { cpu: step("cpu", 18), gpu: step("gpu", 16), ram: step("ram", 4) };
}
