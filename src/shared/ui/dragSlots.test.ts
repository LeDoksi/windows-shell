import { describe, expect, it } from "vitest";
import { insertionIndex, moveIndex, nearestSlot, type Slot } from "./dragSlots";

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

describe("insertionIndex", () => {
  it("inserts before the first cell the cursor has not passed", () => {
    expect(insertionIndex(grid, 10, 50)).toBe(0);
    expect(insertionIndex(grid, 70, 50)).toBe(1);
    expect(insertionIndex(grid, 290, 50)).toBe(3);
    expect(insertionIndex(grid, 120, 150)).toBe(4);
    expect(insertionIndex(grid, 280, 150)).toBe(5);
    expect(insertionIndex(grid, 10, 500)).toBe(5);
  });
});

describe("moveIndex", () => {
  it("moves an element and clamps the target", () => {
    expect(moveIndex(["a", "b", "c"], 0, 2)).toEqual(["b", "c", "a"]);
    expect(moveIndex(["a", "b", "c"], 2, 0)).toEqual(["c", "a", "b"]);
    expect(moveIndex(["a", "b", "c"], 1, 9)).toEqual(["a", "c", "b"]);
  });
});
