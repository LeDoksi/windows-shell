import { SETTINGS_FLYOUT } from "./panelFlyoutIds";
import { WidgetFlyout } from "./WidgetZone";
import { WidgetSettings } from "./WidgetSettings";

/** Flyout под верхней панелью: виджет по id или настройки виджетов. */
export function PanelFlyoutContent({ id }: { id: string }) {
  return id === SETTINGS_FLYOUT ? <WidgetSettings /> : <WidgetFlyout id={id} />;
}
