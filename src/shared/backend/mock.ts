import type { Backend, CommandName, Commands, EventName, Events } from "./types";

type Handlers = { [C in CommandName]: (args: Commands[C]["args"]) => Commands[C]["result"] };

const handlers: Handlers = {
  app_info: () => ({ version: "0.1.0", platform: "browser" }),
};

type Listener = (payload: unknown) => void;

/** Мок для браузерного режима и тестов. `emit` имитирует событие от Rust. */
export function createMockBackend(): Backend & {
  emit<E extends EventName>(event: E, payload: Events[E]): void;
} {
  const listeners = new Map<string, Set<Listener>>();
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
    emit: (event, payload) => listeners.get(event)?.forEach((l) => l(payload)),
  };
}
