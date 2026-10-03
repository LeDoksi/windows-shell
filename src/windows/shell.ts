import { useEffect } from "react";
import { LogicalSize, PhysicalPosition } from "@tauri-apps/api/dpi";
import { currentMonitor, getCurrentWindow } from "@tauri-apps/api/window";
import { WebviewWindow } from "@tauri-apps/api/webviewWindow";

/** Метки окон оболочки — те же, что в `src-tauri/src/shell.rs`. */
export type WindowLabel = "launcher" | "category" | "dock" | "dock-backdrop" | "flyout" | "search";

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

/**
 * Поставить окно flyout над прямоугольником `pill` (CSS-пиксели окна дока),
 * правым краем по правому краю таблетки. У карточки отступ 12 px от краёв окна (windows.css),
 * поэтому окно опускаем на 2 px ниже верха таблетки — до карточки остаётся 10 px.
 */
export async function placeFlyoutOver(pill: DOMRect) {
  const flyout = await WebviewWindow.getByLabel("flyout");
  if (!flyout) return;
  const dpr = window.devicePixelRatio;
  const [origin, size] = await Promise.all([
    getCurrentWindow().outerPosition(),
    flyout.outerSize(),
  ]);
  await flyout.setPosition(
    new PhysicalPosition(
      Math.round(origin.x + pill.right * dpr - size.width),
      Math.round(origin.y + (pill.top + 2) * dpr - size.height),
    ),
  );
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
