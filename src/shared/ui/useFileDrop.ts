import { useEffect, useRef } from "react";
import { backend, isTauri } from "../backend";

/**
 * Файлы из Проводника над окном: `x`, `y` — курсор в CSS-пикселях окна. Пока файлы только
 * несут, их пути неизвестны (браузер их не показывает) — они приходят с `drop`.
 */
export type FileDrag =
  | { type: "over"; x: number; y: number }
  | { type: "drop"; paths: string[]; x: number; y: number }
  | { type: "leave" };

/** Что умеет WebView2 сверх браузера: передать файлы в Rust вместе с сообщением. */
type WebView2 = {
  postMessageWithAdditionalObjects?: (message: unknown, objects: unknown[]) => void;
};

const isFileDrag = (e: DragEvent) => e.dataTransfer?.types.includes("Files") ?? false;

/**
 * Пути брошенного: файлы (и папки) уходят в Rust как `File` — у него WebView2 отдаёт путь на
 * диске. `FileSystemHandle` здесь приходит в Rust пустым (тест WSH-E7). Rust отвечает событием
 * `files_dropped` с тем же `id` (`file_drop.rs`).
 */
async function dropPaths(data: DataTransfer): Promise<string[]> {
  const webview = (window as { chrome?: { webview?: WebView2 } }).chrome?.webview;
  if (!webview?.postMessageWithAdditionalObjects) return [];
  const objects = [...data.files];
  if (!objects.length) return [];
  const id = Math.floor(Math.random() * 2 ** 48);
  let answer: (paths: string[]) => void = () => {};
  const answered = new Promise<string[]>((resolve) => (answer = resolve));
  // Подписываемся до отправки: Rust отвечает быстрее, чем регистрируется слушатель.
  const off = await backend.on("files_dropped", (e) => e.id === id && answer(e.paths));
  const timer = setTimeout(() => answer([]), 3000);
  try {
    // Строкой, не объектом: обработчик IPC Tauri (wry) падает на объекте, и тогда WebView2
    // не передаёт сообщение дальше, в `file_drop.rs`.
    webview.postMessageWithAdditionalObjects(JSON.stringify({ wshDrop: id }), objects);
    return await answered;
  } finally {
    clearTimeout(timer);
    off();
  }
}

/**
 * Перетаскивание файлов из Проводника в это окно. Принимает сам WebView2 (HTML5 drag and drop),
 * поэтому курсор и координаты точные; пути отдаёт Rust после `drop`.
 */
export function useFileDrop(handler: (e: FileDrag) => void) {
  const ref = useRef(handler);
  useEffect(() => {
    ref.current = handler;
  });
  useEffect(() => {
    // В браузерном режиме (`pnpm dev:web`) путей к файлам нет.
    if (!isTauri()) return;
    // dragenter/dragleave приходят на каждый элемент под курсором: считаем вложенность.
    let depth = 0;
    const over = (e: DragEvent) => {
      if (!isFileDrag(e)) return;
      e.preventDefault();
      e.dataTransfer!.dropEffect = "copy";
      ref.current({ type: "over", x: e.clientX, y: e.clientY });
    };
    const enter = (e: DragEvent) => {
      if (!isFileDrag(e)) return;
      depth++;
      over(e);
    };
    const leave = (e: DragEvent) => {
      if (!isFileDrag(e)) return;
      depth = Math.max(0, depth - 1);
      if (depth === 0) ref.current({ type: "leave" });
    };
    const drop = (e: DragEvent) => {
      if (!isFileDrag(e)) return;
      e.preventDefault();
      depth = 0;
      const p = { x: e.clientX, y: e.clientY };
      void dropPaths(e.dataTransfer!).then(
        (paths) => ref.current({ type: "drop", paths, ...p }),
        (err: unknown) => {
          console.error(err);
          ref.current({ type: "leave" });
        },
      );
    };
    window.addEventListener("dragenter", enter);
    window.addEventListener("dragover", over);
    window.addEventListener("dragleave", leave);
    window.addEventListener("drop", drop);
    return () => {
      window.removeEventListener("dragenter", enter);
      window.removeEventListener("dragover", over);
      window.removeEventListener("dragleave", leave);
      window.removeEventListener("drop", drop);
    };
  }, []);
}
