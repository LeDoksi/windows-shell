import { describe, expect, it } from "vitest";
import { defaultConfig } from "../shared/config/defaults";
import { validateConfig } from "../shared/config/validate";
import {
  addCategory,
  moveCategory,
  recolorCategory,
  removeCategory,
  renameCategory,
} from "./categories";

const ids = (config: typeof defaultConfig) => config.categories.map((c) => c.id);

describe("categories", () => {
  it("adds an empty category with a free id and an unused color", () => {
    const { config, id } = addCategory(defaultConfig);
    const added = config.categories.at(-1)!;
    expect(added).toMatchObject({ id, name: "Новая категория", items: [] });
    expect(defaultConfig.categories.map((c) => c.color)).not.toContain(added.color);
    expect(validateConfig(config)).toBeNull();
    const again = addCategory(config);
    expect(again.id).not.toBe(id);
    expect(validateConfig(again.config)).toBeNull();
  });

  it("renames, but not to an empty name", () => {
    const id = defaultConfig.categories[0].id;
    expect(renameCategory(defaultConfig, id, "  Учёба ").categories[0].name).toBe("Учёба");
    expect(renameCategory(defaultConfig, id, "   ")).toBe(defaultConfig);
  });

  it("recolors one category", () => {
    const id = defaultConfig.categories[1].id;
    const next = recolorCategory(defaultConfig, id, "#8E9AAF");
    expect(next.categories[1].color).toBe("#8E9AAF");
    expect(next.categories[0]).toBe(defaultConfig.categories[0]);
  });

  it("moves left and right, and stops at the edges", () => {
    const [a, b] = ids(defaultConfig);
    expect(ids(moveCategory(defaultConfig, b, -1)).slice(0, 2)).toEqual([b, a]);
    expect(ids(moveCategory(defaultConfig, a, 1)).slice(0, 2)).toEqual([b, a]);
    expect(moveCategory(defaultConfig, a, -1)).toBe(defaultConfig);
    const last = ids(defaultConfig).at(-1)!;
    expect(moveCategory(defaultConfig, last, 1)).toBe(defaultConfig);
  });

  it("removes only that category", () => {
    const [a, ...rest] = ids(defaultConfig);
    expect(ids(removeCategory(defaultConfig, a))).toEqual(rest);
  });
});
