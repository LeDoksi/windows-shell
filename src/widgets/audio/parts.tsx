import type { CSSProperties } from "react";
import { useSystemIcon } from "../../shared/useSystemIcon";
import { Icon } from "../../shared/ui";

/** Пиковый уровень: тонкая полоска под ползунком, как в EarTrumpet. */
export function PeakBar({ peak }: { peak: number }) {
  return (
    <span
      className="ws-peak"
      aria-hidden="true"
      style={{ "--ws-peak": `${Math.round(Math.min(1, peak) * 100)}%` } as CSSProperties}
    />
  );
}

/** Иконка приложения по его exe; у системных звуков — динамик. */
export function SessionIcon({ exe }: { exe: string | null }) {
  const src = useSystemIcon("file", exe ?? "");
  if (src) return <img className="ws-session__icon" src={src} alt="" />;
  return (
    <span className="ws-session__icon">
      <Icon name="speaker" size={16} />
    </span>
  );
}
