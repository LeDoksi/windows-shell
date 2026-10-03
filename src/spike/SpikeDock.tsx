import { invoke } from "@tauri-apps/api/core";
import { useEffect, useState } from "react";
import { Dock } from "../dock/Dock";
import { buildDock, clickApp } from "../dock/model";
import { mockRunning } from "../dock/mockRunning";
import { defaultConfig } from "../shared/config";
import { SystemTail } from "../system/SystemTail";
import { mockSystem } from "../system/mockSystem";
import "./spike.css";

/** Спайк WSH-4: окна задаёт `src-tauri/src/spike.rs`, роль и вариант приходят оттуда. */
export type SpikeInfo = { role: "icons" | "backdrop" | "bg"; variant: string };

export function SpikeRoot({ role, variant }: SpikeInfo) {
  useEffect(() => {
    document.documentElement.dataset.spike = variant;
  }, [variant]);
  if (role === "bg")
    return <div className="spike-bg">{"Проверка размытия · 0123456789 · ".repeat(60)}</div>;
  return role === "icons" ? <SpikeDock /> : null;
}

function SpikeDock() {
  const [running, setRunning] = useState(mockRunning);
  const [system, setSystem] = useState(() => mockSystem(false));
  const dock = buildDock(defaultConfig.dock.pinned, running);

  // Каждый кадр сверяем прямоугольник таблетки и отдаём его в Rust, если он изменился.
  useEffect(() => {
    let frame = 0;
    let last = "";
    const tick = () => {
      const pill = document.querySelector(".ws-dock__pill");
      if (pill) {
        const r = pill.getBoundingClientRect();
        const radius = parseFloat(getComputedStyle(pill).borderTopLeftRadius) || 0;
        // DPI в ключе: при переезде на другой монитор CSS-размеры те же, а физические — нет.
        const key = `${r.x},${r.y},${r.width},${r.height},${devicePixelRatio}`;
        if (key !== last) {
          last = key;
          // Физические пиксели считает сам WebView2: его devicePixelRatio, а не scale_factor окна
          // (на мониторе 175 % они расходятся — проверено).
          const d = devicePixelRatio;
          void invoke("spike_pill", {
            x: r.x * d,
            y: r.y * d,
            w: r.width * d,
            h: r.height * d,
            radius: radius * d,
          });
        }
      }
      frame = requestAnimationFrame(tick);
    };
    frame = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(frame);
  }, []);

  return (
    <Dock
      pinned={dock.pinned}
      transient={dock.transient}
      onClickApp={(entry) => setRunning((r) => clickApp(r, entry.id, entry.title))}
      tail={<SystemTail state={system} onChange={setSystem} />}
    />
  );
}
