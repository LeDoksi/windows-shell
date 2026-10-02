import { useState } from "react";
import { Dock } from "./dock/Dock";
import { buildDock, clickApp } from "./dock/model";
import { mockRunning } from "./dock/mockRunning";
import { CategoryWindow } from "./launcher/CategoryWindow";
import { TopPanel } from "./launcher/TopPanel";
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

  // Запуск объектов появится на этапе 3; пока клики ничего не делают.
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
          onOpenItem={noop}
          onRevealItem={noop}
          onOpenFolder={noop}
          onAdd={noop}
          onRemoveItem={(item) =>
            update((c) => ({
              ...c,
              categories: c.categories.map((category) =>
                category.id === openCategory.id
                  ? { ...category, items: category.items.filter((i) => i.id !== item.id) }
                  : category,
              ),
            }))
          }
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
