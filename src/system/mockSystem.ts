import type { SystemState } from "./model";

/** Моки системного хвоста. `?laptop` в адресе включает батарею. */
export function mockSystem(laptop: boolean): SystemState {
  return {
    tray: [
      { id: "onedrive", title: "OneDrive" },
      { id: "discord", title: "Discord", attention: true },
      { id: "steam", title: "Steam" },
      { id: "nvidia", title: "NVIDIA" },
      { id: "obs", title: "OBS Studio" },
    ],
    network: { kind: "wifi", ssid: "Home_5G", signal: 3 },
    wifiEnabled: true,
    wifiNetworks: [
      { ssid: "Home_5G", signal: 3, secured: true },
      { ssid: "Home", signal: 4, secured: true },
      { ssid: "Cafe Guest", signal: 1, secured: false },
    ],
    battery: laptop ? { percent: 78, charging: false, minutesLeft: 214 } : null,
  };
}
