import { describe, expect, it } from "vitest";
import { btnProps, variantAttr } from "./buttonSystem";

describe("button system", () => {
  it("renders every legacy action variant with the premium primary look", () => {
    for (const v of ["secondary", "ghost", "outline", "success", "primary"] as const) {
      expect(variantAttr(v)).toBe("primary");
    }
  });
  it("keeps destructive buttons on the danger look", () => {
    expect(variantAttr("danger")).toBe("danger");
  });
  it("defaults btnProps to the premium primary look", () => {
    expect(btnProps()["data-variant"]).toBe("primary");
  });
});
