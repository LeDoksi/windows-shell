import { findWidget } from "./registry";

/** Flyout под верхней панелью: id виджета или настройки виджетов. */
export const SETTINGS_FLYOUT = "settings";

export function panelFlyoutLabel(id: string): string {
  return id === SETTINGS_FLYOUT ? "Настройки виджетов" : (findWidget(id)?.name ?? "");
}
