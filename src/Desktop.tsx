import { useState } from "react";
import { CategoryWindow } from "./launcher/CategoryWindow";
import { TopPanel } from "./launcher/TopPanel";
import { defaultConfig, type Category } from "./shared/config";

/**
 * Браузерный режим: все окна оболочки на одной странице поверх «обоев».
 * В Tauri каждая часть станет отдельным окном (этап 2).
 */
export function Desktop() {
  const [categories, setCategories] = useState<Category[]>(defaultConfig.categories);
  const [openCategoryId, setOpenCategoryId] = useState<string | null>(null);
  const openCategory = categories.find((c) => c.id === openCategoryId);

  // Запуск объектов появится на этапе 3; пока клики ничего не делают.
  const noop = () => {};

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
            setCategories((all) =>
              all.map((c) =>
                c.id === openCategory.id
                  ? { ...c, items: c.items.filter((i) => i.id !== item.id) }
                  : c,
              ),
            )
          }
        />
      )}
    </div>
  );
}
