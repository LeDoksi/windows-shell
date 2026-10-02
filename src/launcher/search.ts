import type { LauncherItem } from "../shared/config";

export function matchesQuery(item: LauncherItem, query: string) {
  const q = query.trim().toLocaleLowerCase();
  return !q || item.title.toLocaleLowerCase().includes(q);
}
