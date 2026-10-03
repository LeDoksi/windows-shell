import { defaultConfig } from "../config/defaults";
import { validateConfig } from "../config/validate";
import type { Backend, CommandName, Commands, EventName, Events } from "./types";

type Handlers = { [C in CommandName]: (args: Commands[C]["args"]) => Commands[C]["result"] };

type Listener = (payload: unknown) => void;

/** Мок для браузерного режима и тестов. `emit` имитирует событие от Rust. */
export function createMockBackend(): Backend {
  const listeners = new Map<string, Set<Listener>>();
  const emit = <E extends EventName>(event: E, payload: Events[E]) =>
    listeners.get(event)?.forEach((l) => l(payload));

  let config = structuredClone(defaultConfig);

  const handlers: Handlers = {
    app_info: () => ({ version: "0.1.0", platform: "browser" }),
    config_get: () => structuredClone(config),
    config_set: ({ config: next }) => {
      const error = validateConfig(next);
      if (error) throw new Error(error);
      config = structuredClone(next);
      emit("config_changed", structuredClone(config));
    },
    // В браузере открыть можно только ссылку; остальное — запись в консоль.
    launch: ({ kind, target }) => {
      if (kind === "url") window.open(target, "_blank", "noopener");
      else console.info(`[mock] открыть ${kind}: ${target}`);
    },
    reveal: ({ target }) => console.info(`[mock] показать в Проводнике: ${target}`),
    paths_exist: ({ paths }) => paths.map(() => true),
    glass_rect: () => {},
  };

  return {
    kind: "mock",
    call: async (command, ...args) =>
      (handlers[command] as (a: unknown) => unknown)(args[0] ?? {}) as never,
    on: async (event, handler) => {
      const set = listeners.get(event) ?? new Set<Listener>();
      set.add(handler as Listener);
      listeners.set(event, set);
      return () => set.delete(handler as Listener);
    },
    // Слушатели вызываются синхронно: тестам и браузерному режиму так проще.
    emit: async (event, payload) => emit(event, payload),
  };
}
