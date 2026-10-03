import { useCallback, useEffect, useState } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { Dock } from "../dock/Dock";
import { buildDock, clickApp } from "../dock/model";
import { mockRunning } from "../dock/mockRunning";
import { CategoryWindow } from "../launcher/CategoryWindow";
import { ItemIcon } from "../launcher/ItemIcon";
import { TopPanel } from "../launcher/TopPanel";
import { matchesQuery } from "../launcher/search";
import { backend, type EventName, type Events } from "../shared/backend";
import { useConfig } from "../shared/config";
import { Flyout, GlassSurface, SearchField, Tile } from "../shared/ui";
import { SystemFlyoutContent } from "../system/SystemFlyoutContent";
import { SystemTail } from "../system/SystemTail";
import { flyoutLabels, type FlyoutId } from "../system/flyoutIds";
import { mockSystem } from "../system/mockSystem";
import type { SystemState } from "../system/model";
import {
  hideWindow,
  placeFlyoutOver,
  showWindow,
  useCloseOnBlur,
  useFitWindowTop,
  type WindowLabel,
} from "./shell";
import "./windows.css";

/** Окна оболочки в Tauri: каждое рисует свою часть, общаются событиями через Rust. */
export function ShellWindow({ label }: { label: WindowLabel }) {
  useEffect(() => {
    document.documentElement.dataset.window = label;
  }, [label]);
  switch (label) {
    case "launcher":
      return <LauncherWindow />;
    case "category":
      return <CategoryPopup />;
    case "dock":
      return <DockWindow />;
    case "flyout":
      return <FlyoutWindow />;
    case "search":
      return <SearchWindow />;
    case "dock-backdrop":
      // Подложка дока — только Acrylic от DWM, рисовать нечего.
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

// Запуск объектов появится на этапе 3; пока клики по плиткам ничего не делают.
const noop = () => {};

function LauncherWindow() {
  const { config } = useConfig();
  const [openId, setOpenId] = useState<string | null>(null);
  useEvent(
    "category_closed",
    useCallback(() => setOpenId(null), []),
  );
  useFitWindowTop(".ws-top-panel", 16, 80);
  if (!config) return null;
  return (
    <TopPanel
      categories={config.categories}
      openCategoryId={openId}
      onOpenCategory={(id) => {
        setOpenId(id);
        if (id) {
          void backend.emit("category_open", { id }).then(() => showWindow("category"));
        } else {
          void hideWindow("category");
        }
      }}
      onSearch={() => void showWindow("search")}
    />
  );
}

function CategoryPopup() {
  const { config, update } = useConfig();
  // `n` пересоздаёт окно при каждом открытии: сброс поиска и автофокус.
  const [open, setOpen] = useState<{ id: string; n: number } | null>(null);
  useEvent(
    "category_open",
    useCallback(({ id }) => setOpen((o) => ({ id, n: (o?.n ?? 0) + 1 })), []),
  );
  const close = useCallback(() => {
    setOpen(null);
    void hideWindow("category");
    void backend.emit("category_closed", null);
  }, []);
  useCloseOnBlur(open !== null, close);

  const category = config?.categories.find((c) => c.id === open?.id);
  if (!open || !category) return null;
  return (
    <CategoryWindow
      key={`${category.id}-${open.n}`}
      category={category}
      onClose={close}
      onOpenItem={noop}
      onRevealItem={noop}
      onOpenFolder={noop}
      onAdd={noop}
      onRemoveItem={(item) =>
        update((c) => ({
          ...c,
          categories: c.categories.map((cat) =>
            cat.id === category.id
              ? { ...cat, items: cat.items.filter((i) => i.id !== item.id) }
              : cat,
          ),
        }))
      }
    />
  );
}

/** Каждый кадр сверяем таблетку и отдаём её Rust: подложка с Acrylic встаёт ровно под неё. */
function usePillSync() {
  useEffect(() => {
    let frame = 0;
    let last = "";
    const tick = () => {
      const pill = document.querySelector(".ws-dock__pill");
      if (pill) {
        // Физические пиксели — по devicePixelRatio самого WebView2 (спайк WSH-6).
        const d = window.devicePixelRatio;
        const r = pill.getBoundingClientRect();
        const rect = { x: r.x * d, y: r.y * d, w: r.width * d, h: r.height * d };
        const key = `${rect.x},${rect.y},${rect.w},${rect.h}`;
        if (key !== last) {
          last = key;
          void backend.call("dock_pill", rect);
        }
      }
      frame = requestAnimationFrame(tick);
    };
    frame = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(frame);
  }, []);
}

function DockWindow() {
  const { config } = useConfig();
  const [running, setRunning] = useState(mockRunning);
  const [system, setSystem] = useState(() => mockSystem(false));
  const [flyout, setFlyout] = useState<FlyoutId | null>(null);
  useEvent("system_state", setSystem);
  useEvent(
    "flyout_closed",
    useCallback(() => setFlyout(null), []),
  );
  usePillSync();

  if (!config) return null;
  const dock = buildDock(config.dock.pinned, running);
  // Изменения в хвосте (колесо громкости) — всем окнам, в том числе открытому flyout.
  const change = (update: (s: SystemState) => SystemState) =>
    void backend.emit("system_state", update(system));

  return (
    <Dock
      pinned={dock.pinned}
      transient={dock.transient}
      onClickApp={(entry) => setRunning((r) => clickApp(r, entry.id, entry.title))}
      tail={
        <SystemTail
          state={system}
          onChange={change}
          flyoutOpen={flyout}
          onFlyout={(id, pill) => {
            setFlyout(id);
            if (!id) return void hideWindow("flyout");
            void backend
              .emit("flyout_open", { id, state: system })
              .then(() => placeFlyoutOver(pill))
              .then(() => showWindow("flyout"));
          }}
        />
      }
    />
  );
}

function FlyoutWindow() {
  const [open, setOpen] = useState<{ id: FlyoutId; state: SystemState } | null>(null);
  useEvent("flyout_open", setOpen);
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
            onClick={noop}
          />
        ))}
        {query && results.length === 0 && <p className="ws-search-window__empty">Ничего не нашлось</p>}
      </div>
    </GlassSurface>
  );
}
