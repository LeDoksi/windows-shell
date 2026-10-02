import { describe, expect, it } from "vitest";
import { defaultConfig } from "./defaults";

describe("defaultConfig", () => {
  it("has unique category and item ids", () => {
    const categoryIds = defaultConfig.categories.map((c) => c.id);
    const itemIds = defaultConfig.categories.flatMap((c) => c.items.map((i) => i.id));
    expect(new Set(categoryIds).size).toBe(categoryIds.length);
    expect(new Set(itemIds).size).toBe(itemIds.length);
  });
});
