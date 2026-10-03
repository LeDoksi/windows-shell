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
import { AppPicker, LinkForm } from "./AddPanels";
import { itemFromPath, type NewItem } from "./add";
import { categoryColors } from "./categories";
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
  onAddItems: (items: NewItem[]) => void;
  /** Системный диалог выбора; пустой ответ — пользователь передумал. */
  onPickPaths: (folders: boolean) => Promise<string[]>;
  onOpenFolder: (path: string) => void;
  /** Id плиток, чьих файлов нет на диске: рисуются приглушёнными. */
  missing?: ReadonlySet<string>;
  /** Открыть сразу в режиме правки (новая категория). */
  startEditing?: boolean;
  /** Крайняя слева / справа на панели: сдвигать дальше некуда. */
  isFirst?: boolean;
  isLast?: boolean;
  onRename: (name: string) => void;
  onRecolor: (color: string) => void;
  onMove: (delta: -1 | 1) => void;
  /** Убирает категорию из конфига вместе с плитками; файлы на диске остаются. */
  onDelete: () => void;
};

export function CategoryWindow({
  category,
  onClose,
  onOpenItem,
  onRevealItem,
  onRemoveItem,
  onAddItems,
  onPickPaths,
  onOpenFolder,
  missing,
  startEditing = false,
  isFirst,
  isLast,
  onRename,
  onRecolor,
  onMove,
  onDelete,
}: Props) {
  const ref = useRef<HTMLDivElement>(null);
  const [query, setQuery] = useState("");
  const [menu, setMenu] = useState<{ item: LauncherItem; x: number; y: number } | null>(null);
  const [addMenu, setAddMenu] = useState<{ x: number; y: number } | null>(null);
  /** Вместо сетки — выбор приложений или форма ссылки. */
  const [panel, setPanel] = useState<"app" | "link" | null>(null);
  const [editing, setEditing] = useState(startEditing);
  const [name, setName] = useState(category.name);
  const [confirmDelete, setConfirmDelete] = useState(false);
  // Пока открыто меню, панель «＋» или правка, Esc и клик мимо закрывают только их.
  useDismiss(ref, menu === null && addMenu === null && panel === null && !editing, onClose);

  const finishEditing = () => {
    if (name.trim() && name.trim() !== category.name) onRename(name);
    else setName(category.name);
    setEditing(false);
    setConfirmDelete(false);
  };

  const add = (items: NewItem[]) => {
    setPanel(null);
    if (items.length) onAddItems(items);
  };
  const pick = (folders: boolean) =>
    void onPickPaths(folders).then((paths) => add(paths.map((p) => itemFromPath(p, folders))));
  const openAddMenu = (e: React.MouseEvent) => setAddMenu({ x: e.clientX, y: e.clientY });
  const addEntries: MenuEntry[] = [
    { label: "Файл…", onSelect: () => pick(false) },
    { label: "Папку…", onSelect: () => pick(true) },
    { label: "Приложение…", onSelect: () => setPanel("app") },
    { label: "Ссылку…", onSelect: () => setPanel("link") },
  ];

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
      {editing ? (
        <div
          className="ws-category__editor"
          onKeyDown={(e) => {
            if (e.key === "Escape" || e.key === "Enter") {
              e.stopPropagation();
              finishEditing();
            }
          }}
        >
          <header className="ws-category__head">
            <span
              className="ws-folder ws-category__folder"
              style={{ background: category.color }}
              aria-hidden="true"
            />
            <label className="ws-search ws-category__name">
              <input
                value={name}
                onChange={(e) => setName(e.target.value)}
                aria-label="Название категории"
                placeholder="Название"
                autoFocus
                onFocus={(e) => e.target.select()}
              />
            </label>
            <button type="button" className="ws-button ws-button--accent" onClick={finishEditing}>
              Готово
            </button>
          </header>
          <div className="ws-category__tools">
            <div className="ws-category__colors" role="radiogroup" aria-label="Цвет">
              {categoryColors.map((color) => (
                <button
                  key={color}
                  type="button"
                  role="radio"
                  aria-checked={color.toUpperCase() === category.color.toUpperCase()}
                  aria-label={color}
                  className="ws-category__color"
                  style={{ background: color }}
                  onClick={() => onRecolor(color)}
                />
              ))}
            </div>
            <button
              type="button"
              className="ws-button"
              disabled={isFirst}
              onClick={() => onMove(-1)}
            >
              Левее
            </button>
            <button type="button" className="ws-button" disabled={isLast} onClick={() => onMove(1)}>
              Правее
            </button>
            <button
              type="button"
              className="ws-button ws-button--danger"
              onClick={() => (confirmDelete ? onDelete() : setConfirmDelete(true))}
            >
              {confirmDelete ? "Точно удалить?" : "Удалить"}
            </button>
          </div>
        </div>
      ) : (
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
          <IconButton
            icon="settings"
            label="Изменить категорию"
            onClick={() => {
              setName(category.name);
              setEditing(true);
            }}
          />
          <IconButton icon="close" label="Закрыть" onClick={onClose} />
        </header>
      )}

      {panel === "app" && (
        <AppPicker color={category.color} onAdd={add} onCancel={() => setPanel(null)} />
      )}
      {panel === "link" && <LinkForm onAdd={add} onCancel={() => setPanel(null)} />}

      <div className="ws-category__grid" hidden={panel !== null}>
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
        {!query && <AddTile onClick={openAddMenu} />}
        {query && items.length === 0 && <p className="ws-category__empty">Ничего не нашлось</p>}
      </div>

      <footer className="ws-category__foot" hidden={panel !== null}>
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
        <button type="button" className="ws-category__link" onClick={openAddMenu}>
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
      <ContextMenu
        open={addMenu !== null}
        x={addMenu?.x ?? 0}
        y={addMenu?.y ?? 0}
        items={addEntries}
        label="Добавить"
        onClose={() => setAddMenu(null)}
      />
    </GlassSurface>
  );
}
