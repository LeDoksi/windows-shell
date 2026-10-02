import type { ButtonHTMLAttributes } from "react";
import { Icon, type IconName } from "./Icon";
import { cx } from "./cx";

type Props = ButtonHTMLAttributes<HTMLButtonElement> & { icon: IconName; label: string };

export function IconButton({ icon, label, className, ...rest }: Props) {
  return (
    <button
      type="button"
      className={cx("ws-icon-button", className)}
      aria-label={label}
      title={label}
      {...rest}
    >
      <Icon name={icon} />
    </button>
  );
}
