import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { GlassSurface } from "./GlassSurface";
import { cx } from "./cx";
import { nextEnabledIndex } from "./menuNavigation";
import { useDismiss } from "./useDismiss";

export type MenuEntry =
  | { kind: "separator" }
  | {
      kind?: "item";
      label: string;
      shortcut?: string;
      danger?: boolean;
      disabled?: boolean;
      onSelect: () => void;
    };

type Props = {
  open: boolean;
  x: number;
  y: number;
  items: MenuEntry[];
  onClose: () => void;
  label?: string;
};

export function ContextMenu({ open, x, y, items, onClose, label = "Меню" }: Props) {
  const ref = useRef<HTMLDivElement>(null);
  useDismiss(ref, open, onClose);

  const actionable = items.flatMap((item) => (item.kind === "separator" ? [] : [item]));
  const disabled = actionable.map((item) => Boolean(item.disabled));
  const first = nextEnabledIndex(disabled, -1, 1);
  const [active, setActive] = useState(open ? first : -1);

  // Фокус сбрасываем только при открытии: подстраиваем состояние во время рендера.
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) setActive(first);
  }

  useEffect(() => {
    if (!open) return;
    ref.current?.querySelectorAll<HTMLButtonElement>("[role=menuitem]")[active]?.focus();
  }, [open, active]);

  if (!open) return null;

  const actionIndex = items.map(
    (_, i) => i - items.slice(0, i + 1).filter((entry) => entry.kind === "separator").length,
  );
  // Портал: у стеклянных предков backdrop-filter ломает position: fixed.
  return createPortal(
    <GlassSurface
      ref={ref}
      level="menu"
      role="menu"
      aria-label={label}
      className="ws-menu"
      style={{ left: x, top: y }}
      onKeyDown={(e: React.KeyboardEvent) => {
        if (e.key === "ArrowDown" || e.key === "ArrowUp") {
          e.preventDefault();
          setActive((i) => nextEnabledIndex(disabled, i, e.key === "ArrowDown" ? 1 : -1));
        }
      }}
    >
      {items.map((item, i) => {
        if (item.kind === "separator") return <hr key={`sep-${i}`} />;
        const own = actionIndex[i];
        return (
          <button
            key={item.label}
            type="button"
            role="menuitem"
            tabIndex={own === active ? 0 : -1}
            disabled={item.disabled}
            className={cx("ws-menu__item", item.danger && "ws-menu__item--danger")}
            onMouseEnter={() => !item.disabled && setActive(own)}
            onClick={() => {
              item.onSelect();
              onClose();
            }}
          >
            <span>{item.label}</span>
            {item.shortcut && <kbd>{item.shortcut}</kbd>}
          </button>
        );
      })}
    </GlassSurface>,
    document.body,
  );
}
