import { describe, expect, it } from "vitest";
import { clockLines, duration, monthGrid } from "./format";

describe("clockLines", () => {
  it("formats time and short date", () => {
    const { time, day } = clockLines(new Date(2026, 9, 2, 12, 23));
    expect(time).toBe("12:23");
    expect(day).toBe("2 окт");
  });
});

describe("monthGrid", () => {
  it("starts weeks on Monday", () => {
    // 1 октября 2026 — четверг.
    const grid = monthGrid(2026, 9);
    expect(grid[0]).toEqual([null, null, null, 1, 2, 3, 4]);
    expect(grid.flat().filter(Boolean)).toHaveLength(31);
    expect(grid.every((week) => week.length === 7)).toBe(true);
  });
});

describe("duration", () => {
  it("formats hours and minutes", () => {
    expect(duration(214)).toBe("3 ч 34 мин");
    expect(duration(45)).toBe("45 мин");
  });
});
