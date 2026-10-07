import type { AppTarget, WindowInfo } from "../shared/backend";
import type { Config, DockPin } from "../shared/config";
import { moveIndex } from "../shared/ui/dragSlots";

export type DockAppState = "idle" | "running" | "active" | "attention";

export type DockEntry = {
  id: string;
  title: string;
  pinned: boolean;
  state: DockAppState;
  /** Что запускать и откуда брать иконку: AUMID или путь. */
  target: string;
  /** Окна приложения, последнее активное — первым. */
  windows: WindowInfo[];
  /** Закреплённое приложение не найдено в системе (удалили). */
  missing?: boolean;
};

const norm = (s: string | null | undefined) => (s ?? "").trim().toLowerCase();

/** Окно принадлежит закреплённому приложению: тот же AUMID или тот же exe. */
function matchesPin(w: WindowInfo, pin: DockPin, target: AppTarget | undefined) {
  const t = norm(pin.target);
  if (w.aumid && norm(w.aumid) === t) return true;
  const exe = norm(w.exe);
  return exe !== "" && (exe === t || exe === norm(target?.exe));
}

/** Чем незакреплённое приложение отличается от других: AUMID, иначе exe, иначе само окно. */
export function appKey(w: WindowInfo) {
  return norm(w.aumid) || norm(w.exe) || `hwnd:${w.hwnd}`;
}

/**
 * Что запускать для незакреплённого приложения и откуда брать иконку. У UWP exe лежит
 * в WindowsApps и сам не запускается — там нужен AUMID; у обычных программ надёжнее exe.
 * Свёрнутое UWP-окно отдаёт exe рамки, ApplicationFrameHost, — его тоже не запускаем.
 */
export function launchTarget(w: WindowInfo) {
  if (w.exe && !/\\windowsapps\\|\\applicationframehost\.exe$/i.test(w.exe)) return w.exe;
  return w.aumid ?? w.exe ?? "";
}

function stateOf(windows: WindowInfo[]): DockAppState {
  if (!windows.length) return "idle";
  if (windows.some((w) => w.attention)) return "attention";
  return windows.some((w) => w.active) ? "active" : "running";
}

const byRecent = (a: WindowInfo, b: WindowInfo) => b.focused - a.focused || a.seq - b.seq;

/**
 * Закреплённые в своём порядке, затем незакреплённые запущенные — в порядке появления.
 * Окно сначала ищет своего по AUMID, потом по exe: так окна PWA с собственным AUMID
 * не уходят к браузеру, если он закреплён по AUMID.
 */
export function buildDock(
  pins: DockPin[],
  windows: WindowInfo[],
  targets: ReadonlyMap<string, AppTarget> = new Map(),
) {
  const owner = new Map<number, string>();
  for (const pass of ["aumid", "exe"] as const) {
    for (const w of windows) {
      if (owner.has(w.hwnd)) continue;
      const pin = pins.find((p) =>
        pass === "aumid"
          ? w.aumid !== null && norm(w.aumid) === norm(p.target)
          : matchesPin(w, p, targets.get(p.target)),
      );
      if (pin) owner.set(w.hwnd, pin.id);
    }
  }

  const pinned: DockEntry[] = pins.map((pin) => {
    const own = windows.filter((w) => owner.get(w.hwnd) === pin.id).sort(byRecent);
    return {
      id: pin.id,
      title: pin.title,
      pinned: true,
      state: stateOf(own),
      target: pin.target,
      windows: own,
      missing: !own.length && targets.get(pin.target)?.exists === false,
    };
  });

  const groups = new Map<string, WindowInfo[]>();
  for (const w of windows) {
    if (owner.has(w.hwnd)) continue;
    const key = appKey(w);
    groups.set(key, [...(groups.get(key) ?? []), w]);
  }
  const transient: DockEntry[] = [...groups.entries()]
    .map(([key, own]) => {
      const first = own.reduce((a, b) => (b.seq < a.seq ? b : a));
      return {
        entry: {
          id: `run:${key}`,
          title: first.appName || first.title,
          pinned: false,
          state: stateOf(own),
          target: launchTarget(first),
          windows: [...own].sort(byRecent),
        },
        seq: first.seq,
      };
    })
    .sort((a, b) => a.seq - b.seq)
    .map(({ entry }) => entry);

  return { pinned, transient };
}

