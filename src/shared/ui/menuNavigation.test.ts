import { describe, expect, it } from "vitest";
import { nextEnabledIndex } from "./menuNavigation";

describe("nextEnabledIndex", () => {
  it("skips disabled items", () => {
    expect(nextEnabledIndex([false, true, false], 0, 1)).toBe(2);
  });

  it("wraps around in both directions", () => {
    expect(nextEnabledIndex([false, false, false], 2, 1)).toBe(0);
    expect(nextEnabledIndex([false, false, false], 0, -1)).toBe(2);
  });

  it("starts from the first enabled item when nothing is focused", () => {
    expect(nextEnabledIndex([true, false], -1, 1)).toBe(1);
  });

  it("stays put when every item is disabled", () => {
    expect(nextEnabledIndex([true, true], -1, 1)).toBe(-1);
  });
});
