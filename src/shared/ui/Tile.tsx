import type { ButtonHTMLAttributes, ReactNode } from "react";
import { Icon } from "./Icon";
import { cx } from "./cx";

type Props = ButtonHTMLAttributes<HTMLButtonElement> & {
  icon: ReactNode;
  title: string;
  meta: string;
  selected?: boolean;
  /** Объект на диске не найден: плитка остаётся, но приглушена. */
  missing?: boolean;
};

export function Tile({ icon, title, meta, selected, missing, className, ...rest }: Props) {
  return (
    <button
      type="button"
      className={cx(
        "ws-tile",
        selected && "ws-tile--selected",
        missing && "ws-tile--missing",
        className,
      )}
      {...rest}
    >
      <span className="ws-tile__icon">{icon}</span>
      <span className="ws-tile__title">{title}</span>
      <span className="ws-tile__meta">{missing ? "Не найдено" : meta}</span>
    </button>
  );
}

export function AddTile(props: ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button type="button" className="ws-tile ws-tile--add" {...props}>
      <Icon name="plus" />
      <span className="ws-tile__meta">Добавить</span>
    </button>
  );
}
