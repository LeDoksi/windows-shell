import { useState } from "react";
import { TopPanel } from "./launcher/TopPanel";
import { defaultConfig } from "./shared/config";

/**
 * Браузерный режим: все окна оболочки на одной странице поверх «обоев».
 * В Tauri каждая часть станет отдельным окном (этап 2).
 */
export function Desktop() {
  const [openCategoryId, setOpenCategoryId] = useState<string | null>(null);
  const { categories } = defaultConfig;

  return (
    <div className="ws-desktop">
      <TopPanel
        categories={categories}
        openCategoryId={openCategoryId}
        onOpenCategory={setOpenCategoryId}
      />
    </div>
  );
}
