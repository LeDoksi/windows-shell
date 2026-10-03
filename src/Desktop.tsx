import { useState } from "react";
import { Dock } from "./dock/Dock";
import { buildDock, clickApp } from "./dock/model";
import { mockRunning } from "./dock/mockRunning";
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
  const [running, setRunning] = useState(mockRunning);
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
  const dock = buildDock(config.dock.pinned, running);

  return (
    <div className="ws-desktop">
      <TopPanel
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
        pinned={dock.pinned}
        transient={dock.transient}
        onClickApp={(entry) => setRunning((r) => clickApp(r, entry.id, entry.title))}
        tail={<SystemTail state={system} onChange={setSystem} />}
      />
    </div>
  );
}
