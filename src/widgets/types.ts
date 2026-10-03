import type { ComponentType } from "react";
import type { IconName } from "../shared/ui";

/**
 * Где живёт компактный вид виджета: верхняя панель справа от категорий
 * или системный хвост дока (только звук, вместо значка Windows).
 */
export type WidgetSlot = "launcher" | "dock-tail";

/** Что получает любая часть виджета: свои настройки (с дефолтами) и способ их поменять. */
export type WidgetProps<S> = {
  settings: S;
  updateSettings: (change: (settings: S) => S) => void;
};

export type CompactProps<S> = WidgetProps<S> & {
  /** Открыт flyout этого виджета. */
  open: boolean;
  /** Открыть или закрыть flyout под (над) этой кнопкой. */
  onToggle: (anchor: HTMLElement) => void;
};

export type WidgetDef<S extends object = object> = {
  /** Тот же id, что в конфиге и у сервиса в Rust (`src-tauri/src/widgets.rs`). */
  id: string;
  name: string;
  /** Подпись в настройках: что виджет делает. */
  description: string;
  icon: IconName;
  slot: WidgetSlot;
  defaults: S;
  /** Кнопка в слоте. */
  Compact: ComponentType<CompactProps<S>>;
  /** Панель по клику. */
  Flyout?: ComponentType<WidgetProps<S>>;
  /** Свои настройки — в окне настроек виджетов. */
  Settings?: ComponentType<WidgetProps<S>>;
};

/**
 * Виджет — модуль в репозитории (docs/WIDGETS.md): манифест с интерфейсом и дефолтами.
 * Функция только проверяет типы: настройки, дефолты и компоненты согласованы.
 */
export function defineWidget<S extends object>(def: WidgetDef<S>): WidgetDef<S> {
  return def;
}

/** Виджет с любыми настройками: так их держит реестр. */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type AnyWidget = WidgetDef<any>;
