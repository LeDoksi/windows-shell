import { useRef, useState } from "react";
import { createPortal } from "react-dom";
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
  useFileDrop,
  usePointerDrag,
  type DragPoint,
  type MenuEntry,
} from "../shared/ui";
import { cx } from "../shared/ui/cx";
import { insertionIndex, moveIndex, nearestSlot, slotOf, type Slot } from "../shared/ui/dragSlots";
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
  /** Плитку перетащили на место `index` в этой категории. */
  onMoveItem?: (itemId: string, index: number) => void;
  /**
   * Плитку тащат выше окна (к верхней панели): `move` — курсор там, `drop` — отпустили там,
   * `leave` — вернули в окно или отменили. Точка — в CSS-пикселях окна.
   */
  onDragOut?: (phase: "move" | "drop" | "leave", item: LauncherItem, p: DragPoint) => void;
  /** Из Проводника бросили файлы на место `index`. */
  onDropPaths?: (paths: string[], index: number) => void;
  /** Файлы из Проводника над окном (`true`) или ушли (`false`). */
  onFileDrag?: (over: boolean) => void;
};

/** Плитку тащат: где она была, где встанет и где курсор (для призрака под курсором). */
type TileDrag = {
  item: LauncherItem;
  from: number;
  to: number;
  outside: boolean;
  p: DragPoint;
  /** Курсор относительно угла плитки и её размер. */
  grab: { x: number; y: number; w: number; h: number };
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
  onMoveItem,
  onDragOut,
  onDropPaths,
  onFileDrag,
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

  const grid = useRef<HTMLDivElement>(null);
  /** Места плиток в начале перетаскивания: пока плитки переставляются, они не меняются. */
  const slots = useRef<Slot[]>([]);
  const snapshot = () =>
    (slots.current = [...(grid.current?.querySelectorAll("[data-tile]") ?? [])].map(slotOf));
  const [drag, setDragState] = useState<TileDrag | null>(null);
  const dragRef = useRef(drag);
  const setDrag = (d: TileDrag | null) => {
    dragRef.current = d;
    setDragState(d);
  };
  // Пока идёт поиск, порядок плиток не тот, что в категории, — переставлять нечего.
  const startDrag = usePointerDrag<LauncherItem>(
    {
      onStart: (item, p, el) => {
        const from = category.items.findIndex((i) => i.id === item.id);
        const r = el.getBoundingClientRect();
        snapshot();
        setMenu(null);
        setDrag({
          item,
          from,
          to: from,
          outside: false,
          p,
          grab: { x: p.x - r.left, y: p.y - r.top, w: r.width, h: r.height },
        });
      },
      onMove: (p) => {
        const d = dragRef.current;
        if (!d) return;
        // Выше окна — к панели категорий: туда плитку уносит панель.
        const outside = p.y < 0;
        if (outside) onDragOut?.("move", d.item, p);
        else if (d.outside) onDragOut?.("leave", d.item, p);
        const to = outside ? d.from : nearestSlot(slots.current, p.x, p.y);
        setDrag({ ...d, p, outside, to });
      },
      onEnd: (p) => {
        const d = dragRef.current;
        setDrag(null);
        if (!d) return;
        if (d.outside) onDragOut?.(p ? "drop" : "leave", d.item, p ?? d.p);
        else if (p && d.to !== d.from) onMoveItem?.(d.item.id, d.to);
      },
    },
    !query && panel === null && !editing,
  );

  /** Куда встанут файлы из Проводника; `null` — их над окном нет. */
  const [fileIndex, setFileIndex] = useState<number | null>(null);
  useFileDrop((e) => {
    if (panel !== null || editing) return;
    if (e.type === "leave") {
      setFileIndex(null);
      onFileDrag?.(false);
      return;
    }
    if (fileIndex === null) {
      snapshot();
      onFileDrag?.(true);
    }
    const index = query ? category.items.length : insertionIndex(slots.current, e.x, e.y);
    if (e.type === "over") return setFileIndex(index);
    setFileIndex(null);
    if (e.paths.length) onDropPaths?.(e.paths, index);
  });

  const dropSpot = (
    <div key="drop-spot" className="ws-tile ws-tile--drop" aria-hidden="true">
      <Icon name="plus" />
      <span className="ws-tile__meta">Сюда</span>
    </div>
  );

  const filtered = category.items.filter((item) => matchesQuery(item, query));
  const items = drag && !drag.outside ? moveIndex(filtered, drag.from, drag.to) : filtered;

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

      <div
        ref={grid}
        className="ws-category__grid"
        hidden={panel !== null}
        data-file-over={fileIndex !== null || undefined}
      >
        {items.flatMap((item, i) => [
          ...(i === fileIndex ? [dropSpot] : []),
          <Tile
            key={item.id}
            data-tile
            className={cx(drag?.item.id === item.id && "ws-tile--dragging")}
            onPointerDown={startDrag(item)}
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
          />,
        ])}
        {fileIndex !== null && fileIndex >= items.length && dropSpot}
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
      {/* Призрак под курсором — порталом: у стеклянных предков ломается position: fixed. */}
      {drag &&
        !drag.outside &&
        createPortal(
          <div
            className="ws-tile-ghost"
            style={{
              left: drag.p.x - drag.grab.x,
              top: drag.p.y - drag.grab.y,
              width: drag.grab.w,
              height: drag.grab.h,
            }}
            aria-hidden="true"
          >
            <Tile
              tabIndex={-1}
              icon={<ItemIcon item={drag.item} color={category.color} />}
              title={drag.item.title}
              meta={drag.item.meta ?? ""}
            />
          </div>,
          document.body,
        )}
    </GlassSurface>
  );
}
