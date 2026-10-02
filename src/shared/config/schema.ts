/** Конфиг оболочки, схема v1. Источник правды — Rust; во фронте только типы и дефолты. */
export type ItemKind = "app" | "file" | "folder" | "url";

export type LauncherItem = {
  id: string;
  kind: ItemKind;
  title: string;
  /** Путь, AUMID или URL. */
  target: string;
  /** Подпись под названием: «Приложение», «Документ · 2.4 МБ». */
  meta?: string;
};

export type Category = {
  id: string;
  name: string;
  color: string;
  items: LauncherItem[];
};

export type Config = {
  schemaVersion: 1;
  categories: Category[];
};
