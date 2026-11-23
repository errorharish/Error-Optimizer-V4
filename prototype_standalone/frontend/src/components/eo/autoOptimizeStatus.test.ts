import { act, renderHook } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

vi.mock("sonner", () => ({ toast: Object.assign(vi.fn(), { error: vi.fn() }) }));

import { autoOnline } from "./AutoOptimizePanel";
import { autoOptimize, useAutoOptimize } from "./autoOptimizeStore";

describe("Auto Optimize ONLINE / OFFLINE status", () => {
  it("goes ONLINE on enable, OFFLINE on disable, and stays OFFLINE when enable fails", () => {
    const { result } = renderHook(() => useAutoOptimize());
    const cfg = result.current.config;

    act(() => autoOptimize.disable());
    expect(autoOnline(result.current)).toBe(false);

    let ok = false;
    act(() => {
      ok = autoOptimize.enable();
    });
    expect(ok).toBe(true);
    expect(autoOnline(result.current)).toBe(true);
    expect(result.current.run).toBeNull(); // enabling never starts a manual run

    act(() => autoOptimize.disable());
    expect(autoOnline(result.current)).toBe(false);

    act(() => autoOptimize.saveConfig({ ...cfg, tasks: [] }));
    act(() => {
      ok = autoOptimize.enable();
    });
    expect(ok).toBe(false);
    expect(autoOnline(result.current)).toBe(false);
  });
});
