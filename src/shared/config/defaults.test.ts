import { describe, expect, it } from "vitest";
import { defaultConfig } from "./defaults";
import { validateConfig } from "./validate";

describe("defaultConfig", () => {
  it("is a valid v1 config", () => {
    expect(validateConfig(defaultConfig)).toBeNull();
  });
});

describe("validateConfig", () => {
  it("rejects duplicate item ids across categories", () => {
    const [a, b] = defaultConfig.categories;
    const broken = {
      ...defaultConfig,
      categories: [a, { ...b, items: [...b.items, a.items[0]] }],
    };
    expect(validateConfig(broken)).toMatch(/повторяется id item/);
  });

  it("rejects unknown schema version", () => {
    expect(validateConfig({ ...defaultConfig, schemaVersion: 2 as 1 })).toMatch(/версия/);
  });
});
