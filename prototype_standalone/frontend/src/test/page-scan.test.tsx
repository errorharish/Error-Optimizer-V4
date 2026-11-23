import { act, render, screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { getPageScan, resetPageScans, runPageScan } from "@/lib/eo/pageScan";
import { usePageScan } from "@/components/eo/usePageScan";
import { PageHeader } from "@/components/eo/kit";

const deferred = () => {
  let resolve!: () => void;
  let reject!: (e: Error) => void;
  const promise = new Promise<void>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
};

describe("optimization page scan rules", () => {
  beforeEach(() => resetPageScans());

  it("runs one scan even when asked twice while it is running", async () => {
    const d = deferred();
    const task = vi.fn(() => d.promise);
    runPageScan("p", task);
    runPageScan("p", task);
    runPageScan("p", task, { force: true });
    expect(task).toHaveBeenCalledTimes(1);
    d.resolve();
    await runPageScan("p", task).promise;
    expect(getPageScan("p").phase).toBe("done");
  });

  it("keeps a finished result instead of rescanning on re-entry", async () => {
    const task = vi.fn(async () => {});
    await runPageScan("p", task).promise;
    await runPageScan("p", task).promise;
    expect(task).toHaveBeenCalledTimes(1);
    expect(getPageScan("p").runs).toBe(1);
  });

  it("Refresh repeats the scan", async () => {
    const task = vi.fn(async () => {});
    await runPageScan("p", task).promise;
    await runPageScan("p", task, { force: true }).promise;
    expect(task).toHaveBeenCalledTimes(2);
    expect(getPageScan("p").runs).toBe(2);
  });

  it("a failed scan shows its error and the next attempt retries", async () => {
    await runPageScan("p", async () => {
      throw new Error("Backend unavailable.");
    }).promise;
    expect(getPageScan("p")).toMatchObject({ phase: "error", error: "Backend unavailable." });
    const retry = vi.fn(async () => {});
    await runPageScan("p", retry).promise;
    expect(retry).toHaveBeenCalledTimes(1);
    expect(getPageScan("p").phase).toBe("done");
  });
});

function Page({ task }: { task: () => Promise<void> }) {
  const scan = usePageScan("page", task);
  return <PageHeader title="Test Page" guide="dashboard" scan={scan} />;
}

describe("optimization page header", () => {
  beforeEach(() => resetPageScans());

  it("shows exactly Guide and Refresh and scans automatically on open", async () => {
    const d = deferred();
    const task = vi.fn(() => d.promise);
    const { container, rerender } = render(<Page task={task} />);
    rerender(<Page task={task} />);
    expect(task).toHaveBeenCalledTimes(1);

    const actions = container.querySelector(".eo-page-actions") as HTMLElement;
    const buttons = within(actions).getAllByRole("button");
    expect(buttons.map((b) => b.textContent?.trim())).toEqual(["Guide", "Refresh"]);
    // Refresh is blocked while the initial scan runs.
    expect(screen.getByRole("button", { name: /refresh/i })).toBeDisabled();

    await act(async () => d.resolve());
    const refresh = screen.getByRole("button", { name: /refresh/i });
    expect(refresh).not.toBeDisabled();
    await act(async () => refresh.click());
    expect(task).toHaveBeenCalledTimes(2);
  });
});
