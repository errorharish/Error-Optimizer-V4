// Status displays draw exactly three colours: green, yellow, red.
import { describe, expect, it } from "vitest";
import { statusFamily, type StatusTone } from "@/components/eo/StatusIndicator";

describe("status colour families", () => {
  it("connected / running is green", () => expect(statusFamily("pos")).toBe("pos"));
  it("connecting / detecting is yellow", () => expect(statusFamily("warn")).toBe("warn"));
  it("disconnected / offline is red", () => expect(statusFamily("neg")).toBe("neg"));
  it("errors stay in the red family", () => expect(statusFamily("err")).toBe("neg"));
  it("no tone renders a fourth colour", () => {
    const all: StatusTone[] = ["pos", "warn", "neg", "err", "info", "muted"];
    expect(new Set(all.map(statusFamily))).toEqual(new Set(["pos", "warn", "neg"]));
  });
});
