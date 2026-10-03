import type { ReactNode } from "react";
import { CategoryPill, GlassSurface, IconButton, Tooltip } from "../shared/ui";
import type { Category } from "../shared/config";
import "./launcher.css";

type Props = {
  categories: Category[];
  /** Открытая категория; null — окно категории закрыто. */
  openCategoryId: string | null;
  onOpenCategory: (id: string | null) => void;
  onSearch?: () => void;
  onAdd?: () => void;
  /** Шестерёнка: настройки виджетов под ней. */
  onSettings?: (anchor: HTMLElement) => void;
  settingsOpen?: boolean;
  /** Компактные виджеты слота `launcher` (`WidgetZone`). */
  widgets?: ReactNode;
  hidden?: boolean;
};

export function TopPanel({
  categories,
  openCategoryId,
  onOpenCategory,
  onSearch,
  onAdd,
  onSettings,
  settingsOpen,
  widgets,
  hidden,
}: Props) {
  return (
    <GlassSurface
      as="nav"
      level="bar"
      aria-label="Категории"
      className="ws-top-panel"
      // Клики по панели переключают окно категории сами.
      data-dismiss-ignore
      data-hidden={hidden || undefined}
    >
      <div className="ws-top-panel__categories">
        {categories.map((category) => (
          <CategoryPill
            key={category.id}
            label={category.name}
            color={category.color}
            selected={category.id === openCategoryId}
            onClick={() => onOpenCategory(category.id === openCategoryId ? null : category.id)}
          />
        ))}
      </div>
      <span className="ws-top-panel__separator" aria-hidden="true" />
      <div className="ws-top-panel__widgets" data-slot="launcher">
        {widgets}
      </div>
      <div className="ws-top-panel__actions">
        <Tooltip text="Поиск" placement="bottom">
          <IconButton icon="search" label="Поиск" onClick={onSearch} />
        </Tooltip>
        <Tooltip text="Добавить" placement="bottom">
          <IconButton icon="plus" label="Добавить" onClick={onAdd} />
        </Tooltip>
        <Tooltip text="Настройки" placement="bottom">
          <IconButton
            icon="settings"
            label="Настройки"
            aria-expanded={settingsOpen}
            aria-haspopup="dialog"
            onClick={(e) => onSettings?.(e.currentTarget)}
          />
        </Tooltip>
      </div>
    </GlassSurface>
  );
}
