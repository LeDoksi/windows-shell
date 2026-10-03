import type { Config, WidgetEntry } from "../shared/config";
import type { AnyWidget, WidgetSlot } from "./types";

/** Виджет, как его видят панель и настройки: манифест, включён ли, настройки с дефолтами. */
export type ResolvedWidget = {
  def: AnyWidget;
  enabled: boolean;
  settings: Record<string, unknown>;
};

/**
 * Порядок из конфига; неизвестные id (виджет убрали из сборки) пропускаем, а виджеты,
 * которых в конфиге ещё нет (появились в новой версии), — в конец, включёнными.
 */
export function resolveWidgets(entries: WidgetEntry[], registry: AnyWidget[]): ResolvedWidget[] {
  const known = new Map(registry.map((def) => [def.id, def]));
  const out: ResolvedWidget[] = [];
  for (const entry of entries) {
    const def = known.get(entry.id);
    if (!def || out.some((w) => w.def.id === def.id)) continue;
    out.push({ def, enabled: entry.enabled, settings: { ...def.defaults, ...entry.settings } });
  }
  for (const def of registry) {
    if (!out.some((w) => w.def.id === def.id)) {
      out.push({ def, enabled: true, settings: { ...def.defaults } });
    }
  }
  return out;
}

export function inSlot(widgets: ResolvedWidget[], slot: WidgetSlot): ResolvedWidget[] {
  return widgets.filter((w) => w.enabled && w.def.slot === slot);
}

/** Записать в конфиг полный список виджетов (с теми, которых там ещё не было). */
function withWidgets(
  config: Config,
  registry: AnyWidget[],
  change: (list: WidgetEntry[]) => WidgetEntry[],
): Config {
  const list = resolveWidgets(config.widgets, registry).map((w) => ({
    id: w.def.id,
    enabled: w.enabled,
    settings: config.widgets.find((e) => e.id === w.def.id)?.settings ?? {},
  }));
  // Записи о виджетах, которых нет в этой сборке, не теряем: вдруг их вернут.
  const unknown = config.widgets.filter((e) => !registry.some((d) => d.id === e.id));
  return { ...config, widgets: [...change(list), ...unknown] };
}

export function setWidgetEnabled(
  config: Config,
  registry: AnyWidget[],
  id: string,
  enabled: boolean,
): Config {
  return withWidgets(config, registry, (list) =>
    list.map((e) => (e.id === id ? { ...e, enabled } : e)),
  );
}

/** Сдвинуть виджет на `delta` мест среди виджетов того же слота. */
export function moveWidget(
  config: Config,
  registry: AnyWidget[],
  id: string,
  delta: number,
): Config {
  return withWidgets(config, registry, (list) => {
    const slotOf = (wid: string) => registry.find((d) => d.id === wid)?.slot;
    const slot = slotOf(id);
    const same = list.map((e, i) => ({ e, i })).filter(({ e }) => slotOf(e.id) === slot);
    const from = same.findIndex(({ e }) => e.id === id);
    const to = from + delta;
    if (from < 0 || to < 0 || to >= same.length) return list;
    const next = [...list];
    const [a, b] = [same[from].i, same[to].i];
    [next[a], next[b]] = [next[b], next[a]];
    return next;
  });
}

/** Поменять настройки виджета; в конфиг пишутся настройки целиком, с дефолтами. */
export function updateWidgetSettings(
  config: Config,
  registry: AnyWidget[],
  id: string,
  change: (settings: Record<string, unknown>) => Record<string, unknown>,
): Config {
  const current = resolveWidgets(config.widgets, registry).find((w) => w.def.id === id);
  if (!current) return config;
  const settings = change(current.settings);
  return withWidgets(config, registry, (list) =>
    list.map((e) => (e.id === id ? { ...e, settings } : e)),
  );
}
