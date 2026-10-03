import { useState } from "react";
import { Dock } from "./dock/Dock";
import type { DockEntry } from "./dock/model";
import { useDockApps } from "./dock/useDockApps";
import { CategoryWindow } from "./launcher/CategoryWindow";
import { TopPanel } from "./launcher/TopPanel";
import { addItems } from "./launcher/add";
import {
  addCategory,
  moveCategory,
  recolorCategory,
  removeCategory,
  renameCategory,
} from "./launcher/categories";
import { backend } from "./shared/backend";
import { openFolder, openItem, removeItem, revealItem } from "./launcher/items";
import { useConfig } from "./shared/config";
import { ContextMenu, Flyout } from "./shared/ui";
import { PanelFlyoutContent } from "./widgets/PanelFlyout";
import { SETTINGS_FLYOUT, panelFlyoutLabel } from "./widgets/panelFlyoutIds";
import { WidgetZone } from "./widgets/WidgetZone";
import { SystemTail } from "./system/SystemTail";
import { mockSystem } from "./system/mockSystem";

/**
 * Браузерный режим: все окна оболочки на одной странице поверх «обоев».
 * В Tauri каждая часть станет отдельным окном (этап 2).
 */
export function Desktop() {
  const { config, update } = useConfig();
  const [openCategoryId, setOpenCategoryId] = useState<string | null>(null);
  /** Новая категория открывается сразу в режиме правки. */
  const [editNew, setEditNew] = useState(false);
  const apps = useDockApps();
  const [menu, setMenu] = useState<{ id: string; x: number; y: number } | null>(null);
  const [panelFlyout, setPanelFlyout] = useState<{ id: string; right: number; top: number } | null>(
    null,
  );
  const [system, setSystem] = useState(() =>
    mockSystem(new URLSearchParams(window.location.search).has("laptop")),
  );

  if (!config) return null;
  const { categories } = config;
  const index = categories.findIndex((c) => c.id === openCategoryId);
  const openCategory = categories[index];
  const open = (id: string | null, edit = false) => {
    setOpenCategoryId(id);
    setEditNew(edit);
  };
  const openMenu = (entry: DockEntry, icon: HTMLElement) => {
    const r = icon.getBoundingClientRect();
    setMenu({ id: entry.id, x: r.left + r.width / 2, y: r.top - 10 });
  };
  const menuEntry = menu && apps.find(menu.id);
  // Flyout виджета или настроек: под панелью, правым краем по кнопке.
  const togglePanelFlyout = (id: string, anchor: HTMLElement) => {
    if (panelFlyout?.id === id) return setPanelFlyout(null);
    const panel = document.querySelector(".ws-top-panel")?.getBoundingClientRect();
    const r = anchor.getBoundingClientRect();
    setPanelFlyout({
      id,
      right: window.innerWidth - r.right,
      top: (panel?.bottom ?? r.bottom) + 8,
    });
  };

  return (
    <div className="ws-desktop">
      <TopPanel
        widgets={<WidgetZone openId={panelFlyout?.id ?? null} onToggle={togglePanelFlyout} />}
        settingsOpen={panelFlyout?.id === SETTINGS_FLYOUT}
        onSettings={(a) => togglePanelFlyout(SETTINGS_FLYOUT, a)}
        categories={categories}
        openCategoryId={openCategoryId}
        onOpenCategory={(id) => open(id)}
        onAdd={() => {
          const added = addCategory(config);
          void update(() => added.config).then(() => open(added.id, true));
        }}
      />
      {openCategory && (
        <CategoryWindow
          key={openCategory.id}
          category={openCategory}
          startEditing={editNew}
          isFirst={index === 0}
          isLast={index === categories.length - 1}
          onRename={(name) => update((c) => renameCategory(c, openCategory.id, name))}
          onRecolor={(color) => update((c) => recolorCategory(c, openCategory.id, color))}
          onMove={(delta) => update((c) => moveCategory(c, openCategory.id, delta))}
          onDelete={() =>
            void update((c) => removeCategory(c, openCategory.id)).then(() => open(null))
          }
          onClose={() => open(null)}
          onOpenItem={(item) => void openItem(item)}
          onRevealItem={(item) => void revealItem(item)}
          onOpenFolder={(path) => void openFolder(path)}
          onAddItems={(items) => update((c) => addItems(c, openCategory.id, items))}
          onPickPaths={(folders) => backend.call("pick_paths", { folders })}
          onRemoveItem={(item) => update((c) => removeItem(c, openCategory.id, item.id))}
        />
      )}
      <Dock
        pinned={apps.pinned}
        transient={apps.transient}
        onClickApp={(entry, icon) => apps.click(entry) === "menu" && openMenu(entry, icon)}
        onMenu={openMenu}
        onLaunch={apps.launch}
        tail={<SystemTail state={system} onChange={setSystem} />}
      />
      <Flyout
        open={panelFlyout !== null}
        onClose={() => setPanelFlyout(null)}
        label={panelFlyout ? panelFlyoutLabel(panelFlyout.id) : ""}
        anchor="launcher"
        className="ws-panel-flyout"
        style={
          panelFlyout ? { position: "fixed", right: panelFlyout.right, top: panelFlyout.top } : {}
        }
      >
        {panelFlyout && <PanelFlyoutContent key={panelFlyout.id} id={panelFlyout.id} />}
      </Flyout>
      <ContextMenu
        open={Boolean(menuEntry)}
        x={menu?.x ?? 0}
        y={menu?.y ?? 0}
        placement="above"
        label={menuEntry?.title}
        items={menuEntry ? apps.menu(menuEntry) : []}
        onClose={() => setMenu(null)}
      />
    </div>
  );
}
