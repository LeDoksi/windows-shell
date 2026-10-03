import { useRef, useState } from "react";
import type { Category, LauncherItem } from "../shared/config";
import {
  AddTile,
  ContextMenu,
  GlassSurface,
  Icon,
  IconButton,
  SearchField,
  Tile,
  useDismiss,
  type MenuEntry,
} from "../shared/ui";
import { ItemIcon } from "./ItemIcon";
import { canReveal } from "./items";
import { matchesQuery } from "./search";

type Props = {
  category: Category;
  onClose: () => void;
  onOpenItem: (item: LauncherItem) => void;
  onRevealItem: (item: LauncherItem) => void;
  /** Убирает запись из конфига. Сам объект на диске не трогается. */
  onRemoveItem: (item: LauncherItem) => void;
  onAdd: () => void;
  onOpenFolder: (path: string) => void;
  /** Id плиток, чьих файлов нет на диске: рисуются приглушёнными. */
  missing?: ReadonlySet<string>;
};

export function CategoryWindow({
  category,
  onClose,
  onOpenItem,
  onRevealItem,
  onRemoveItem,
  onAdd,
  onOpenFolder,
  missing,
}: Props) {
  const ref = useRef<HTMLDivElement>(null);
  const [query, setQuery] = useState("");
  const [menu, setMenu] = useState<{ item: LauncherItem; x: number; y: number } | null>(null);
  // Пока открыто меню плитки, Esc и клик мимо закрывают только его.
  useDismiss(ref, menu === null, onClose);

  const items = category.items.filter((item) => matchesQuery(item, query));

  const menuItems: MenuEntry[] = menu
    ? [
        { label: "Открыть", shortcut: "Enter", onSelect: () => onOpenItem(menu.item) },
        {
          label: "Открыть расположение",
          disabled: !canReveal(menu.item) || missing?.has(menu.item.id),
          onSelect: () => onRevealItem(menu.item),
        },
        { kind: "separator" },
        { label: "Удалить из панели", danger: true, onSelect: () => onRemoveItem(menu.item) },
      ]
    : [];

  return (
    <GlassSurface
      ref={ref}
      level="panel"
      role="dialog"
      aria-label={category.name}
      className="ws-category"
    >
      <header className="ws-category__head">
        <span
          className="ws-folder ws-category__folder"
          style={{ background: category.color }}
          aria-hidden="true"
        />
        <h2 className="ws-category__title">{category.name}</h2>
        <SearchField
          value={query}
          onChange={setQuery}
          placeholder="Поиск в категории"
          aria-label="Поиск в категории"
          className="ws-category__search"
          autoFocus
        />
        <IconButton icon="close" label="Закрыть" onClick={onClose} />
      </header>

      <div className="ws-category__grid">
        {items.map((item) => (
          <Tile
            key={item.id}
            icon={<ItemIcon item={item} color={category.color} />}
            title={item.title}
            meta={item.meta ?? ""}
            selected={menu?.item.id === item.id}
            missing={missing?.has(item.id)}
            onClick={() => onOpenItem(item)}
            onContextMenu={(e) => {
              e.preventDefault();
              setMenu({ item, x: e.clientX, y: e.clientY });
            }}
          />
        ))}
        {!query && <AddTile onClick={onAdd} />}
        {query && items.length === 0 && <p className="ws-category__empty">Ничего не нашлось</p>}
      </div>

      <footer className="ws-category__foot">
        {category.sourceFolder && (
          <button
            type="button"
            className="ws-category__link"
            onClick={() => onOpenFolder(category.sourceFolder!)}
          >
            <Icon name="folderOpen" size={14} />
            Открыть папку
          </button>
        )}
        <button type="button" className="ws-category__link" onClick={onAdd}>
          <Icon name="plus" size={14} />
          Добавить
        </button>
      </footer>

      <ContextMenu
        open={menu !== null}
        x={menu?.x ?? 0}
        y={menu?.y ?? 0}
        items={menuItems}
        label={menu?.item.title}
        onClose={() => setMenu(null)}
      />
    </GlassSurface>
  );
}
