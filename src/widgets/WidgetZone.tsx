import { inSlot } from "./resolve";
import "./widgets.css";
import { useWidgets } from "./useWidgets";

type Props = {
  /** Открытый flyout (id виджета) или null. */
  openId: string | null;
  onToggle: (id: string, anchor: HTMLElement) => void;
};

/** Компактные виджеты слота `launcher` — справа в верхней панели, в порядке из настроек. */
export function WidgetZone({ openId, onToggle }: Props) {
  const { widgets, updateSettings } = useWidgets();
  return (
    <>
      {inSlot(widgets, "launcher").map(({ def, settings }) => (
        <def.Compact
          key={def.id}
          settings={settings}
          updateSettings={(change) => void updateSettings(def.id, change)}
          open={openId === def.id}
          onToggle={(anchor) => onToggle(def.id, anchor)}
        />
      ))}
    </>
  );
}

/** Flyout виджета по id: тот же компонент в браузере, в окне flyout и под панелью. */
export function WidgetFlyout({ id }: { id: string }) {
  const { widgets, updateSettings } = useWidgets();
  const widget = widgets.find((w) => w.def.id === id);
  if (!widget?.def.Flyout) return null;
  const { def, settings } = widget;
  const Flyout = def.Flyout!;
  return (
    <Flyout settings={settings} updateSettings={(change) => void updateSettings(def.id, change)} />
  );
}
