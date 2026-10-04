import { useEffect, useRef } from "react";
import { getCurrentWebview } from "@tauri-apps/api/webview";
import { isTauri } from "../backend";

/** Файлы из Проводника над окном: `x`, `y` — курсор в CSS-пикселях окна. */
export type FileDrag =
  | { type: "over"; paths: string[]; x: number; y: number }
  | { type: "drop"; paths: string[]; x: number; y: number }
  | { type: "leave" };

/**
 * Перетаскивание файлов из Проводника в это окно (Tauri отдаёт пути и курсор в физических
 * пикселях). `enter` и `over` приходят одним `over` — уже с путями.
 */
export function useFileDrop(handler: (e: FileDrag) => void) {
  const ref = useRef(handler);
  useEffect(() => {
    ref.current = handler;
  });
  useEffect(() => {
    // В браузерном режиме (`pnpm dev:web`) файлы из Проводника не приходят.
    if (!isTauri()) return;
    let paths: string[] = [];
    const off = getCurrentWebview().onDragDropEvent(({ payload: e }) => {
      if (e.type === "leave") {
        paths = [];
        return ref.current({ type: "leave" });
      }
      if (e.type !== "over") paths = e.paths;
      const d = window.devicePixelRatio;
      const p = { x: e.position.x / d, y: e.position.y / d };
      ref.current(
        e.type === "drop" ? { type: "drop", paths, ...p } : { type: "over", paths, ...p },
      );
      if (e.type === "drop") paths = [];
    });
    return () => {
      void off.then((f) => f());
    };
  }, []);
}
