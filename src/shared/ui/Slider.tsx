import type { CSSProperties } from "react";

type Props = {
  value: number;
  onChange: (value: number) => void;
  label: string;
  min?: number;
  max?: number;
  step?: number;
  /** Шаг колеса мыши; 0 выключает управление колесом. */
  wheelStep?: number;
  disabled?: boolean;
};

export function Slider({
  value,
  onChange,
  label,
  min = 0,
  max = 100,
  step = 1,
  wheelStep = 2,
  disabled,
}: Props) {
  const percent = ((value - min) / (max - min)) * 100;
  const clamp = (v: number) => Math.min(max, Math.max(min, v));
  return (
    <input
      type="range"
      className="ws-slider"
      aria-label={label}
      min={min}
      max={max}
      step={step}
      value={value}
      disabled={disabled}
      style={{ "--ws-slider-fill": `${percent}%` } as CSSProperties}
      onChange={(e) => onChange(Number(e.target.value))}
      onWheel={(e) => {
        if (!wheelStep || disabled) return;
        onChange(clamp(value + (e.deltaY < 0 ? wheelStep : -wheelStep)));
      }}
    />
  );
}
