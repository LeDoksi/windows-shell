import { describe, expect, it } from "vitest";
import { moveIndex, nearestSlot, type Slot } from "./dragSlots";

/** Сетка 3×2 из клеток 100×100. */
const grid: Slot[] = [0, 1, 2, 3, 4].map((i) => {
  const left = (i % 3) * 100;
  const top = Math.floor(i / 3) * 100;
  return { left, top, right: left + 100, bottom: top + 100 };
});

describe("nearestSlot", () => {
  it("picks the closest cell", () => {
    expect(nearestSlot(grid, 10, 10)).toBe(0);
    expect(nearestSlot(grid, 260, 40)).toBe(2);
    expect(nearestSlot(grid, 150, 190)).toBe(4);
    expect(nearestSlot(grid, 900, 900)).toBe(4);
    expect(nearestSlot([], 0, 0)).toBe(0);
  });
});

describe("moveIndex", () => {
  it("moves an element and clamps the target", () => {
    expect(moveIndex(["a", "b", "c"], 0, 2)).toEqual(["b", "c", "a"]);
    expect(moveIndex(["a", "b", "c"], 2, 0)).toEqual(["c", "a", "b"]);
    expect(moveIndex(["a", "b", "c"], 1, 9)).toEqual(["a", "c", "b"]);
  });
});
