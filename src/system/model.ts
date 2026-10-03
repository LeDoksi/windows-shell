/** Состояние системного хвоста. Сейчас моки; на этапе 8 приходит из Rust. */

export type TrayIcon = {
  id: string;
  title: string;
  /** Иконка мигает или сменилась на «внимание». */
  attention?: boolean;
};

export type WifiNetwork = { ssid: string; signal: 0 | 1 | 2 | 3 | 4; secured: boolean };

export type NetworkState =
  | { kind: "ethernet"; name: string }
  | { kind: "wifi"; ssid: string; signal: 0 | 1 | 2 | 3 | 4 }
  | { kind: "none" };

export type BatteryState = { percent: number; charging: boolean; minutesLeft: number | null };

export type SystemState = {
  tray: TrayIcon[];
  network: NetworkState;
  wifiEnabled: boolean;
  wifiNetworks: WifiNetwork[];
  /** null — батареи нет (ПК), элемент не показывается. */
  battery: BatteryState | null;
};
