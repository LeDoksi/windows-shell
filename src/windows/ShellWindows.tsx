import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { Dock } from "../dock/Dock";
import type { DockEntry } from "../dock/model";
import { useDockApps } from "../dock/useDockApps";
import { CategoryWindow } from "../launcher/CategoryWindow";
import { ItemIcon } from "../launcher/ItemIcon";
import { TopPanel } from "../launcher/TopPanel";
import { addItems } from "../launcher/add";
import {
  addCategory,
  moveCategory,
  recolorCategory,
  removeCategory,
  renameCategory,
} from "../launcher/categories";
import { openFolder, openItem, removeItem, revealItem, useMissing } from "../launcher/items";
import { dropPaths, moveItem } from "../launcher/reorder";
import { matchesQuery } from "../launcher/search";
import { backend, type EventName, type Events } from "../shared/backend";
import { useConfig } from "../shared/config";
import { ContextMenu, Flyout, GlassSurface, SearchField, Tile, useFileDrop } from "../shared/ui";
import { SystemFlyoutContent } from "../system/SystemFlyoutContent";
import { SystemTail } from "../system/SystemTail";
import { flyoutLabels, type FlyoutId } from "../system/flyoutIds";
import { mockSystem } from "../system/mockSystem";
import type { SystemState } from "../system/model";
import { PanelFlyoutContent } from "../widgets/PanelFlyout";
import { SETTINGS_FLYOUT, panelFlyoutLabel } from "../widgets/panelFlyoutIds";
import { WidgetZone } from "../widgets/WidgetZone";
import {
  fitWindowAbove,
  fromScreen,
  hideWindow,
  screenTopCenter,
  showWindow,
  toScreen,
  useAnchoredWindow,
  useCloseOnBlur,
  useFitWindowTop,
  type Anchor,
  type WindowLabel,
} from "./shell";
import "./windows.css";

/** Окна оболочки в Tauri: каждое рисует свою часть, общаются событиями через Rust. */
export function ShellWindow({ label }: { label: WindowLabel }) {
  const glass = useConfig().config?.appearance.glass;
  useEffect(() => {
    document.documentElement.dataset.window = label;
  }, [label]);
  // «Тонировка» — плотный фон без размытия (токены `data-glass="off"`).
  useEffect(() => {
    if (glass) document.documentElement.dataset.glass = glass === "tint" ? "off" : "acrylic";
  }, [glass]);
  switch (label) {
    case "launcher":
      return <LauncherWindow />;
    case "category":
      return <CategoryPopup />;
    case "dock":
      return <DockWindow />;
    case "dock-menu":
      return <DockMenuWindow />;
    case "flyout":
      return <FlyoutWindow />;
    case "panel-flyout":
      return <PanelFlyoutWindow />;
    case "search":
      return <SearchWindow />;
    case "dock-backdrop":
    case "launcher-backdrop":
      // Подложки стекла — только Acrylic от DWM, рисовать нечего.
      return null;
  }
}

function useEvent<E extends EventName>(event: E, handler: (payload: Events[E]) => void) {
  useEffect(() => {
    const off = backend.on(event, handler);
    return () => {
      off.then((f) => f());
    };
  }, [event, handler]);
}

