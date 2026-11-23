import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { ExecutionResultPanel } from "@/components/eo/ExecutionResult";
import {
  RUN_STATUS_TITLE,
  countResults,
  mergeRetry,
  parseHostResult,
  retryTargets,
  runStatus,
  simulatedResult,
  type ExecutionResult,
  type OperationResult,
  type OpStatus,
} from "@/lib/eo/executionResult";

const ops = (spec: Partial<Record<OpStatus, number>>): OperationResult[] =>
  (Object.entries(spec) as [OpStatus, number][]).flatMap(([status, n]) =>
    Array.from({ length: n }, (_, i) => ({ id: `${status}-${i}`, title: `${status} ${i}`, status })),
  );
const result = (operations: OperationResult[], extra: Partial<ExecutionResult> = {}): ExecutionResult => ({
  module: "Test",
  title: "Test run",
  simulated: false,
  operations,
  recovery: { available: false, label: "No backup" },
  ...extra,
});

describe("overall result status comes only from operation results", () => {
  it("10 verified → SUCCESS", () => {
    expect(runStatus(result(ops({ succeeded: 10 })))).toBe("SUCCESS");
  });
  it("7 verified, 2 failed, 1 skipped → PARTIAL with exact counts", () => {
    const r = result(ops({ succeeded: 7, failed: 2, skipped: 1 }));
    expect(runStatus(r)).toBe("PARTIAL");
    expect(countResults(r.operations)).toEqual({ requested: 10, succeeded: 7, failed: 2, skipped: 1, unverified: 0 });
  });
  it("0 verified, 5 failed → FAILED", () => {
    expect(runStatus(result(ops({ failed: 5 })))).toBe("FAILED");
  });
  it("5 executed without confirmation → UNVERIFIED, never SUCCESS", () => {
    expect(runStatus(result(ops({ unverified: 5 })))).toBe("UNVERIFIED");
  });
  it("verified plus unverified is PARTIAL, not SUCCESS", () => {
    expect(runStatus(result(ops({ succeeded: 4, unverified: 1 })))).toBe("PARTIAL");
  });
  it("nothing executed → SKIPPED; a run-level error → FAILED", () => {
    expect(runStatus(result(ops({ skipped: 3 })))).toBe("SKIPPED");
    expect(runStatus(result([], { error: "Service not reachable" }))).toBe("FAILED");
    expect(runStatus(result(ops({ succeeded: 2 }), { error: "Crashed after step 2" }))).toBe("PARTIAL");
  });
});

describe("Windows host results", () => {
  const host = (operations: unknown[]) => parseHostResult({ type: "optimize:result", operations }, "M", "T");
  it("applied + verified → verified; applied without verification → unverified", () => {
    const r = host([
      { id: "a", title: "A", executed: true, ok: true, verified: true },
      { id: "b", title: "B", executed: true, ok: true },
    ])!;
    expect(r.operations.map((o) => o.status)).toEqual(["succeeded", "unverified"]);
    expect(runStatus(r)).toBe("PARTIAL");
  });
  it("a failed verification is a failure with its reason", () => {
    const r = host([{ id: "a", executed: true, ok: true, verified: false, verifyDetail: "Value still 0" }])!;
    expect(r.operations[0]).toMatchObject({ status: "failed", reason: "Value still 0" });
  });
  it("keeps skip reasons and errors as written", () => {
    const r = host([
      { id: "a", skippedReason: "Requires administrator" },
      { id: "b", executed: true, ok: false, error: "Access denied", retryable: true },
    ])!;
    expect(r.operations[0]).toMatchObject({ status: "skipped", reason: "Requires administrator" });
    expect(r.operations[1]).toMatchObject({ status: "failed", reason: "Access denied", retryable: true });
  });
  it("offers rollback only when the host reports a restore point", () => {
    expect(host([])?.recovery.available).toBe(false);
    const r = parseHostResult({ type: "optimize:result", operations: [], rollback: { available: true, restorePoint: "RP 12" } }, "M", "T")!;
    expect(r.recovery).toEqual({ available: true, label: "RP 12" });
    expect(parseHostResult({ type: "telemetry" }, "M", "T")).toBeNull();
  });
});

describe("retry", () => {
  it("retries only eligible failed operations and keeps the successful ones", () => {
    const prev = result([
      { id: "ok", title: "OK", status: "succeeded" },
      { id: "f1", title: "F1", status: "failed", retryable: true },
      { id: "f2", title: "F2", status: "failed", retryable: false },
    ]);
    expect(retryTargets(prev).map((o) => o.id)).toEqual(["f1"]);
    const retry = result([
      { id: "f1", title: "F1", status: "succeeded" },
      { id: "ok", title: "OK", status: "failed" },
    ]);
    const merged = mergeRetry(prev, retry);
    expect(merged.operations.map((o) => o.status)).toEqual(["succeeded", "succeeded", "failed"]);
    expect(runStatus(merged)).toBe("PARTIAL");
  });
});

describe("browser preview runs", () => {
  it("never report success: every operation is skipped and rollback is not offered", () => {
    const r = simulatedResult("Tier", "Run", [
      { id: "a", title: "A" },
      { id: "b", title: "B", skipReason: "Running apps are never trimmed" },
    ]);
    expect(r.simulated).toBe(true);
    expect(runStatus(r)).toBe("SKIPPED");
    expect(r.operations[1]?.reason).toBe("Running apps are never trimmed");
    expect(r.recovery.available).toBe(false);
  });
});

describe("result panel", () => {
  it("shows the title for each state and never OPTIMIZATION SUCCESSFUL for mixed results", () => {
    const { unmount } = render(<ExecutionResultPanel result={result(ops({ succeeded: 7, failed: 2, skipped: 1 }))} />);
    expect(screen.getByText(RUN_STATUS_TITLE.PARTIAL)).toBeTruthy();
    expect(screen.queryByText(RUN_STATUS_TITLE.SUCCESS)).toBeNull();
    unmount();
    render(<ExecutionResultPanel result={result(ops({ unverified: 5 }))} />);
    expect(screen.getByText("VERIFICATION PENDING")).toBeTruthy();
  });
  it("shows Retry only when a failed operation is retryable and a retry handler exists", () => {
    const r = result([{ id: "f", title: "F", status: "failed", retryable: true }]);
    const { rerender } = render(<ExecutionResultPanel result={r} />);
    expect(screen.queryByRole("button", { name: /retry/i })).toBeNull();
    rerender(<ExecutionResultPanel result={r} onRetry={() => {}} />);
    expect(screen.getByRole("button", { name: /retry 1 failed/i })).toBeTruthy();
  });
});
