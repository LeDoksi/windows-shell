import { useId, useRef, useState } from "react";
import { GlassSurface } from "./GlassSurface";
import { Icon } from "./Icon";
import { nextEnabledIndex } from "./menuNavigation";
import { useDismiss } from "./useDismiss";

export type SelectOption<V extends string> = { value: V; label: string; hint?: string };

type Props<V extends string> = {
  value: V;
  options: SelectOption<V>[];
  onChange: (value: V) => void;
  label: string;
};

export function Select<V extends string>({ value, options, onChange, label }: Props<V>) {
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const ref = useRef<HTMLDivElement>(null);
  const listId = useId();
  useDismiss(ref, open, () => setOpen(false));
  const current = options.find((o) => o.value === value);
  const disabled = options.map(() => false);

  const openList = () => {
    setActive(
      Math.max(
        0,
        options.findIndex((o) => o.value === value),
      ),
    );
    setOpen(true);
  };
  const choose = (i: number) => {
    onChange(options[i].value);
    setOpen(false);
  };

  return (
    <div className="ws-select" ref={ref}>
      <button
        type="button"
        className="ws-select__button"
        aria-label={label}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={listId}
        onClick={() => (open ? setOpen(false) : openList())}
        onKeyDown={(e) => {
          if (!open && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
            e.preventDefault();
            openList();
          } else if (open && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
            e.preventDefault();
            setActive((i) => nextEnabledIndex(disabled, i, e.key === "ArrowDown" ? 1 : -1));
          } else if (open && e.key === "Enter") {
            e.preventDefault();
            choose(active);
          }
        }}
      >
        <span>{current?.label ?? "—"}</span>
        <Icon name="chevronDown" size={14} />
      </button>
      {open && (
        <GlassSurface as="ul" level="menu" role="listbox" id={listId} className="ws-select__list">
          {options.map((o, i) => (
            <li
              key={o.value}
              role="option"
              aria-selected={o.value === value}
              className="ws-select__option"
              data-active={i === active || undefined}
              onPointerEnter={() => setActive(i)}
              onClick={() => choose(i)}
            >
              <span>{o.label}</span>
              {o.hint && <small>{o.hint}</small>}
              {o.value === value && <Icon name="check" size={14} />}
            </li>
          ))}
        </GlassSurface>
      )}
    </div>
  );
}
