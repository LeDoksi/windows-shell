import type { Config } from "../config/schema";

/** Что фронт может спросить у Rust. Ключ — имя команды `#[tauri::command]`. */
export interface Commands {
  app_info: { args: Record<string, never>; result: AppInfo };
  config_get: { args: Record<string, never>; result: Config };
  /** Заменяет конфиг целиком; Rust проверяет его и рассылает `config_changed`. */
  config_set: { args: { config: Config }; result: void };
}

/**
 * Что Rust присылает фронту. Ключ — имя события.
 * Заполняется по мере появления живых данных: окна, аудио, трей.
 */
export interface Events {
  config_changed: Config;
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
}
