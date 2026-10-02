import { describe, expect, it } from "vitest";
import type { LauncherItem } from "../shared/config";
import { matchesQuery } from "./search";

const item: LauncherItem = { id: "a", kind: "app", title: "Visual Studio Code", target: "code" };

describe("matchesQuery", () => {
  it("matches everything on empty query", () => {
    expect(matchesQuery(item, "  ")).toBe(true);
  });
  it("is case-insensitive substring match", () => {
    expect(matchesQuery(item, "studio")).toBe(true);
    expect(matchesQuery(item, "СТУДИЯ")).toBe(false);
  });
});