function LauncherWindow() {
  const { config, update } = useConfig();
  const [openId, setOpenId] = useState<string | null>(null);
  const [flyout, setFlyout] = useState<string | null>(null);
  useEvent(
    "category_closed",
    useCallback(() => setOpenId(null), []),
  );
  useEvent(
    "panel_flyout_closed",
    useCallback(() => setFlyout(null), []),
  );
  useFitWindowTop(".ws-top-panel", 16, 80);
  useGlassRect(".ws-top-panel");

  /** Категория под плиткой из окна категории или под файлами из Проводника. */
  const [dragOver, setDragOver] = useState<string | null>(null);
  // Ответы `fromScreen` могут прийти не по порядку: учитываем только последнее событие.
  const tileSeq = useRef(0);
  useEvent(
    "tile_drag",
    useCallback(
      (e: Events["tile_drag"]) => {
        const seq = ++tileSeq.current;
        void fromScreen(e.x, e.y).then(({ x, y }) => {
          if (seq !== tileSeq.current) return;
          const id = categoryAt(x, y);
          const target = id !== e.categoryId ? id : null;
          if (e.phase === "move") return setDragOver(target);
          setDragOver(null);
          if (e.phase === "drop" && target) {
            void update((c) => moveItem(c, e.categoryId, e.itemId, target)).catch(console.error);
          }
        });
      },
      [update],
    ),
  );

  // Файлы из Проводника: задержались над категорией — открываем её, отпустили — добавляем.
  const spring = useRef<{ id: string; timer: number } | null>(null);
  const stopSpring = () => {
    if (spring.current) clearTimeout(spring.current.timer);
    spring.current = null;
  };
  useEffect(() => stopSpring, []);
  const addDropped = async (id: string, dropped: string[]) => {
    const paths = await backend.call("keep_shortcuts", { paths: dropped });
    const dirs = await backend.call("paths_are_dirs", { paths });
    await update((c) => dropPaths(c, id, paths, dirs));
  };
  useFileDrop((e) => {
    if (e.type === "leave") {
      stopSpring();
      return setDragOver(null);
    }
    const id = categoryAt(e.x, e.y);
    if (e.type === "over") {
      setDragOver(id);
      if (spring.current?.id === id) return;
      stopSpring();
      if (id && id !== openId) {
        spring.current = {
          id,
          timer: window.setTimeout(() => {
            spring.current = null;
            setOpenId(id);
            // Без фокуса: Проводник ещё держит перетаскивание.
            void backend
              .emit("category_open", { id, byDrag: true })
              .then(() => showWindow("category", false));
          }, SPRING_DELAY),
        };
      }
      return;
    }
    stopSpring();
    setDragOver(null);
    if (id && e.paths.length) addDropped(id, e.paths).catch(console.error);
  });

  if (!config) return null;
  // Flyout виджета или настроек — своё окно под панелью, правым краем по кнопке.
  const toggleFlyout = async (id: string, anchor: HTMLElement) => {
    if (flyout === id) {
      setFlyout(null);
      return hideWindow("panel-flyout");
    }
    setFlyout(id);
    const panel = document.querySelector(".ws-top-panel")?.getBoundingClientRect();
    const r = anchor.getBoundingClientRect();
    const { x: right, y: top } = await toScreen(r.right, (panel?.bottom ?? r.bottom) + 8);
    await backend.emit("panel_flyout_open", { id, right, top });
  };
  return (
    <TopPanel
      widgets={<WidgetZone openId={flyout} onToggle={(id, a) => void toggleFlyout(id, a)} />}
      settingsOpen={flyout === SETTINGS_FLYOUT}
      onSettings={(a) => void toggleFlyout(SETTINGS_FLYOUT, a)}
      categories={config.categories}
      openCategoryId={openId}
      dragOverId={dragOver}
      onOpenCategory={(id) => {
        setOpenId(id);
        if (id) {
          void backend.emit("category_open", { id }).then(() => showWindow("category"));
        } else {
          void hideWindow("category");
        }
      }}
      onSearch={() => void showWindow("search")}
      onAdd={() => {
        const added = addCategory(config);
        setOpenId(added.id);
        void update(() => added.config)
          .then(() => backend.emit("category_open", { id: added.id, edit: true }))
          .then(() => showWindow("category"));
      }}
    />
  );
}

/** Сколько держать файлы над категорией на панели, чтобы она открылась. */
const SPRING_DELAY = 600;
/** Через сколько закрыть окно, открытое перетаскиванием, если файлы увели и не бросили. */
const LEAVE_DELAY = 700;

/**
 * Категория на панели под точкой окна панели (CSS-пиксели): по горизонтали — пилюля под
 * курсором, по вертикали — любая высота окна панели, чтобы не целиться в узкую пилюлю.
 */
function categoryAt(x: number, y: number): string | null {
  if (y < 0 || y > window.innerHeight) return null;
  for (const pill of document.querySelectorAll<HTMLElement>("[data-category-id]")) {
    const r = pill.getBoundingClientRect();
    if (x >= r.left && x <= r.right) return pill.dataset.categoryId ?? null;
  }
  return null;
}

