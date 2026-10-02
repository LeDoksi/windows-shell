import { Icon } from "../shared/ui";
import type { LauncherItem } from "../shared/config";

const appColors = ["#3D8EF0", "#9B6BF0", "#3DB88C", "#F0A64A", "#F06B8E", "#4CC3D9"];

function hash(text: string) {
  let h = 0;
  for (const ch of text) h = (h * 31 + ch.charCodeAt(0)) | 0;
  return Math.abs(h);
}

/**
 * Заглушка иконки, пока нет настоящих системных иконок (этап 3).
 * Приложения — цветная плашка с буквой, остальное — глиф по типу.
 */
export function ItemIcon({ item, color }: { item: LauncherItem; color: string }) {
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
