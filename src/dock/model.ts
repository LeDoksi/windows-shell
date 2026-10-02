import type { DockPin } from "../shared/config";

/** Запущенное приложение. Сейчас мок; на этапе 4 приходит из трекера окон в Rust. */
export type RunningApp = {
  id: string;
  title: string;
  windows: number;
  active?: boolean;
  /** Окно мигает, требует внимания. */
  attention?: boolean;
};

export type DockAppState = "idle" | "running" | "active" | "attention";

export type DockEntry = {
  id: string;
  title: string;
  pinned: boolean;
  state: DockAppState;
};

function stateOf(app: RunningApp | undefined): DockAppState {
  if (!app) return "idle";
  if (app.attention) return "attention";
  return app.active ? "active" : "running";
}

/** Закреплённые в своём порядке, затем незакреплённые запущенные. */
export function buildDock(pinned: DockPin[], running: RunningApp[]) {
  const byId = new Map(running.map((app) => [app.id, app]));
  const pinnedEntries: DockEntry[] = pinned.map((pin) => ({
    id: pin.id,
    title: pin.title,
    pinned: true,
    state: stateOf(byId.get(pin.id)),
  }));
  const pinnedIds = new Set(pinned.map((pin) => pin.id));
  const transient: DockEntry[] = running
    .filter((app) => !pinnedIds.has(app.id))
    .map((app) => ({ id: app.id, title: app.title, pinned: false, state: stateOf(app) }));
  return { pinned: pinnedEntries, transient };
}

/**
 * Клик по иконке, как в Windows: не запущено — запустить; запущено, но не активно — активировать;
 * активно — свернуть. Возвращает новый список запущенных (для мока).
 */
export function clickApp(running: RunningApp[], id: string, title: string): RunningApp[] {
  const app = running.find((a) => a.id === id);
  if (!app) {
    return [
      ...running.map((a) => ({ ...a, active: false })),
      { id, title, windows: 1, active: true },
    ];
  }
  if (app.active) return running.map((a) => (a.id === id ? { ...a, active: false } : a));
  return running.map((a) => ({
    ...a,
    active: a.id === id,
    attention: a.id === id ? false : a.attention,
  }));
}
