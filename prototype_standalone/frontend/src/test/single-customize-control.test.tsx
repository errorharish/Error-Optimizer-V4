import { act, render, renderHook, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import { SelectionBar } from "@/components/eo/kit";
import { resetSavedSelections, useModedSelection } from "@/components/eo/useSelection";

const items = [
  { id: "a", risk: "SAFE" as const },
  { id: "b", risk: "MEDIUM" as const },
  { id: "c", risk: "LOW" as const, unsupported: true },
];

function Bar({ k }: { k: string }) {
  const moded = useModedSelection(k, items);
  return <SelectionBar count={moded.sel.size} total={moded.eligibleCount} moded={moded} />;
}

describe("single Customize control", () => {
  beforeEach(() => resetSavedSelections());

  it("defaults to ALL ELIGIBLE with every eligible option selected", () => {
    const { result } = renderHook(() => useModedSelection("p:default", items));
    expect(result.current.mode).toBe("ALL_ELIGIBLE");
    expect([...result.current.sel].sort()).toEqual(["a", "b"]);
  });

  it("shows exactly one Customize control on the selection bar", () => {
    render(<Bar k="p:bar" />);
    expect(screen.getAllByRole("button", { name: /customiz/i })).toHaveLength(1);
  });

  it("switching back to ALL ELIGIBLE restores the eligible selection", () => {
    const { result } = renderHook(() => useModedSelection("p:restore", items));
    act(() => result.current.confirmCustom(new Set(["a"])));
    expect([...result.current.sel]).toEqual(["a"]);
    act(() => result.current.setMode("ALL_ELIGIBLE"));
    expect([...result.current.sel].sort()).toEqual(["a", "b"]);
  });

  it("each page keeps its own custom selection", () => {
    const { result: one } = renderHook(() => useModedSelection("p:one", items));
    const { result: two } = renderHook(() => useModedSelection("p:two", items));
    act(() => one.current.confirmCustom(new Set(["b"])));
    expect([...one.current.sel]).toEqual(["b"]);
    expect(two.current.mode).toBe("ALL_ELIGIBLE");
    expect([...two.current.sel].sort()).toEqual(["a", "b"]);
  });
});
