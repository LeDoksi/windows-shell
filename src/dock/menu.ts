import type { MenuEntry } from "../shared/ui";
import type { DockEntry } from "./model";

export type AppMenuActions = {
  activate: (hwnd: number) => void;
  launch: () => void;
  pin: () => void;
  unpin: () => void;
  close: (hwnds: number[]) => void;
};

/** Сколько окон показывать в меню; остальные — через Alt+Tab. */
const MAX_WINDOWS = 12;

/** Меню иконки дока: окна приложения, новое окно, закрепить или убрать, закрыть. */
export function appMenu(entry: DockEntry, a: AppMenuActions): MenuEntry[] {
  const windows: MenuEntry[] = entry.windows.slice(0, MAX_WINDOWS).map((w) => ({
    label: w.title,
    shortcut: w.minimized ? "свёрнуто" : w.active ? "активно" : undefined,
    onSelect: () => a.activate(w.hwnd),
  }));
  const count = entry.windows.length;
  return [
    ...windows,
    ...(windows.length ? [{ kind: "separator" as const }] : []),
    {
      label: count ? "Новое окно" : "Открыть",
      disabled: !entry.target || entry.missing,
      onSelect: a.launch,
    },
    entry.pinned
      ? { label: "Убрать из дока", onSelect: a.unpin }
      : { label: "Закрепить в доке", disabled: !entry.target, onSelect: a.pin },
    ...(count
      ? [
          { kind: "separator" as const },
          {
            label: count > 1 ? `Закрыть все окна (${count})` : "Закрыть окно",
            danger: true,
            onSelect: () => a.close(entry.windows.map((w) => w.hwnd)),
          },
        ]
      : []),
  ];
}
