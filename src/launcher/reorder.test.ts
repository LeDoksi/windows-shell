import { describe, expect, it } from "vitest";
import type { Config } from "../shared/config";
import { validateConfig } from "../shared/config/validate";
import { dropPaths, moveItem } from "./reorder";

const item = (id: string) => ({ id, kind: "file" as const, title: id, target: `C:\\${id}` });
const config: Config = {
  schemaVersion: 1,
  categories: [
    { id: "a", name: "A", color: "#43B4F5", items: [item("1"), item("2"), item("3")] },
    { id: "b", name: "B", color: "#B07CF0", items: [item("4")] },
  ],
  dock: { pinned: [] },
  appearance: { glass: "acrylic" },
  bars: { launcherPinned: true, dockPinned: true, monitor: null },
  widgets: [],
};
const ids = (c: Config, id: string) =>
  c.categories.find((x) => x.id === id)!.items.map((i) => i.id);

describe("moveItem", () => {
  it("reorders inside a category", () => {
    expect(ids(moveItem(config, "a", "1", "a", 2), "a")).toEqual(["2", "3", "1"]);
    expect(ids(moveItem(config, "a", "3", "a", 0), "a")).toEqual(["3", "1", "2"]);
    expect(moveItem(config, "a", "2", "a", 1)).toBe(config);
  });

  it("moves to another category, at a place or to the end", () => {
    const end = moveItem(config, "a", "2", "b");
    expect(ids(end, "a")).toEqual(["1", "3"]);
    expect(ids(end, "b")).toEqual(["4", "2"]);
    expect(ids(moveItem(config, "a", "2", "b", 0), "b")).toEqual(["2", "4"]);
    expect(validateConfig(end)).toBeNull();
  });

  it("ignores unknown tiles and categories", () => {
    expect(moveItem(config, "a", "nope", "b")).toBe(config);
    expect(moveItem(config, "a", "1", "nope")).toBe(config);
  });
});

describe("dropPaths", () => {
  it("adds files and folders at a place, skipping ones already there", () => {
    const next = dropPaths(
      config,
      "b",
      ["D:\\Фото\\", "C:\\4", "D:\\Plan.xlsx", "d:\\фото"],
      [true, false, false, true],
      0,
    );
    const items = next.categories[1].items;
    expect(items.map((i) => i.title)).toEqual(["Фото", "Plan.xlsx", "4"]);
    expect(items[0]).toMatchObject({ kind: "folder", target: "D:\\Фото\\" });
    expect(dropPaths(config, "b", ["C:\\4"], [false])).toBe(config);
  });
});
