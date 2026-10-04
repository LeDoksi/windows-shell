import { useMemo, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { dock } from "../shared/design/tokens";
import { GlassSurface, Tooltip, useFileDrop, usePointerDrag, type DragPoint } from "../shared/ui";
import { AppGlyph } from "./AppGlyph";
import { baseCenters } from "./magnification";
import { programsFrom, type DockEntry } from "./model";
import { useMagnification } from "./useMagnification";
import "./dock.css";

const SEPARATOR_WIDTH = 1;

type Slot =
  | { kind: "app"; entry: DockEntry }
  | { kind: "separator"; id: string }
  | { kind: "drop"; id: string };

/** Иконку тащат: `to` — её место среди закреплённых, `null` — не закреплять (незакреплённую). */
type DockDrag = { entry: DockEntry; p: DragPoint; to: number | null };

/** Середины закреплённых иконок по горизонтали, кроме `skip` (CSS-пиксели окна). */
function pinCenters(row: HTMLElement | null, skip?: string) {
  return [...(row?.querySelectorAll<HTMLElement>("[data-pin]") ?? [])]
    .filter((el) => el.dataset.pin !== skip)
    .map((el) => {
      const r = el.getBoundingClientRect();
      return r.left + r.width / 2;
    });
}

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
  /** Закреплённую иконку перетащили на место `index`. */
  onMovePin?: (entry: DockEntry, index: number) => void;
  /** Незакреплённую (запущенную) иконку перетащили к закреплённым — закрепить на `index`. */
  onPinAt?: (entry: DockEntry, index: number) => void;
  /** Из Проводника бросили программы — закрепить с места `index`. */
  onDropPrograms?: (paths: string[], index: number) => void;
};

const stateLabel: Record<DockEntry["state"], string> = {
  idle: "",
  running: "запущено",
  active: "активно",
  attention: "требует внимания",
};

export function Dock({
  pinned,
  transient,
  onClickApp,
  onMenu,
  onLaunch,
  tail,
  onMovePin,
  onPinAt,
  onDropPrograms,
}: Props) {
  const row = useRef<HTMLDivElement>(null);
  const [drag, setDragState] = useState<DockDrag | null>(null);
  const dragRef = useRef(drag);
  const setDrag = (d: DockDrag | null) => {
    dragRef.current = d;
    setDragState(d);
  };
  /** Куда встанут программы из Проводника; `null` — их над доком нет. */
  const [fileIndex, setFileIndex] = useState<number | null>(null);

  const slots = useMemo<Slot[]>(() => {
    // Пока тащат, иконка уже стоит там, куда её отпустят.
    let shownPinned = pinned;
    let shownTransient = transient;
    if (drag && drag.to !== null) {
      shownPinned = pinned.filter((e) => e.id !== drag.entry.id);
      shownPinned.splice(drag.to, 0, drag.entry);
      shownTransient = transient.filter((e) => e.id !== drag.entry.id);
    }
    const apps: Slot[] = shownPinned.map((entry) => ({ kind: "app" as const, entry }));
    if (fileIndex !== null) apps.splice(fileIndex, 0, { kind: "drop", id: "drop" });
    return [
      ...apps,
      // Разделитель остаётся, пока тащат запущенную: за него её можно вернуть.
      ...(shownTransient.length || (drag && !drag.entry.pinned)
        ? [{ kind: "separator" as const, id: "sep-transient" }]
        : []),
      ...shownTransient.map((entry) => ({ kind: "app" as const, entry })),
    ];
  }, [pinned, transient, drag, fileIndex]);
  const centers = useMemo(
    () =>
      baseCenters(
        slots.map((s) => (s.kind === "separator" ? SEPARATOR_WIDTH : dock.iconSize)),
        dock.gap,
      ),
    [slots],
  );
  const { scales, onPointerMove, onPointerLeave } = useMagnification(centers);

  /** Место среди закреплённых под курсором; для незакреплённой правее разделителя — `null`. */
  const pinIndex = (entry: DockEntry, x: number) => {
    if (!entry.pinned) {
      const sep = row.current?.querySelector(".ws-dock__separator")?.getBoundingClientRect();
      if (sep && x > sep.left) return null;
    }
    return pinCenters(row.current, entry.id).filter((c) => c < x).length;
  };
  const startDrag = usePointerDrag<DockEntry>({
    onStart: (entry, p) => {
      // Увеличение мешает целиться: иконки возвращаются к обычному размеру.
      onPointerLeave();
      setDrag({ entry, p, to: pinIndex(entry, p.x) });
    },
    onMove: (p) => {
      const d = dragRef.current;
      if (d) setDrag({ ...d, p, to: pinIndex(d.entry, p.x) });
    },
    onEnd: (p) => {
      const d = dragRef.current;
      setDrag(null);
      if (!d || !p || d.to === null) return;
      if (d.entry.pinned) {
        if (d.to !== pinned.findIndex((e) => e.id === d.entry.id)) onMovePin?.(d.entry, d.to);
      } else onPinAt?.(d.entry, d.to);
    },
  });

  // Программы и ярлыки из Проводника закрепляются; остальное док не берёт (он только для программ).
  useFileDrop((e) => {
    if (e.type === "leave" || !programsFrom(e.paths).length) return setFileIndex(null);
    const index = pinCenters(row.current).filter((c) => c < e.x).length;
    if (e.type === "over") return setFileIndex(index);
    setFileIndex(null);
    onDropPrograms?.(e.paths, index);
  });

  return (
    <div className="ws-dock" style={{ "--ws-dock-icon": `${dock.iconSize}px` } as CSSProperties}>
      <GlassSurface as="nav" level="bar" aria-label="Dock" className="ws-dock__pill">
        <div
          className="ws-dock__apps"
          style={{ gap: dock.gap }}
          ref={row}
          onPointerMove={(e) => !drag && onPointerMove(e)}
          onPointerLeave={onPointerLeave}
        >
          {slots.map((slot, i) => {
            if (slot.kind === "separator") {
              return <span key={slot.id} className="ws-dock__separator" aria-hidden="true" />;
            }
            if (slot.kind === "drop") {
              const size = dock.iconSize * (scales[i] ?? 1);
              return (
                <span
                  key={slot.id}
                  className="ws-dock__drop"
                  style={{ width: size, height: size }}
                  aria-hidden="true"
                />
              );
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
                  data-pin={entry.pinned ? entry.id : undefined}
                  data-dragging={drag?.entry.id === entry.id || undefined}
                  onPointerDown={startDrag(entry)}
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
      {/* Призрак под курсором — порталом: у стеклянных предков ломается position: fixed. */}
      {drag &&
        createPortal(
          <div
            className="ws-dock__ghost"
            style={{
              left: drag.p.x - dock.iconSize / 2,
              top: drag.p.y - dock.iconSize / 2,
              width: dock.iconSize,
              height: dock.iconSize,
              fontSize: dock.iconSize * 0.42,
            }}
            aria-hidden="true"
          >
            <AppGlyph id={drag.entry.id} title={drag.entry.title} target={drag.entry.target} />
          </div>,
          document.body,
        )}
    </div>
  );
}
