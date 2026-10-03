import { useEffect, useState } from "react";
import { backend } from "../shared/backend";
import { Icon } from "../shared/ui";
import type { LauncherItem } from "../shared/config";

const appColors = ["#3D8EF0", "#9B6BF0", "#3DB88C", "#F0A64A", "#F06B8E", "#4CC3D9"];

function hash(text: string) {
  let h = 0;
  for (const ch of text) h = (h * 31 + ch.charCodeAt(0)) | 0;
  return Math.abs(h);
}

/** Один запрос на объект за жизнь окна: плитка в категории и в поиске делят ответ. */
const icons = new Map<string, Promise<string | null>>();

function useSystemIcon({ kind, target }: LauncherItem): string | null {
  const key = `${kind}|${target}`;
  const [icon, setIcon] = useState<{ key: string; src: string | null } | null>(null);
  useEffect(() => {
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

/**
 * Иконка из Windows (256 px, как в Проводнике). Пока её нет или у объекта её не бывает
 * (ссылки, браузерный режим) — заглушка: приложения — плашка с буквой, остальное — глиф.
 */
export function ItemIcon({ item, color }: { item: LauncherItem; color: string }) {
  const src = useSystemIcon(item);
  if (src) return <img className="ws-item-icon--system" src={src} alt="" draggable={false} />;
  switch (item.kind) {
    case "app":
      return (
        <span
          className="ws-item-icon ws-item-icon--app"
          style={{ background: appColors[hash(item.id) % appColors.length] }}
          aria-hidden="true"
        >
          {item.title.charAt(0)}
        </span>
      );
    case "folder":
      return <span className="ws-folder ws-item-icon--folder" style={{ background: color }} />;
    case "file":
      return <Icon name="document" size={28} />;
    case "url":
      return <Icon name="globe" size={28} />;
  }
}
