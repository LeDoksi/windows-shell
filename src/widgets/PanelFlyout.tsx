import { SETTINGS_FLYOUT } from "./panelFlyoutIds";
import { WidgetFlyout } from "./WidgetZone";
import { WidgetSettings } from "./WidgetSettings";

/** Flyout под верхней панелью: виджет по id или настройки виджетов. */
export function PanelFlyoutContent({ id, close }: { id: string; close: () => void }) {
  return id === SETTINGS_FLYOUT ? <WidgetSettings /> : <WidgetFlyout id={id} close={close} />;
}
