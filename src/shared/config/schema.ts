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
  /** Папка, из которой собрана категория; кнопка «Открыть папку» в футере. */
  sourceFolder?: string;
  items: LauncherItem[];
};

/** Закреплённое в доке приложение. */
export type DockPin = {
  id: string;
  title: string;
  /** AUMID или путь к exe. */
  target: string;
};

/** «Стекло» (Acrylic) или «Тонировка». Только ручной выбор, без автопереключения. */
export type GlassMode = "acrylic" | "tint";

export type Config = {
  schemaVersion: 1;
  categories: Category[];
  dock: { pinned: DockPin[] };
  appearance: { glass: GlassMode };
};
