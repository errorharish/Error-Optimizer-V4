import { describe, expect, it } from "vitest";
import { statusTone } from "@/components/eo/StatusIndicator";
import { STATUS_TONE } from "@/components/eo/AutoOptimizePanel";

describe("status display colours", () => {
  it("online / connected states are green", () => {
    expect(statusTone("online")).toBe("pos");
    expect(statusTone("ok")).toBe("pos");
  });
  it("offline / disconnected states are red, not error", () => {
    expect(statusTone("offline")).toBe("neg");
    expect(statusTone("down")).toBe("neg");
  });
  it("connecting / detecting states are amber", () => {
    expect(statusTone("connecting")).toBe("warn");
    expect(statusTone("busy")).toBe("warn");
  });
  it("failed states use the error tone", () => {
    expect(statusTone("failed")).toBe("err");
  });
  it("Auto Optimize: running and enabled green, paused amber, disabled red", () => {
    expect(STATUS_TONE.Running).toBe("pos");
    expect(STATUS_TONE.Enabled).toBe("pos");
    expect(STATUS_TONE.Paused).toBe("warn");
    expect(STATUS_TONE.Disabled).toBe("neg");
  });
});
