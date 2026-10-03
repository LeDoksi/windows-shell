import { describe, expect, it, vi } from "vitest";
import { defaultConfig } from "../shared/config/defaults";
import { canReveal, isPath, removeItem } from "./items";

describe("removeItem", () => {
  it("drops only that tile from its category", () => {
    const [work] = defaultConfig.categories;
    const [first, ...rest] = work.items;
    const next = removeItem(defaultConfig, work.id, first.id);
    expect(next.categories[0].items).toEqual(rest);
    expect(next.categories.slice(1)).toEqual(defaultConfig.categories.slice(1));
    expect(defaultConfig.categories[0].items[0]).toBe(first);
  });

  it("never touches the file on disk: it only builds a new config", async () => {
    const { backend } = await import("../shared/backend");
    const call = vi.spyOn(backend, "call");
    const [work] = defaultConfig.categories;
    removeItem(defaultConfig, work.id, work.items[0].id);
    expect(call).not.toHaveBeenCalled();
  });
});

describe("paths", () => {
  it("tells disk paths from app ids and links", () => {
    expect(isPath("D:\\Work\\Planning.xlsx")).toBe(true);
    expect(isPath("MSTeams_8wekyb3d8bbwe!MSTeams")).toBe(false);
    expect(isPath("{6D809377-6AF0-444B-8957-A3773F02200E}\\Notepad++\\notepad++.exe")).toBe(false);
    expect(isPath("\\\\nas\\share")).toBe(true);
    expect(canReveal({ id: "u", kind: "url", title: "", target: "https://a.b/c" })).toBe(false);
    expect(canReveal({ id: "a", kind: "app", title: "", target: "Microsoft.Edge" })).toBe(false);
    expect(canReveal({ id: "f", kind: "file", title: "", target: "C:\\a.txt" })).toBe(true);
  });
});
