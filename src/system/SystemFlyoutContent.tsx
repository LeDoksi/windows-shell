import { BatteryFlyout, ClockFlyout, NetworkFlyout, TrayFlyout } from "./flyouts";
import type { FlyoutId } from "./flyoutIds";
import type { SystemState } from "./model";
import type { SystemChange } from "./SystemTail";
import { useNow } from "./useNow";
import { WidgetFlyout } from "../widgets/WidgetZone";

/** Содержимое flyout системного хвоста: в доке (браузерный режим) или в окне flyout (Tauri). */
export function SystemFlyoutContent({
  id,
  state,
  onChange,
}: {
  id: FlyoutId;
  state: SystemState;
  onChange: SystemChange;
}) {
  const now = useNow();
  const { battery } = state;
  switch (id) {
    case "tray":
      return <TrayFlyout icons={state.tray} />;
    case "network":
      return (
        <NetworkFlyout
          network={state.network}
          wifiEnabled={state.wifiEnabled}
          networks={state.wifiNetworks}
          onWifiEnabled={(wifiEnabled) => onChange((s) => ({ ...s, wifiEnabled }))}
        />
      );
    case "audio":
      return <WidgetFlyout id="audio" />;
    case "battery":
      return battery ? <BatteryFlyout battery={battery} /> : null;
    case "clock":
      return <ClockFlyout now={now} />;
  }
}
