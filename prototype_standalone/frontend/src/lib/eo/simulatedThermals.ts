// Demo-only temperatures for the Dashboard Temperature Monitoring card in the browser preview.
// Kept separate from thermals.ts so generated values never mix with real sensor readings; the UI
// labels them SIMULATED DATA and never uses them inside the Windows host.
import type { ThermalSample } from "./thermals";

export const SIM_THERMAL_INTERVAL_MS = 1500;

const BANDS = {
  cpu: [38, 72],
  gpu: [34, 68],
  ssd: [33, 48],
} as const;

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));
const r1 = (v: number) => Math.round(v * 10) / 10;

export const initialSimThermal = (): ThermalSample => ({ cpu: 46, gpu: 41, gpuHotspot: 52, ssd: 38 });

/** Small random-walk step inside each sensor's band; the hotspot tracks the GPU core (+8–14 °C). */
export function nextSimThermal(prev: ThermalSample, rand: () => number = Math.random): ThermalSample {
  const step = (k: keyof typeof BANDS, spread: number) => {
    const [lo, hi] = BANDS[k];
    return r1(clamp((prev[k] ?? lo) + (rand() - 0.5) * spread, lo, hi));
  };
  const gpu = step("gpu", 3.2);
  return { cpu: step("cpu", 3.6), gpu, gpuHotspot: r1(gpu + 8 + rand() * 6), ssd: step("ssd", 0.8) };
}

/** Pre-filled history so the preview graph is readable immediately. */
export function seedSimHistory(n: number, rand: () => number = Math.random): ThermalSample[] {
  const out: ThermalSample[] = [];
  let s = initialSimThermal();
  for (let i = 0; i < n; i++) {
    s = nextSimThermal(s, rand);
    out.push(s);
  }
  return out;
}
