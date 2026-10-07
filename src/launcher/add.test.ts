import { describe, expect, it } from "vitest";
import { defaultConfig } from "../shared/config/defaults";
import { validateConfig } from "../shared/config/validate";
import { addItems, itemFromApp, itemFromPath, itemFromUrl } from "./add";

describe("itemFromPath", () => {
  it("makes folders, programs and files", () => {
    expect(itemFromPath("D:\\Фото\\", true)).toEqual({
      kind: "folder",
      title: "Фото",
      target: "D:\\Фото\\",
      meta: "Папка",
    });
    expect(itemFromPath("C:\\Users\\me\\Desktop\\Steam.lnk", false)).toMatchObject({
      kind: "app",
      title: "Steam",
    });
    expect(itemFromPath("C:\\Users\\me\\Desktop\\Stardew Valley.url", false)).toMatchObject({
      kind: "app",
      title: "Stardew Valley",
      meta: "Приложение",
    });
    expect(itemFromPath("D:\\Work\\Plan.xlsx", false)).toMatchObject({
      kind: "file",
      title: "Plan.xlsx",
      meta: "Файл XLSX",
    });
    expect(itemFromPath("D:\\README", false).meta).toBe("Файл");
  });
});

describe("itemFromUrl", () => {
  it("adds https and names the link after the site", () => {
    expect(itemFromUrl(" www.youtube.com/feed ")).toEqual({
      kind: "url",
      title: "youtube.com",
      target: "https://www.youtube.com/feed",
      meta: "Сайт",
    });
    expect(itemFromUrl("https://ya.ru", "Яндекс")?.title).toBe("Яндекс");
    expect(itemFromUrl("mailto:me@example.com")?.target).toBe("mailto:me@example.com");
  });

  it("rejects things that are not addresses", () => {
    expect(itemFromUrl("")).toBeNull();
    expect(itemFromUrl("просто текст")).toBeNull();
    expect(itemFromUrl("hello")).toBeNull();
  });
});

describe("addItems", () => {
  it("appends tiles with ids unique across the whole config", () => {
    const [work] = defaultConfig.categories;
    const next = addItems(defaultConfig, work.id, [
      itemFromApp({ name: "Excel", id: "Microsoft.Office.EXCEL.EXE.15" }),
      itemFromApp({ name: "Excel", id: "Other" }),
      itemFromPath("D:\\Отчёты", true),
    ]);
    const added = next.categories[0].items.slice(work.items.length);
    expect(added.map((i) => i.id)).toEqual(["excel-2", "excel-3", "item"]);
    expect(validateConfig(next)).toBeNull();
    expect(next.categories.slice(1)).toEqual(defaultConfig.categories.slice(1));
  });
});
