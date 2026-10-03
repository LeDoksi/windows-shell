import { useEffect } from "react";
import { LogicalSize, PhysicalPosition, PhysicalSize } from "@tauri-apps/api/dpi";
import { currentMonitor, getCurrentWindow } from "@tauri-apps/api/window";
import { WebviewWindow } from "@tauri-apps/api/webviewWindow";

/** Метки окон оболочки — те же, что в `src-tauri/src/shell.rs`. */
export type WindowLabel =
  | "launcher"
  | "launcher-backdrop"
  | "category"
  | "dock"
  | "dock-backdrop"
  | "dock-menu"
  | "flyout"
  | "panel-flyout"
  | "search";

export async function showWindow(label: WindowLabel, focus = true) {
  const w = await WebviewWindow.getByLabel(label);
  await w?.show();
  if (focus) await w?.setFocus();
}

export async function hideWindow(label: WindowLabel) {
  await (await WebviewWindow.getByLabel(label))?.hide();
}

/** Поповер закрывается, когда его окно теряет фокус (клик в другое окно или приложение). */
export function useCloseOnBlur(open: boolean, onClose: () => void) {
  useEffect(() => {
    if (!open) return;
    const off = getCurrentWindow().onFocusChanged(({ payload: focused }) => {
      if (!focused) onClose();
    });
    return () => {
      off.then((f) => f());
    };
  }, [open, onClose]);
}

/** Куда ставить карточку поповера: её правый край и верх или низ, физические пиксели экрана. */
export type Anchor = { right: number; top: number } | { right: number; bottom: number };

/** Точка окна (CSS-пиксели) в физических пикселях экрана. */
export async function toScreen(x: number, y: number) {
  const dpr = window.devicePixelRatio;
  const origin = await getCurrentWindow().outerPosition();
  return { x: Math.round(origin.x + x * dpr), y: Math.round(origin.y + y * dpr) };
}

/**
 * Окно по размеру карточки `el` плюс поле `margin` под тень; карточка стоит в окне с отступом
 * `margin` (windows.css), окно — так, чтобы карточка встала в `anchor`. Прозрачная часть окна
 * перехватывала бы клики по чужим окнам, поэтому окно не больше карточки.
 */
export async function fitWindowAt(el: Element, margin: number, anchor: Anchor) {
  const dpr = window.devicePixelRatio;
  const r = el.getBoundingClientRect();
  const w = Math.ceil((r.width + margin * 2) * dpr);
  const h = Math.ceil((r.height + margin * 2) * dpr);
  const win = getCurrentWindow();
  await win.setSize(new PhysicalSize(w, h));
  const m = Math.round(margin * dpr);
  const y = "top" in anchor ? anchor.top - m : anchor.bottom - h + m;
  await win.setPosition(new PhysicalPosition(anchor.right - w + m, y));
}

/**
 * Поповер, который сам подгоняет своё окно: по карточке `selector`, к точке `anchor`.
 * Пока открыт — следит за размером (появились приложения в микшере, раскрыли настройки).
 * Показывается, когда уже на месте: без прыжка из старой позиции.
 */
export function useAnchoredWindow(selector: string, margin: number, anchor: Anchor | null) {
  useEffect(() => {
    if (!anchor) return;
    const el = document.querySelector(selector);
    if (!el) return;
    let shown = false;
    const fit = async () => {
      await fitWindowAt(el, margin, anchor);
      if (shown) return;
      shown = true;
      const win = getCurrentWindow();
      await win.show();
      await win.setFocus();
    };
    const observer = new ResizeObserver(() => void fit().catch(console.error));
    observer.observe(el);
    return () => observer.disconnect();
  }, [selector, margin, anchor]);
}

/**
 * Окно по размеру элемента `selector` плюс поле `margin` под тень, по центру верха монитора.
 * Прозрачная часть окна перехватывает клики — поэтому окно панели не шире самой панели.
 */
export function useFitWindowTop(selector: string, margin: number, height: number) {
  useEffect(() => {
    const el = document.querySelector(selector);
    if (!el) return;
    const fit = async () => {
      const width = Math.ceil(el.getBoundingClientRect().width) + margin * 2;
      const win = getCurrentWindow();
      const monitor = await currentMonitor();
      if (!monitor) return;
      const { position, size } = monitor.workArea;
      await win.setSize(new LogicalSize(width, height));
      const physical = Math.round(width * monitor.scaleFactor);
      await win.setPosition(
        new PhysicalPosition(position.x + Math.round((size.width - physical) / 2), position.y),
      );
    };
    const observer = new ResizeObserver(() => void fit());
    observer.observe(el);
    return () => observer.disconnect();
  });
}

/** Середина верхнего края элемента окна в физических пикселях экрана. */
export async function screenTopCenter(el: Element, gap: number) {
  const dpr = window.devicePixelRatio;
  const origin = await getCurrentWindow().outerPosition();
  const r = el.getBoundingClientRect();
  return {
    x: Math.round(origin.x + (r.left + r.width / 2) * dpr),
    y: Math.round(origin.y + (r.top - gap) * dpr),
  };
}

/**
 * Окно по размеру элемента `el` (плюс поле `margin` под тень), нижней серединой в точку
 * `(x, y)` экрана: прозрачная часть окна перехватывала бы клики по чужим окнам.
 */
export async function fitWindowAbove(el: Element, margin: number, x: number, y: number) {
  const dpr = window.devicePixelRatio;
  const r = el.getBoundingClientRect();
  const w = Math.ceil((r.width + margin * 2) * dpr);
  const h = Math.ceil((r.height + margin * 2) * dpr);
  const win = getCurrentWindow();
  await win.setSize(new PhysicalSize(w, h));
  await win.setPosition(new PhysicalPosition(Math.round(x - w / 2), y - h));
}
