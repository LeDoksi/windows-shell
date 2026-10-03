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

  it("defaults to glass and accepts only the two glass modes", () => {
    expect(defaultConfig.appearance.glass).toBe("acrylic");
    const tint = { ...defaultConfig, appearance: { glass: "tint" as const } };
    expect(validateConfig(tint)).toBeNull();
    const broken = { ...defaultConfig, appearance: { glass: "auto" as "tint" } };
    expect(validateConfig(broken)).toMatch(/режим стекла/);
  });

  it("defaults both bars to autohide", () => {
    expect(defaultConfig.bars).toEqual({ launcherPinned: false, dockPinned: false });
    const broken = {
      ...defaultConfig,
      bars: { launcherPinned: "yes" as unknown as boolean, dockPinned: false },
    };
    expect(validateConfig(broken)).toMatch(/закрепление/);
  });

  it("rejects unknown schema version", () => {
    expect(validateConfig({ ...defaultConfig, schemaVersion: 2 as 1 })).toMatch(/версия/);
  });
});
