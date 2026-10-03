import { AudioFlyout, BatteryFlyout, ClockFlyout, NetworkFlyout, TrayFlyout } from "./flyouts";
import type { FlyoutId } from "./flyoutIds";
import type { SystemState } from "./model";
import type { SystemChange } from "./SystemTail";
import { useNow } from "./useNow";

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
  const { audio, battery } = state;
  const setVolume = (volume: number) =>
    onChange((s) => ({ ...s, audio: { ...s.audio, volume, muted: false } }));
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
      return (
        <AudioFlyout
          audio={audio}
          onDevice={(defaultId) => onChange((s) => ({ ...s, audio: { ...s.audio, defaultId } }))}
          onVolume={setVolume}
          onMute={(muted) => onChange((s) => ({ ...s, audio: { ...s.audio, muted } }))}
        />
      );
    case "battery":
      return battery ? <BatteryFlyout battery={battery} /> : null;
    case "clock":
      return <ClockFlyout now={now} />;
  }
}
