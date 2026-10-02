import { useEffect, type RefObject } from "react";

/**
 * Закрывает поповер по Esc и по клику мимо него.
 * Клик по элементу с `data-dismiss-ignore` (например, по кнопке, которая сама переключает поповер) не считается кликом мимо.
 */
export function useDismiss(ref: RefObject<HTMLElement | null>, open: boolean, onClose: () => void) {
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    const onDown = (e: PointerEvent) => {
      const target = e.target as Element;
      if (target.closest?.("[data-dismiss-ignore]")) return;
      if (ref.current && !ref.current.contains(target)) onClose();
    };
    document.addEventListener("keydown", onKey);
    document.addEventListener("pointerdown", onDown);
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("pointerdown", onDown);
    };
  }, [ref, open, onClose]);
}
