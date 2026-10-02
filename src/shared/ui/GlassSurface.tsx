import type { ComponentProps, ElementType } from "react";
import { cx } from "./cx";

export type GlassLevel = "bar" | "panel" | "menu";

type Props<T extends ElementType> = {
  as?: T;
  level?: GlassLevel;
} & ComponentProps<T>;

/** Единственный способ сделать стекло: тонировка, обводка, блик, тень, backdrop. */
export function GlassSurface<T extends ElementType = "div">({
  as,
  level = "bar",
  className,
  ...rest
}: Props<T>) {
  const Tag = as ?? "div";
  return <Tag className={cx("ws-glass", `ws-glass--${level}`, className)} {...rest} />;
}
