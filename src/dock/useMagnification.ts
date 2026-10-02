import { useCallback, useEffect, useRef, useState, type PointerEvent } from "react";
import { dock, motion } from "../shared/design/tokens";
import { isSettled, springStep, targetScales } from "./magnification";

function prefersReducedMotion() {
  return window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false;
}

const options = {
  iconSize: dock.iconSize,
  gap: dock.gap,
  max: dock.magnification,
  range: dock.magnificationRange,
};

/**
 * Масштабы элементов ряда дока на пружине.
 * `centers` — базовые центры элементов от центра ряда (см. `baseCenters`).
 * При reduced motion увеличения нет.
 */
export function useMagnification(centers: number[]) {
  const [scales, setScales] = useState<number[]>(() => centers.map(() => 1));
  const cursor = useRef<number | null>(null);
  const values = useRef<number[]>([]);
  const velocities = useRef<number[]>([]);
  const frame = useRef<number | undefined>(undefined);
  const centersRef = useRef(centers);

  useEffect(() => {
    centersRef.current = centers;
  }, [centers]);

  const run = useCallback(() => {
    if (frame.current !== undefined) return;
    let last = performance.now();
    const step = (now: number) => {
      const dt = Math.min((now - last) / 1000, 1 / 30);
      last = now;
      const targets = targetScales(cursor.current, centersRef.current, options);
      let settled = true;
      const next = targets.map((target, i) => {
        const [x, v] = springStep(
          values.current[i] ?? 1,
          velocities.current[i] ?? 0,
          target,
          dt,
          motion.magnification,
        );
        velocities.current[i] = v;
        if (!isSettled(x, v, target)) settled = false;
        return x;
      });
      values.current = settled ? targets : next;
      if (settled) velocities.current = targets.map(() => 0);
      setScales(values.current);
      frame.current = settled ? undefined : requestAnimationFrame(step);
    };
    frame.current = requestAnimationFrame(step);
  }, []);

  useEffect(() => () => cancelAnimationFrame(frame.current ?? 0), []);

  const onPointerMove = useCallback(
    (e: PointerEvent<HTMLElement>) => {
      if (prefersReducedMotion()) return;
      const rect = e.currentTarget.getBoundingClientRect();
      cursor.current = e.clientX - (rect.left + rect.width / 2);
      run();
    },
    [run],
  );

  const onPointerLeave = useCallback(() => {
    cursor.current = null;
    run();
  }, [run]);

  return { scales, onPointerMove, onPointerLeave };
}
