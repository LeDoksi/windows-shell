import { useCallback, useEffect, useRef, type PointerEvent as ReactPointerEvent } from "react";

/** Точка курсора в CSS-пикселях окна; за краем окна — отрицательная или больше его размера. */
export type DragPoint = { x: number; y: number };

export type DragHandlers<T> = {
  /** Курсор с зажатой кнопкой ушёл дальше порога: перетаскивание началось. */
  onStart: (item: T, p: DragPoint, el: HTMLElement) => void;
  onMove: (p: DragPoint) => void;
  /** Кнопку отпустили; `null` — отменили (Esc, система забрала курсор). */
  onEnd: (p: DragPoint | null) => void;
};

/** Сколько пикселей с зажатой кнопкой — ещё клик, а не перетаскивание. */
const THRESHOLD = 6;

/**
 * Перетаскивание мышью на pointer-событиях (не HTML5 drag and drop: его в WebView2 забирает
 * приём файлов из Проводника). Курсор захватывается, поэтому события идут и за краем окна —
 * так плитку можно унести из окна категории на панель. Клик после перетаскивания гасится.
 * Возвращает `onPointerDown` для элемента `item`.
 */
export function usePointerDrag<T>(handlers: DragHandlers<T>, enabled = true) {
  const ref = useRef(handlers);
  useEffect(() => {
    ref.current = handlers;
  });
  const stop = useRef<(() => void) | null>(null);
  useEffect(() => () => stop.current?.(), []);

  return useCallback(
    (item: T) => (e: ReactPointerEvent<HTMLElement>) => {
      if (!enabled || e.button !== 0 || !e.isPrimary) return;
      stop.current?.();
      const el = e.currentTarget;
      const id = e.pointerId;
      const start = { x: e.clientX, y: e.clientY };
      let dragging = false;

      const move = (ev: PointerEvent) => {
        if (ev.pointerId !== id) return;
        const p = { x: ev.clientX, y: ev.clientY };
        if (!dragging) {
          if (Math.hypot(p.x - start.x, p.y - start.y) < THRESHOLD) return;
          dragging = true;
          try {
            el.setPointerCapture(id);
          } catch {
            // Элемент уже убрали — тогда события придут окну и так.
          }
          ref.current.onStart(item, start, el);
        }
        ref.current.onMove(p);
      };
      const finish = (p: DragPoint | null) => {
        cleanup();
        if (!dragging) return;
        // Отпускание над тем же элементом даёт ещё и click — он не должен открыть плитку.
        const swallow = (ev: MouseEvent) => {
          ev.stopPropagation();
          ev.preventDefault();
        };
        window.addEventListener("click", swallow, { capture: true, once: true });
        setTimeout(() => window.removeEventListener("click", swallow, { capture: true }), 0);
        ref.current.onEnd(p);
      };
      const up = (ev: PointerEvent) => {
        if (ev.pointerId === id) finish({ x: ev.clientX, y: ev.clientY });
      };
      const cancel = (ev: PointerEvent) => {
        if (ev.pointerId === id) finish(null);
      };
      const key = (ev: KeyboardEvent) => {
        if (ev.key !== "Escape" || !dragging) return;
        // Esc отменяет перетаскивание, а не закрывает окно категории.
        ev.stopPropagation();
        finish(null);
      };
      const cleanup = () => {
        stop.current = null;
        window.removeEventListener("pointermove", move);
        window.removeEventListener("pointerup", up);
        window.removeEventListener("pointercancel", cancel);
        window.removeEventListener("keydown", key, { capture: true });
        if (el.hasPointerCapture?.(id)) el.releasePointerCapture(id);
      };
      stop.current = cleanup;
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", up);
      window.addEventListener("pointercancel", cancel);
      window.addEventListener("keydown", key, { capture: true });
    },
    [enabled],
  );
}
