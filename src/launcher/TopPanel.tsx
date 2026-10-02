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
  onSettings?: () => void;
  hidden?: boolean;
};

export function TopPanel({
  categories,
  openCategoryId,
  onOpenCategory,
  onSearch,
  onAdd,
  onSettings,
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
      {/* Зона виджетов слота "launcher": заполняет каркас виджетов на этапе 5. */}
      <div className="ws-top-panel__widgets" data-slot="launcher" />
      <div className="ws-top-panel__actions">
        <Tooltip text="Поиск" placement="bottom">
          <IconButton icon="search" label="Поиск" onClick={onSearch} />
        </Tooltip>
        <Tooltip text="Добавить" placement="bottom">
          <IconButton icon="plus" label="Добавить" onClick={onAdd} />
        </Tooltip>
        <Tooltip text="Настройки" placement="bottom">
          <IconButton icon="settings" label="Настройки" onClick={onSettings} />
        </Tooltip>
      </div>
    </GlassSurface>
  );
}
