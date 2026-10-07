import { mockWindows } from "../../dock/mockWindows";
import { defaultConfig } from "../config/defaults";
import { validateConfig } from "../config/validate";
import { createMockAudio } from "./mockAudio";
import type { Backend, CommandName, Commands, EventName, Events, WindowInfo } from "./types";

type Handlers = { [C in CommandName]: (args: Commands[C]["args"]) => Commands[C]["result"] };

type Listener = (payload: unknown) => void;

/** Мок для браузерного режима и тестов. `emit` имитирует событие от Rust. */
export function createMockBackend(): Backend {
  const listeners = new Map<string, Set<Listener>>();
  const emit = <E extends EventName>(event: E, payload: Events[E]) =>
    listeners.get(event)?.forEach((l) => l(payload));

  let config = structuredClone(defaultConfig);

  // Окна программ: клики дока меняют их, как это сделала бы Windows.
  let windows = structuredClone(mockWindows);
  let clock = Math.max(...windows.map((w) => Math.max(w.seq, w.focused)));
  const setWindows = (next: WindowInfo[]) => {
    windows = next;
    emit("windows_changed", structuredClone(windows));
  };
  const activate = (hwnd: number) => {
    clock += 1;
    setWindows(
      windows.map((w) =>
        w.hwnd === hwnd
          ? { ...w, active: true, minimized: false, attention: false, focused: clock }
          : { ...w, active: false },
      ),
    );
  };

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
      if (kind !== "app") return;
      // Приложение «открылось»: новое окно с тем же AUMID, что у закреплённого.
      clock += 1;
      const title = config.dock.pinned.find((p) => p.target === target)?.title ?? target;
      const hwnd = Math.max(0, ...windows.map((w) => w.hwnd)) + 1;
      windows = [
        ...windows,
        {
          hwnd,
          title,
          aumid: target,
          exe: null,
          appName: title,
          minimized: false,
          active: false,
          attention: false,
          seq: clock,
          focused: 0,
        },
      ];
      activate(hwnd);
    },
    reveal: ({ target }) => console.info(`[mock] показать в Проводнике: ${target}`),
    paths_exist: ({ paths }) => paths.map(() => true),
    // Без точки в последней части пути — папка.
    paths_are_dirs: ({ paths }) => paths.map((p) => !/\.[^\\/.]+$/.test(p)),
    keep_shortcuts: ({ paths }) => paths,
    item_icon: () => null,
    pick_paths: ({ folders }) => [
      folders ? "C:\\Users\\Demo\\Музыка" : "C:\\Users\\Demo\\Заметки.txt",
    ],
    installed_apps: () => [
      { name: "Калькулятор", id: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" },
      { name: "Paint", id: "Microsoft.Paint_8wekyb3d8bbwe!App" },
      { name: "Блокнот", id: "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App" },
      { name: "Telegram", id: "TelegramDesktop" },
    ],
    glass_rect: () => {},
    windows_list: () => structuredClone(windows),
    window_activate: ({ hwnd }) => activate(hwnd),
    window_minimize: ({ hwnd }) =>
      setWindows(
        windows.map((w) => (w.hwnd === hwnd ? { ...w, minimized: true, active: false } : w)),
      ),
    window_close: ({ hwnd }) => setWindows(windows.filter((w) => w.hwnd !== hwnd)),
    app_targets: ({ targets }) => targets.map(() => ({ exists: true, exe: null })),
    ...createMockAudio(emit),
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