function CategoryPopup() {
  const { config, update } = useConfig();
  // `n` пересоздаёт окно при каждом открытии: сброс поиска и автофокус.
  // `byDrag` — открыли, пока над панелью держат файлы из Проводника: окно без фокуса.
  const [open, setOpen] = useState<{
    id: string;
    edit: boolean;
    byDrag: boolean;
    n: number;
  } | null>(null);
  useEvent(
    "category_open",
    useCallback(
      ({ id, edit, byDrag }: Events["category_open"]) =>
        setOpen((o) => ({
          id,
          edit: edit ?? false,
          byDrag: byDrag ?? false,
          n: (o?.n ?? 0) + 1,
        })),
      [],
    ),
  );
  /** Файлы увели из окна, открытого перетаскиванием, и не бросили — окно закрывается. */
  const leaveTimer = useRef<number | undefined>(undefined);
  useEffect(() => () => clearTimeout(leaveTimer.current), []);
  const close = useCallback(() => {
    clearTimeout(leaveTimer.current);
    setOpen(null);
    void hideWindow("category");
    void backend.emit("category_closed", null);
  }, []);
  // Системный диалог выбора забирает фокус — окно категории при этом не закрываем.
  const picking = useRef(false);
  useCloseOnBlur(
    open !== null,
    useCallback(() => !picking.current && close(), [close]),
  );
  // Неудачный запуск — перепроверить диск: плитка могла пропасть, пока окно было открыто.
  const [failed, setFailed] = useState(0);

  const categories = config?.categories ?? [];
  const index = categories.findIndex((c) => c.id === open?.id);
  const category = categories[index];
  const missing = useMissing(category?.items ?? [], `${open?.n}-${failed}`);
  if (!open || !category) return null;
  // Открыли — окно категории уходит, фокус достаётся запущенной программе.
  const run = (action: Promise<void>) =>
    action.then(close, (e: unknown) => {
      console.error(e);
      setFailed((n) => n + 1);
    });
  return (
    <CategoryWindow
      key={`${category.id}-${open.n}`}
      category={category}
      missing={missing}
      startEditing={open.edit}
      isFirst={index === 0}
      isLast={index === categories.length - 1}
      onRename={(name) => update((c) => renameCategory(c, category.id, name))}
      onRecolor={(color) => update((c) => recolorCategory(c, category.id, color))}
      onMove={(delta) => update((c) => moveCategory(c, category.id, delta))}
      onDelete={() => void update((c) => removeCategory(c, category.id)).then(close)}
      onClose={close}
      onOpenItem={(item) => void run(openItem(item))}
      onRevealItem={(item) => void run(revealItem(item))}
      onOpenFolder={(path) => void run(openFolder(path))}
      onAddItems={(items) => update((c) => addItems(c, category.id, items))}
      onPickPaths={async (folders) => {
        picking.current = true;
        try {
          const paths = await backend.call("pick_paths", { folders });
          return await backend.call("keep_shortcuts", { paths });
        } catch (e) {
          console.error(e);
          return [];
        } finally {
          picking.current = false;
        }
      }}
      onRemoveItem={(item) => update((c) => removeItem(c, category.id, item.id))}
      onMoveItem={(itemId, to) => update((c) => moveItem(c, category.id, itemId, category.id, to))}
      onDragOut={(phase, item, p) =>
        void toScreen(p.x, p.y).then(({ x, y }) =>
          backend.emit("tile_drag", { phase, categoryId: category.id, itemId: item.id, x, y }),
        )
      }
      onFileDrag={(over) => {
        clearTimeout(leaveTimer.current);
        if (!over && open.byDrag) leaveTimer.current = window.setTimeout(close, LEAVE_DELAY);
      }}
      onDropPaths={(dropped, to) => {
        void (async () => {
          const paths = await backend.call("keep_shortcuts", { paths: dropped });
          const dirs = await backend.call("paths_are_dirs", { paths });
          await update((c) => dropPaths(c, category.id, paths, dirs, to));
        })().catch(console.error);
        // Бросили — окно обычное, с фокусом (его держал Проводник): закроется кликом мимо.
        if (open.byDrag) setOpen({ ...open, byDrag: false });
        void showWindow("category");
      }}
    />
  );
}

/**
 * Сообщаем Rust прямоугольник «стеклянного» элемента: подложка с Acrylic встаёт ровно под него.
 * Пересчёт — когда элемент меняет размер (увеличение иконок, новые иконки) или окно — масштаб,
 * а не каждый кадр: постоянный цикл держал WebView2 на ~7 % ядра в простое.
 * В режиме «Тонировка» подложка спрятана, но держим её на месте.
 */
