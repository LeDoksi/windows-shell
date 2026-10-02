import type { ButtonHTMLAttributes } from "react";
import { cx } from "./cx";

type Props = ButtonHTMLAttributes<HTMLButtonElement> & {
  label: string;
  color: string;
  selected?: boolean;
  dragOver?: boolean;
};

export function CategoryPill({ label, color, selected, dragOver, className, ...rest }: Props) {
  return (
    <button
      type="button"
      className={cx("ws-pill", dragOver && "ws-pill--drag-over", className)}
      aria-pressed={selected}
      {...rest}
    >
      <span className="ws-folder" style={{ background: color }} aria-hidden="true" />
      {label}
    </button>
  );
}
