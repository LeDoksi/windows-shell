import { useRef, type CSSProperties, type ReactNode } from "react";
import { GlassSurface } from "./GlassSurface";
import { cx } from "./cx";
import { useDismiss } from "./useDismiss";

type Props = {
  open: boolean;
  onClose: () => void;
  label: string;
  /** Откуда выезжает: из верхней панели вниз или из дока вверх. */
  anchor: "launcher" | "dock";
  style?: CSSProperties;
  className?: string;
  children: ReactNode;
};

export function Flyout({ open, onClose, label, anchor, style, className, children }: Props) {
  const ref = useRef<HTMLDivElement>(null);
  useDismiss(ref, open, onClose);
  return (
    <GlassSurface
      ref={ref}
      level="panel"
      role="dialog"
      aria-label={label}
      aria-hidden={!open}
      inert={!open}
      className={cx("ws-flyout", `ws-flyout--${anchor}`, open && "ws-flyout--open", className)}
      style={style}
    >
      {children}
    </GlassSurface>
  );
}
