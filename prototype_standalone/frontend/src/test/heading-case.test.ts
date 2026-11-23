import { describe, expect, it } from "vitest";
import { headingCase } from "@/lib/eo/headingCase";

describe("headingCase", () => {
  it("title-cases all-caps headings", () => {
    expect(headingCase("OPTIMIZATION ACTIVITY")).toBe("Optimization Activity");
  });
  it("keeps acronyms and version tokens", () => {
    expect(headingCase("V4 PERFORMANCE CENTER")).toBe("V4 Performance Center");
    expect(headingCase("CPU AND GPU USAGE")).toBe("CPU and GPU Usage");
  });
  it("leaves mixed-case text alone", () => {
    expect(headingCase("Emulator Debloater")).toBe("Emulator Debloater");
  });
});