function useGlassRect(selector: string) {
  useEffect(() => {
    let last = "";
    let el: Element | null = null;
    const send = () => {
      if (!el) return;
      // Физические пиксели — по devicePixelRatio самого WebView2 (спайк WSH-6).
      const d = window.devicePixelRatio;
      const r = el.getBoundingClientRect();
      const rect = { x: r.x * d, y: r.y * d, w: r.width * d, h: r.height * d };
      const key = `${rect.x},${rect.y},${rect.w},${rect.h}`;
      if (key !== last) {
        last = key;
        void backend.call("glass_rect", rect);
      }
    };
    const observer = new ResizeObserver(send);
    // Элемент появляется после загрузки конфига — ждём его без постоянного цикла.
    const attach = () => {
      const found = document.querySelector(selector);
      if (found === el) return;
      if (el) observer.unobserve(el);
      el = found;
      if (el) observer.observe(el);
      send();
    };
    const mutations = new MutationObserver(attach);
    mutations.observe(document.body, { childList: true, subtree: true });
    attach();
    window.addEventListener("resize", send);
    return () => {
      observer.disconnect();
      mutations.disconnect();
      window.removeEventListener("resize", send);
    };
  }, [selector]);
}

function DockWindow() {
  const apps = useDockApps();
  const [system, setSystem] = useState(() => mockSystem(false));
  const [flyout, setFlyout] = useState<FlyoutId | null>(null);
  useEvent("system_state", setSystem);
  useEvent(
    "flyout_closed",
    useCallback(() => setFlyout(null), []),
  );
  useGlassRect(".ws-dock__pill");

  if (!apps.ready) return null;
  // Меню приложения — своё окно над иконкой: в окне дока ему не хватит высоты.
  const openMenu = (entry: DockEntry, icon: HTMLElement) =>
    void screenTopCenter(icon, 6).then(({ x, y }) =>
      backend.emit("dock_menu_open", { id: entry.id, x, y }),
    );
  // Изменения в хвосте (колесо громкости) — всем окнам, в том числе открытому flyout.
  const change = (update: (s: SystemState) => SystemState) =>
    void backend.emit("system_state", update(system));

  return (
    <Dock
      pinned={apps.pinned}
      transient={apps.transient}
      onClickApp={(entry, icon) => apps.click(entry) === "menu" && openMenu(entry, icon)}
      onMenu={openMenu}
      onLaunch={apps.launch}
      onMovePin={apps.reorder.move}
      onPinAt={apps.reorder.pin}
      onDropPrograms={apps.reorder.pinPaths}
      tail={
        <SystemTail
          state={system}
          onChange={change}
          flyoutOpen={flyout}
          onFlyout={(id, pill) => {
            setFlyout(id);
            if (!id) return void hideWindow("flyout");
            // Карточка — правым краем по таблетке, на 10 px выше неё; окно ставит себя само.
            void toScreen(pill.right, pill.top - 10).then(({ x: right, y: bottom }) =>
              backend.emit("flyout_open", { id, state: system, right, bottom }),
            );
          }}
        />
      }
    />
  );
}

/** Поле вокруг меню в его окне: под тень. */
const MENU_MARGIN = 12;

