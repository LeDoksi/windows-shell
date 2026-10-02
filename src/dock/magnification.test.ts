import { describe, expect, it } from "vitest";
import { baseCenters, isSettled, springStep, targetScales } from "./magnification";

const o = { iconSize: 52, gap: 8, max: 1.55, range: 2.5 };
const centers = baseCenters([52, 52, 52, 52, 52], 8);

describe("baseCenters", () => {
  it("is symmetric around zero", () => {
    expect(centers).toEqual([-120, -60, 0, 60, 120]);
  });

  it("accounts for a narrow separator", () => {
    expect(baseCenters([52, 1, 52], 8)).toEqual([-34.5, 0, 34.5]);
  });
});

describe("targetScales", () => {
  it("is 1 everywhere without cursor", () => {
    expect(targetScales(null, centers, o)).toEqual([1, 1, 1, 1, 1]);
  });

  it("peaks under the cursor and falls off symmetrically", () => {
    const s = targetScales(0, centers, o);
    expect(s[2]).toBeCloseTo(1.55);
    expect(s[1]).toBeCloseTo(s[3]);
    expect(s[1]).toBeLessThan(s[2]);
    expect(s[0]).toBeLessThan(s[1]);
  });

  it("does not affect icons beyond the range", () => {
    const s = targetScales(-120, centers, { ...o, range: 1 });
    expect(s[4]).toBe(1);
  });
});

describe("springStep", () => {
  it("converges to the target", () => {
    let [x, v] = [1, 0];
    for (let i = 0; i < 120; i++) {
      [x, v] = springStep(x, v, 1.55, 1 / 60, { stiffness: 420, damping: 32 });
    }
    expect(isSettled(x, v, 1.55)).toBe(true);
  });
});
