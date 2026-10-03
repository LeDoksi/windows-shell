import { useEffect, useState } from "react";
import { backend } from "./backend";
import type { ItemKind } from "./config";

/** Один запрос на объект за жизнь окна: плитка, поиск и док делят ответ. */
const icons = new Map<string, Promise<string | null>>();

/** Иконка объекта из Windows (`data:`-URL) или `null`, пока её нет или у объекта её не бывает. */
export function useSystemIcon(kind: ItemKind, target: string): string | null {
  const key = `${kind}|${target}`;
  const [icon, setIcon] = useState<{ key: string; src: string | null } | null>(null);
  useEffect(() => {
    if (!target) return;
    let alive = true;
    let request = icons.get(key);
    if (!request) {
      request = backend.call("item_icon", { kind, target }).catch(() => null);
      icons.set(key, request);
    }
    void request.then((src) => alive && setIcon({ key, src }));
    return () => {
      alive = false;
    };
  }, [key, kind, target]);
  return icon?.key === key ? icon.src : null;
}
