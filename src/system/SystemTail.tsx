import { useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { Flyout, Icon, Tooltip } from "../shared/ui";
import { cx } from "../shared/ui/cx";
import { AudioFlyout, BatteryFlyout, ClockFlyout, NetworkFlyout, TrayFlyout } from "./flyouts";
import { clockLines } from "./format";
import { BatteryGlyph, NetworkGlyph } from "./glyphs";
import type { SystemState } from "./model";
import { useNow } from "./useNow";
import "./system.css";

type FlyoutId = "tray" | "network" | "audio" | "battery" | "clock";

type Props = {
  state: SystemState;
  onChange: (update: (state: SystemState) => SystemState) => void;
};

const flyoutLabels: Record<FlyoutId, string> = {
  tray: "Фоновые приложения",
  network: "Сеть",
  audio: "Звук",
  battery: "Батарея",
  clock: "Календарь",
};

export function SystemTail({ state, onChange }: Props) {
  const [open, setOpen] = useState<FlyoutId | null>(null);
  const [anchor, setAnchor] = useState<CSSProperties>({});
  const ref = useRef<HTMLDivElement>(null);
  const now = useNow();
  const { time, day } = clockLines(now);
  const { audio, battery } = state;
  const trayAttention = state.tray.some((icon) => icon.attention);

  // Flyout выравниваем по правому краю дока и ставим над ним.
  useLayoutEffect(() => {
    if (!open) return;
    const pill = ref.current?.closest(".ws-dock__pill");
    if (!pill) return;
    const rect = pill.getBoundingClientRect();
    setAnchor({
      position: "fixed",
      right: window.innerWidth - rect.right,
      bottom: window.innerHeight - rect.top + 10,
    });
  }, [open]);

  const toggle = (id: FlyoutId) => setOpen((current) => (current === id ? null : id));
  const setVolume = (volume: number) =>
    onChange((s) => ({ ...s, audio: { ...s.audio, volume, muted: false } }));

  const button = (id: FlyoutId, label: string, content: ReactNode, extra?: object) => (
    <Tooltip text={label}>
      <button
        type="button"
        className={cx("ws-tail__item", id === "clock" && "ws-tail__item--clock")}
        aria-label={label}
        aria-expanded={open === id}
        aria-haspopup="dialog"
        onClick={() => toggle(id)}
        {...extra}
      >
        {content}
      </button>
    </Tooltip>
  );

  const networkLabel =
    state.network.kind === "wifi"
      ? `Wi-Fi: ${state.network.ssid}`
      : state.network.kind === "ethernet"
        ? "Ethernet"
        : "Нет сети";

  return (
    <div ref={ref} className="ws-tail" data-dismiss-ignore>
      {button(
        "tray",
        trayAttention ? "Фоновые приложения, есть уведомление" : "Фоновые приложения",
        <>
          <Icon name="chevronUp" size={18} />
          {trayAttention && <span className="ws-attention-dot" aria-hidden="true" />}
        </>,
      )}
      {button("network", networkLabel, <NetworkGlyph network={state.network} />)}
      {button(
        "audio",
        audio.muted ? "Звук выключен" : `Громкость ${audio.volume}%`,
        <Icon name={audio.muted ? "speakerMuted" : "speaker"} size={20} />,
        {
          // Колесо — громкость ±2%, средний клик — mute.
          onWheel: (e: React.WheelEvent) =>
            setVolume(Math.min(100, Math.max(0, audio.volume + (e.deltaY < 0 ? 2 : -2)))),
          onMouseDown: (e: React.MouseEvent) => e.button === 1 && e.preventDefault(),
          onAuxClick: (e: React.MouseEvent) =>
            e.button === 1 &&
            onChange((s) => ({ ...s, audio: { ...s.audio, muted: !s.audio.muted } })),
        },
      )}
      {battery &&
        button(
          "battery",
          `Батарея ${battery.percent}%${battery.charging ? ", заряжается" : ""}`,
          <BatteryGlyph battery={battery} />,
        )}
      {button(
        "clock",
        now.toLocaleDateString("ru-RU", { weekday: "long", day: "numeric", month: "long" }),
        <>
          <span className="ws-tail__time">{time}</span>
          <span className="ws-tail__date">{day}</span>
        </>,
      )}

      {createPortal(
        <Flyout
          open={open !== null}
          onClose={() => setOpen(null)}
          label={open ? flyoutLabels[open] : ""}
          anchor="dock"
          className="ws-tail-flyout"
          style={anchor}
        >
          {open === "tray" && <TrayFlyout icons={state.tray} />}
          {open === "network" && (
            <NetworkFlyout
              network={state.network}
              wifiEnabled={state.wifiEnabled}
              networks={state.wifiNetworks}
              onWifiEnabled={(wifiEnabled) => onChange((s) => ({ ...s, wifiEnabled }))}
            />
          )}
          {open === "audio" && (
            <AudioFlyout
              audio={audio}
              onDevice={(defaultId) =>
                onChange((s) => ({ ...s, audio: { ...s.audio, defaultId } }))
              }
              onVolume={setVolume}
              onMute={(muted) => onChange((s) => ({ ...s, audio: { ...s.audio, muted } }))}
            />
          )}
          {open === "battery" && battery && <BatteryFlyout battery={battery} />}
          {open === "clock" && <ClockFlyout now={now} />}
        </Flyout>,
        document.body,
      )}
    </div>
  );
}
