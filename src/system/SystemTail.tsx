import { useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { Flyout, Icon, Tooltip } from "../shared/ui";
import { cx } from "../shared/ui/cx";
import { flyoutLabels, type FlyoutId } from "./flyoutIds";
import { SystemFlyoutContent } from "./SystemFlyoutContent";
import { clockLines } from "./format";
import { BatteryGlyph, NetworkGlyph } from "./glyphs";
import type { SystemState } from "./model";
import { useNow } from "./useNow";
import { inSlot } from "../widgets/resolve";
import { useWidgets } from "../widgets/useWidgets";
import "./system.css";

export type { FlyoutId };

export type SystemChange = (update: (state: SystemState) => SystemState) => void;

type Props = {
  state: SystemState;
  onChange: SystemChange;
  /**
   * Flyout в отдельном окне (Tauri): хвост только сообщает, какой открыть и над чем.
   * Без этого flyout рисуется здесь же, порталом (браузерный режим).
   */
  onFlyout?: (id: FlyoutId | null, pill: DOMRect) => void;
  flyoutOpen?: FlyoutId | null;
};

export function SystemTail({ state, onChange, onFlyout, flyoutOpen }: Props) {
  const [localOpen, setOpen] = useState<FlyoutId | null>(null);
  const open = onFlyout ? (flyoutOpen ?? null) : localOpen;
  const [anchor, setAnchor] = useState<CSSProperties>({});
  const ref = useRef<HTMLDivElement>(null);
  const now = useNow();
  const { time, day } = clockLines(now);
  const { battery } = state;
  // Виджеты хвоста (звук) — вместо значков Windows.
  const { widgets, updateSettings } = useWidgets();
  const trayAttention = state.tray.some((icon) => icon.attention);

  // Flyout выравниваем по правому краю дока и ставим над ним.
  useLayoutEffect(() => {
    if (!open || onFlyout) return;
    const pill = ref.current?.closest(".ws-dock__pill");
    if (!pill) return;
    const rect = pill.getBoundingClientRect();
    setAnchor({
      position: "fixed",
      right: window.innerWidth - rect.right,
      bottom: window.innerHeight - rect.top + 10,
    });
  }, [open, onFlyout]);

  const toggle = (id: FlyoutId) => {
    const pill = ref.current?.closest(".ws-dock__pill");
    if (onFlyout && pill) onFlyout(open === id ? null : id, pill.getBoundingClientRect());
    else setOpen((current) => (current === id ? null : id));
  };
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
      {inSlot(widgets, "dock-tail").map(({ def, settings }) => (
        <def.Compact
          key={def.id}
          settings={settings}
          updateSettings={(change) => void updateSettings(def.id, change)}
          open={open === def.id}
          onToggle={() => toggle(def.id as FlyoutId)}
        />
      ))}
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

      {!onFlyout &&
        createPortal(
          <Flyout
            open={open !== null}
            onClose={() => setOpen(null)}
            label={open ? flyoutLabels[open] : ""}
            anchor="dock"
            className="ws-tail-flyout"
            style={anchor}
          >
            {open && <SystemFlyoutContent id={open} state={state} onChange={onChange} />}
          </Flyout>,
          document.body,
        )}
    </div>
  );
}