/** Меню иконки дока: окна приложения, новое окно, закрепить или убрать, закрыть. */
function DockMenuWindow() {
  const apps = useDockApps();
  // `n` — каждое открытие заново подгоняет окно, даже для того же приложения.
  const [open, setOpen] = useState<(Events["dock_menu_open"] & { n: number }) | null>(null);
  useEvent(
    "dock_menu_open",
    useCallback(
      (e: Events["dock_menu_open"]) => setOpen((o) => ({ ...e, n: (o?.n ?? 0) + 1 })),
      [],
    ),
  );
  const close = useCallback(() => {
    setOpen(null);
    void hideWindow("dock-menu");
    void backend.emit("dock_menu_closed", null);
  }, []);
  useCloseOnBlur(open !== null, close);

  const entry = open ? apps.find(open.id) : undefined;
  // Приложение пропало из дока (закрыли последнее окно) — прячем и окно меню.
  const gone = open !== null && !entry;
  useEffect(() => {
    if (gone) void hideWindow("dock-menu");
  }, [gone]);
  useEffect(() => {
    if (!open) return;
    const menu = document.querySelector(".ws-menu");
    if (!menu) return;
    void fitWindowAbove(menu, MENU_MARGIN, open.x, open.y).then(() => showWindow("dock-menu"));
    // Только при открытии: пока меню открыто, окно не прыгает.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open?.n]);

  return (
    <ContextMenu
      key={open?.n}
      open={Boolean(entry)}
      x={MENU_MARGIN}
      y={MENU_MARGIN}
      label={entry?.title}
      items={entry ? apps.menu(entry) : []}
      onClose={close}
    />
  );
}

/** Поле вокруг карточки flyout в его окне: под тень (windows.css). */
const FLYOUT_MARGIN = 12;

function FlyoutWindow() {
  // `n` — каждое открытие заново ставит и показывает окно.
  const [open, setOpen] = useState<(Events["flyout_open"] & { n: number }) | null>(null);
  useEvent(
    "flyout_open",
    useCallback((e: Events["flyout_open"]) => setOpen((o) => ({ ...e, n: (o?.n ?? 0) + 1 })), []),
  );
  const anchor = useMemo<Anchor | null>(
    () => open && { right: open.right, bottom: open.bottom },
    // Смена состояния (громкость колесом) окно не двигает — только новое открытие.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [open?.n],
  );
  useAnchoredWindow(".ws-tail-flyout", FLYOUT_MARGIN, anchor);
  useEvent(
    "system_state",
    useCallback((state: SystemState) => setOpen((o) => o && { ...o, state }), []),
  );
  const close = useCallback(() => {
    setOpen(null);
    void hideWindow("flyout");
    void backend.emit("flyout_closed", null);
  }, []);
  useCloseOnBlur(open !== null, close);

  return (
    <Flyout
      open={open !== null}
      onClose={close}
      label={open ? flyoutLabels[open.id] : ""}
      anchor="dock"
      className="ws-tail-flyout"
    >
      {open && (
        <SystemFlyoutContent
          id={open.id}
          state={open.state}
          onChange={(update) => void backend.emit("system_state", update(open.state))}
        />
      )}
    </Flyout>
  );
}

/** Flyout виджета верхней панели или настройки виджетов: окно под панелью. */
function PanelFlyoutWindow() {
  // `n` — каждое открытие заново ставит окно, даже для того же виджета.
  const [open, setOpen] = useState<(Events["panel_flyout_open"] & { n: number }) | null>(null);
  useEvent(
    "panel_flyout_open",
    useCallback(
      (e: Events["panel_flyout_open"]) => setOpen((o) => ({ ...e, n: (o?.n ?? 0) + 1 })),
      [],
    ),
  );
  const anchor = useMemo<Anchor | null>(
    () => open && { right: open.right, top: open.top },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [open?.n],
  );
  useAnchoredWindow(".ws-panel-flyout", FLYOUT_MARGIN, anchor);
  const close = useCallback(() => {
    setOpen(null);
    void hideWindow("panel-flyout");
    void backend.emit("panel_flyout_closed", null);
  }, []);
  useCloseOnBlur(open !== null, close);

  return (
    <Flyout
      key={open?.n}
      open={open !== null}
      onClose={close}
      label={open ? panelFlyoutLabel(open.id) : ""}
      anchor="launcher"
      className="ws-panel-flyout"
    >
      {open && <PanelFlyoutContent id={open.id} close={close} />}
    </Flyout>
  );
}

function SearchWindow() {
  const { config } = useConfig();
  const [query, setQuery] = useState("");
  // Каждое появление окна — пустой запрос и фокус в поле.
  const [shown, setShown] = useState(0);
  useEffect(() => {
    const off = getCurrentWindow().onFocusChanged(({ payload: focused }) => {
      if (focused) {
        setQuery("");
        setShown((n) => n + 1);
      } else {
        void hideWindow("search");
      }
    });
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && void hideWindow("search");
    document.addEventListener("keydown", onKey);
    return () => {
      off.then((f) => f());
      document.removeEventListener("keydown", onKey);
    };
  }, []);

  const results = query
    ? (config?.categories ?? []).flatMap((c) =>
        c.items.filter((item) => matchesQuery(item, query)).map((item) => ({ item, c })),
      )
    : [];

  return (
    <GlassSurface level="panel" role="dialog" aria-label="Поиск" className="ws-search-window">
      <SearchField
        key={shown}
        value={query}
        onChange={setQuery}
        placeholder="Поиск по всем категориям"
        aria-label="Поиск по всем категориям"
        autoFocus
      />
      <div className="ws-search-window__results">
        {results.map(({ item, c }) => (
          <Tile
            key={`${c.id}/${item.id}`}
            icon={<ItemIcon item={item} color={c.color} />}
            title={item.title}
            meta={c.name}
            onClick={() => void openItem(item).then(() => hideWindow("search"), console.error)}
          />
        ))}
        {query && results.length === 0 && (
          <p className="ws-search-window__empty">Ничего не нашлось</p>
        )}
      </div>
    </GlassSurface>
  );
}
