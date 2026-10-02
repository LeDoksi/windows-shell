import { useRef, useState, type ReactNode } from "react";
import { cx } from "./cx";

type Props = {
  text: string;
  placement?: "top" | "bottom";
  delay?: number;
  children: ReactNode;
};

/** Подсказка с задержкой 500 мс, как у иконок дока. */
export function Tooltip({ text, placement = "top", delay = 500, children }: Props) {
  const [shown, setShown] = useState(false);
  const timer = useRef<number | undefined>(undefined);
  const show = () => {
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setShown(true), delay);
  };
  const hide = () => {
    window.clearTimeout(timer.current);
    setShown(false);
  };
  return (
    <span
      className="ws-tooltip-host"
      onPointerEnter={show}
      onPointerLeave={hide}
      onFocus={show}
      onBlur={hide}
    >
      {children}
      <span
        role="tooltip"
        className={cx("ws-tooltip", `ws-tooltip--${placement}`, shown && "ws-tooltip--shown")}
      >
        {text}
      </span>
    </span>
  );
}
