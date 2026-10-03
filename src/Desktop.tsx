import { useState } from "react";
import { Dock } from "./dock/Dock";
import { buildDock, clickApp } from "./dock/model";
import { mockRunning } from "./dock/mockRunning";
import { CategoryWindow } from "./launcher/CategoryWindow";
import { TopPanel } from "./launcher/TopPanel";
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
  const [running, setRunning] = useState(mockRunning);
  const [system, setSystem] = useState(() =>
    mockSystem(new URLSearchParams(window.location.search).has("laptop")),
  );

  // «＋» — следующая задача этапа 3.
  const noop = () => {};

  if (!config) return null;
  const { categories } = config;
  const openCategory = categories.find((c) => c.id === openCategoryId);
  const dock = buildDock(config.dock.pinned, running);

  return (
    <div className="ws-desktop">
      <TopPanel
        categories={categories}
        openCategoryId={openCategoryId}
        onOpenCategory={setOpenCategoryId}
      />
      {openCategory && (
        <CategoryWindow
          key={openCategory.id}
          category={openCategory}
          onClose={() => setOpenCategoryId(null)}
          onOpenItem={(item) => void openItem(item)}
          onRevealItem={(item) => void revealItem(item)}
          onOpenFolder={(path) => void openFolder(path)}
          onAdd={noop}
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
