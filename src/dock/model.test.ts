import { describe, expect, it } from "vitest";
import { buildDock, clickApp, type RunningApp } from "./model";

const pinned = [
  { id: "a", title: "A", target: "a" },
  { id: "b", title: "B", target: "b" },
];

describe("buildDock", () => {
  it("keeps pinned order and appends unpinned running apps", () => {
    const running: RunningApp[] = [
      { id: "x", title: "X", windows: 1 },
      { id: "b", title: "B", windows: 2, active: true },
    ];
    const dock = buildDock(pinned, running);
    expect(dock.pinned.map((e) => [e.id, e.state])).toEqual([
      ["a", "idle"],
      ["b", "active"],
    ]);
    expect(dock.transient.map((e) => e.id)).toEqual(["x"]);
  });

  it("attention wins over active", () => {
    const dock = buildDock(pinned, [
      { id: "a", title: "A", windows: 1, active: true, attention: true },
    ]);
    expect(dock.pinned[0].state).toBe("attention");
  });
});

describe("clickApp", () => {
  it("launches, activates and minimizes", () => {
    let running = clickApp([], "a", "A");
    expect(running).toEqual([{ id: "a", title: "A", windows: 1, active: true }]);
    running = clickApp(running, "b", "B");
    expect(running.find((r) => r.id === "a")?.active).toBe(false);
    running = clickApp(running, "a", "A");
    expect(running.find((r) => r.id === "a")?.active).toBe(true);
    running = clickApp(running, "a", "A");
    expect(running.find((r) => r.id === "a")?.active).toBe(false);
  });
});
