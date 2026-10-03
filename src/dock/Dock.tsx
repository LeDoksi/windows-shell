import { useMemo, type CSSProperties, type ReactNode } from "react";
import { dock } from "../shared/design/tokens";
import { GlassSurface, Tooltip } from "../shared/ui";
import { AppGlyph } from "./AppGlyph";
import { baseCenters } from "./magnification";
import type { DockEntry } from "./model";
import { useMagnification } from "./useMagnification";
import "./dock.css";

const SEPARATOR_WIDTH = 1;

type Slot = { kind: "app"; entry: DockEntry } | { kind: "separator"; id: string };

type Props = {
  pinned: DockEntry[];
  transient: DockEntry[];
  /** `icon` — кнопка иконки: над ней открывается меню. */
  onClickApp: (entry: DockEntry, icon: HTMLElement) => void;
  /** Правый клик: меню приложения. */
  onMenu?: (entry: DockEntry, icon: HTMLElement) => void;
  /** Средний клик: новое окно. */
  onLaunch?: (entry: DockEntry) => void;
  /** Системный хвост справа после разделителя. */
  tail?: ReactNode;
};

const stateLabel: Record<DockEntry["state"], string> = {
  idle: "",
  running: "запущено",
  active: "активно",
  attention: "требует внимания",
};

export function Dock({ pinned, transient, onClickApp, onMenu, onLaunch, tail }: Props) {
  const slots = useMemo<Slot[]>(
    () => [
      ...pinned.map((entry) => ({ kind: "app" as const, entry })),
      ...(transient.length ? [{ kind: "separator" as const, id: "sep-transient" }] : []),
      ...transient.map((entry) => ({ kind: "app" as const, entry })),
    ],
    [pinned, transient],
  );
  const centers = useMemo(
    () =>
      baseCenters(
        slots.map((s) => (s.kind === "app" ? dock.iconSize : SEPARATOR_WIDTH)),
        dock.gap,
      ),
    [slots],
  );
  const { scales, onPointerMove, onPointerLeave } = useMagnification(centers);

  return (
    <div className="ws-dock" style={{ "--ws-dock-icon": `${dock.iconSize}px` } as CSSProperties}>
      <GlassSurface as="nav" level="bar" aria-label="Dock" className="ws-dock__pill">
        <div
          className="ws-dock__apps"
          style={{ gap: dock.gap }}
          onPointerMove={onPointerMove}
          onPointerLeave={onPointerLeave}
        >
          {slots.map((slot, i) => {
            if (slot.kind === "separator") {
              return <span key={slot.id} className="ws-dock__separator" aria-hidden="true" />;
            }
            const { entry } = slot;
            const scale = scales[i] ?? 1;
            const size = dock.iconSize * scale;
            const status = entry.missing ? "не найдено" : stateLabel[entry.state];
            return (
              <Tooltip
                key={entry.id}
                text={entry.missing ? `${entry.title} · Не найдено` : entry.title}
              >
                <button
                  type="button"
                  className="ws-dock__item"
                  data-state={entry.state}
                  data-missing={entry.missing || undefined}
                  aria-label={status ? `${entry.title}, ${status}` : entry.title}
                  aria-haspopup="menu"
                  style={{ width: size, height: size, fontSize: size * 0.42 }}
                  onClick={(e) => onClickApp(entry, e.currentTarget)}
                  onContextMenu={(e) => {
                    e.preventDefault();
                    onMenu?.(entry, e.currentTarget);
                  }}
                  onAuxClick={(e) => {
                    if (e.button === 1 && !entry.missing) onLaunch?.(entry);
                  }}
                >
                  <AppGlyph id={entry.id} title={entry.title} target={entry.target} />
                  <span className="ws-dock__indicator" aria-hidden="true" />
                </button>
              </Tooltip>
            );
          })}
        </div>
        {tail && (
          <>
            <span className="ws-dock__separator" aria-hidden="true" />
            {tail}
          </>
        )}
      </GlassSurface>
    </div>
  );
}
