import { Icon } from "../shared/ui";
import type { BatteryState, NetworkState } from "./model";

/** Wi-Fi с уровнем сигнала: незаполненные дуги приглушены. */
export function WifiGlyph({ signal, size = 20 }: { signal: number; size?: number }) {
  const arcs = ["M2.5 8a11 11 0 0 1 15 0", "M5 11a7 7 0 0 1 10 0", "M7.6 13.8a3 3 0 0 1 4.8 0"];
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 20 20"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.6}
      strokeLinecap="round"
      aria-hidden="true"
    >
      {arcs.map((d, i) => (
        <path key={d} d={d} opacity={signal >= 3 - i + 1 ? 1 : 0.3} />
      ))}
      <circle cx="10" cy="16.4" r="1" fill="currentColor" stroke="none" />
    </svg>
  );
}

export function NetworkGlyph({ network }: { network: NetworkState }) {
  if (network.kind === "ethernet") return <Icon name="ethernet" size={20} />;
  if (network.kind === "none") return <Icon name="wifiOff" size={20} />;
  return <WifiGlyph signal={network.signal} />;
}

export function BatteryGlyph({ battery, size = 22 }: { battery: BatteryState; size?: number }) {
  const width = Math.max(1, (battery.percent / 100) * 12);
  const low = battery.percent <= 15 && !battery.charging;
  return (
    <svg width={size} height={size} viewBox="0 0 22 20" fill="none" aria-hidden="true">
      <rect x="2" y="5.5" width="16" height="9" rx="2.2" stroke="currentColor" strokeWidth="1.4" />
      <path d="M19.6 8.5v3" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
      <rect
        x="4"
        y="7.5"
        width={width}
        height="5"
        rx="1"
        fill={low ? "var(--ws-danger)" : "currentColor"}
      />
      {battery.charging && (
        <path d="M10.8 6.5 8 10.5h2.4L9.6 13.5l3-4h-2.4z" fill="var(--ws-surface-strong)" />
      )}
    </svg>
  );
}