export type DockAction =
  | { kind: "launch" }
  | { kind: "activate"; hwnd: number }
  | { kind: "minimize"; hwnd: number }
  | { kind: "menu" };

/**
 * Клик по иконке, как в Windows: не запущено — запустить; запущено, но не активно — показать
 * последнее окно; активно — свернуть; активно и окон несколько — список окон.
 */
export function clickAction(entry: DockEntry): DockAction {
  const [recent] = entry.windows;
  if (!recent) return { kind: "launch" };
  const active = entry.windows.find((w) => w.active);
  if (!active) return { kind: "activate", hwnd: recent.hwnd };
  if (entry.windows.length > 1) return { kind: "menu" };
  return { kind: "minimize", hwnd: active.hwnd };
}

/**
 * Закрепить приложение на место `index` среди закреплённых (без него — в конец).
 * Уже закреплённое тем же `target` второй раз не добавляем.
 */
export function pinApp(
  config: Config,
  app: { title: string },
  target: string,
  index?: number,
): Config {
  const pinned = config.dock.pinned;
  if (pinned.some((p) => norm(p.target) === norm(target))) return config;
  const ids = new Set(pinned.map((p) => p.id));
  const base = `app-${app.title.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, "-")}`.replace(/-+$/, "");
  let id = base;
  for (let n = 2; ids.has(id); n++) id = `${base}-${n}`;
  const at = Math.max(0, Math.min(index ?? pinned.length, pinned.length));
  const pin = { id, title: app.title, target };
  return {
    ...config,
    dock: { ...config.dock, pinned: [...pinned.slice(0, at), pin, ...pinned.slice(at)] },
  };
}

/** Переставить закреплённое приложение на место `index`. */
export function movePin(config: Config, id: string, index: number): Config {
  const from = config.dock.pinned.findIndex((p) => p.id === id);
  if (from < 0 || from === index) return config;
  return {
    ...config,
    dock: { ...config.dock, pinned: moveIndex(config.dock.pinned, from, index) },
  };
}

/**
 * Что из Проводника можно закрепить в доке: программы и ярлыки, в том числе интернет-ярлыки
 * `.url` — так Steam кладёт игры на рабочий стол (док — только приложения).
 */
const programs = /\.(exe|lnk|url|appref-ms)$/i;

/** Программы из перетащенных путей — названием без расширения. */
export function programsFrom(paths: string[]): { title: string; target: string }[] {
  return paths
    .filter((p) => programs.test(p))
    .map((target) => ({
      title: (target.split(/[\\/]/).pop() ?? target).replace(programs, ""),
      target,
    }));
}

/** Убрать из дока. Само приложение не трогаем. */
export function unpinApp(config: Config, id: string): Config {
  return {
    ...config,
    dock: { ...config.dock, pinned: config.dock.pinned.filter((p) => p.id !== id) },
  };
}

/**
 * Новые цели для закрепов, которых нет в системе: приложение «Пуска» с тем же названием,
 * если оно такое одно. `id` закрепа → AUMID из «Пуска».
 */
export function repairPins(broken: DockPin[], apps: { name: string; id: string }[]) {
  const fixes = new Map<string, string>();
  for (const pin of broken) {
    const same = apps.filter((a) => norm(a.name) === norm(pin.title));
    if (same.length === 1 && norm(same[0].id) !== norm(pin.target)) fixes.set(pin.id, same[0].id);
  }
  return fixes;
}
