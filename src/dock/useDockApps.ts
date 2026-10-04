import { useCallback, useEffect, useMemo, useState } from "react";
import { backend, type AppTarget, type WindowInfo } from "../shared/backend";
import { useConfig } from "../shared/config";
import { appMenu } from "./menu";
import {
  buildDock,
  clickAction,
  launchTarget,
  movePin,
  pinApp,
  programsFrom,
  unpinApp,
  type DockEntry,
} from "./model";

const report = (e: unknown) => console.error(e);

/** Окна программ: первый список по запросу, дальше — события от трекера. */
function useWindows() {
  const [windows, setWindows] = useState<WindowInfo[]>([]);
  useEffect(() => {
    let alive = true;
    const off = backend.on("windows_changed", (list) => alive && setWindows(list));
    void backend.call("windows_list").then((list) => alive && setWindows(list), report);
    return () => {
      alive = false;
      void off.then((f) => f());
    };
  }, []);
  return windows;
}

/** Установлены ли закреплённые и где их exe — чтобы узнать их окна и пометить удалённые. */
function useTargets(targets: string[]) {
  const key = JSON.stringify(targets);
  const [answer, setAnswer] = useState<{ key: string; map: Map<string, AppTarget> }>();
  useEffect(() => {
    let alive = true;
    const list = JSON.parse(key) as string[];
    void backend.call("app_targets", { targets: list }).then((result) => {
      if (alive) setAnswer({ key, map: new Map(list.map((t, i) => [t, result[i]])) });
    }, report);
    return () => {
      alive = false;
    };
  }, [key]);
  return answer?.map ?? new Map<string, AppTarget>();
}

/** Чем закрепить запущенное приложение: AUMID, если он есть в «Пуске», иначе exe. */
async function pinTarget(entry: DockEntry) {
  const [w] = entry.windows;
  if (!w) return entry.target;
  if (w.aumid) {
    const [found] = await backend.call("app_targets", { targets: [w.aumid] });
    if (found?.exists) return w.aumid;
  }
  return launchTarget(w);
}

/** Док из конфига и живых окон, плюс что делать по клику и в меню. */
export function useDockApps() {
  const { config, update } = useConfig();
  const windows = useWindows();
  const pins = useMemo(() => config?.dock.pinned ?? [], [config]);
  const targets = useTargets(pins.map((p) => p.target));
  const dock = useMemo(() => buildDock(pins, windows, targets), [pins, windows, targets]);

  const launch = useCallback(
    (entry: DockEntry) =>
      void backend.call("launch", { kind: "app", target: entry.target }).catch(report),
    [],
  );

  /** Клик; `"menu"` — вызывающий сам открывает меню приложения. */
  const click = useCallback(
    (entry: DockEntry) => {
      const action = clickAction(entry);
      switch (action.kind) {
        case "launch":
          if (!entry.missing) launch(entry);
          break;
        case "activate":
          void backend.call("window_activate", { hwnd: action.hwnd }).catch(report);
          break;
        case "minimize":
          void backend.call("window_minimize", { hwnd: action.hwnd }).catch(report);
          break;
      }
      return action.kind;
    },
    [launch],
  );

  const menu = useCallback(
    (entry: DockEntry) =>
      appMenu(entry, {
        activate: (hwnd) => void backend.call("window_activate", { hwnd }).catch(report),
        launch: () => launch(entry),
        pin: () =>
          void pinTarget(entry)
            .then((target) => update((c) => pinApp(c, entry, target)))
            .catch(report),
        unpin: () => void update((c) => unpinApp(c, entry.id)).catch(report),
        close: (hwnds) =>
          hwnds.forEach((hwnd) => void backend.call("window_close", { hwnd }).catch(report)),
      }),
    [launch, update],
  );

  /** Перетаскивание: переставить закреплённое, закрепить запущенное или программы из Проводника. */
  const reorder = useMemo(
    () => ({
      move: (entry: DockEntry, index: number) =>
        void update((c) => movePin(c, entry.id, index)).catch(report),
      pin: (entry: DockEntry, index: number) =>
        void pinTarget(entry)
          .then((target) => update((c) => pinApp(c, entry, target, index)))
          .catch(report),
      pinPaths: (paths: string[], index: number) =>
        void update((c) =>
          programsFrom(paths).reduce((next, app, i) => pinApp(next, app, app.target, index + i), c),
        ).catch(report),
    }),
    [update],
  );

  const find = useCallback(
    (id: string) => [...dock.pinned, ...dock.transient].find((e) => e.id === id),
    [dock],
  );

  return { ready: config !== null, ...dock, click, launch, menu, find, reorder };
}
