import type { Config } from "../config/schema";
import type { FlyoutId } from "../../system/flyoutIds";
import type { SystemState } from "../../system/model";

/** Что фронт может спросить у Rust. Ключ — имя команды `#[tauri::command]`. */
export interface Commands {
  app_info: { args: Record<string, never>; result: AppInfo };
  config_get: { args: Record<string, never>; result: Config };
  /** Заменяет конфиг целиком; Rust проверяет его и рассылает `config_changed`. */
  config_set: { args: { config: Config }; result: void };
  /** Прямоугольник таблетки дока в физических пикселях окна дока: под ней встаёт подложка. */
  dock_pill: { args: { x: number; y: number; w: number; h: number }; result: void };
}

/**
 * События: от Rust и между окнами оболочки. Ключ — имя события.
 * Заполняется по мере появления живых данных: окна, аудио, трей.
 */
export interface Events {
  config_changed: Config;
  /** Панель открывает окно категории. */
  category_open: { id: string };
  /** Окно категории закрылось (Esc, клик мимо, ушёл фокус). */
  category_closed: null;
  /** Док открывает flyout системного хвоста и передаёт текущее состояние. */
  flyout_open: { id: FlyoutId; state: SystemState };
  flyout_closed: null;
  /** Flyout поменял состояние (громкость, Wi-Fi) — док подхватывает. */
  system_state: SystemState;
}

export interface AppInfo {
  version: string;
  /** "windows" в приложении, "browser" в браузерном режиме. */
  platform: string;
}

export type CommandName = keyof Commands;
export type EventName = keyof Events;
export type Unlisten = () => void;

/**
 * Единственная дверь из UI в систему. Компоненты не зовут `invoke` напрямую,
 * поэтому тот же UI работает и в Tauri, и в обычном браузере на моках.
 */
export interface Backend {
  readonly kind: "tauri" | "mock";
  call<C extends CommandName>(
    command: C,
    ...args: Record<string, never> extends Commands[C]["args"]
      ? [args?: Commands[C]["args"]]
      : [args: Commands[C]["args"]]
  ): Promise<Commands[C]["result"]>;
  on<E extends EventName>(event: E, handler: (payload: Events[E]) => void): Promise<Unlisten>;
  /** Событие всем окнам (и себе). */
  emit<E extends EventName>(event: E, payload: Events[E]): Promise<void>;
}
