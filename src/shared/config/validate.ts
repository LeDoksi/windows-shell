import type { Config } from "./schema";

const kinds = new Set(["app", "file", "folder", "url"]);
const glassModes = new Set(["acrylic", "tint"]);

/** Те же проверки, что `Config::validate` в Rust. Возвращает текст ошибки или null. */
export function validateConfig(config: Config): string | null {
  if (config.schemaVersion !== 1) return `неизвестная версия схемы: ${config.schemaVersion}`;
  const ids = new Set<string>();
  const unique = (scope: string, id: string) => {
    const key = `${scope}:${id}`;
    if (ids.has(key)) return `повторяется id ${scope} «${id}»`;
    ids.add(key);
    return null;
  };
  for (const category of config.categories) {
    const error = unique("category", category.id);
    if (error) return error;
    for (const item of category.items) {
      if (!kinds.has(item.kind)) return `неизвестный тип «${item.kind}» у «${item.id}»`;
      const itemError = unique("item", item.id);
      if (itemError) return itemError;
    }
  }
  for (const pin of config.dock.pinned) {
    const error = unique("pin", pin.id);
    if (error) return error;
  }
  if (!glassModes.has(config.appearance?.glass)) {
    return `неизвестный режим стекла «${config.appearance?.glass}»`;
  }
  if (
    typeof config.bars?.launcherPinned !== "boolean" ||
    typeof config.bars?.dockPinned !== "boolean"
  ) {
    return "закрепление панели и дока — да или нет";
  }
  if (config.bars.monitor !== null && typeof config.bars.monitor !== "string") {
    return "монитор — имя устройства или пусто";
  }
  return null;
}
