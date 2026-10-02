import type { InputHTMLAttributes } from "react";
import { Icon } from "./Icon";
import { cx } from "./cx";

type Props = Omit<InputHTMLAttributes<HTMLInputElement>, "onChange"> & {
  value: string;
  onChange: (value: string) => void;
};

/** Поле поиска. Esc очищает значение. */
export function SearchField({ value, onChange, className, ...rest }: Props) {
  return (
    <label className={cx("ws-search", className)}>
      <Icon name="search" size={14} />
      <input
        type="search"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Escape" && value) {
            e.stopPropagation();
            onChange("");
          }
        }}
        {...rest}
      />
    </label>
  );
}
