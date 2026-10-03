import { useCallback, useMemo } from "react";
import { useConfig } from "../shared/config";
import { widgetRegistry } from "./registry";
import {
  moveWidget,
  resolveWidgets,
  setWidgetEnabled,
  updateWidgetSettings,
  type ResolvedWidget,
} from "./resolve";

/** Виджеты из конфига (с дефолтами) и способы их поменять. Пока конфиг грузится — пусто. */
export function useWidgets() {
  const { config, update } = useConfig();
  const widgets: ResolvedWidget[] = useMemo(
    () => (config ? resolveWidgets(config.widgets, widgetRegistry) : []),
    [config],
  );
  const updateSettings = useCallback(
    (id: string, change: (s: Record<string, unknown>) => Record<string, unknown>) =>
      update((c) => updateWidgetSettings(c, widgetRegistry, id, change)),
    [update],
  );
  const setEnabled = useCallback(
    (id: string, enabled: boolean) =>
      update((c) => setWidgetEnabled(c, widgetRegistry, id, enabled)),
    [update],
  );
  const move = useCallback(
    (id: string, delta: number) => update((c) => moveWidget(c, widgetRegistry, id, delta)),
    [update],
  );
  return { ready: config !== null, widgets, updateSettings, setEnabled, move };
}
