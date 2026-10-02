import { useEffect, useState } from "react";

/** Текущее время, обновляется ровно на границе минуты. */
export function useNow() {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    let timer: number;
    const schedule = () => {
      const d = new Date();
      timer = window.setTimeout(
        () => {
          setNow(new Date());
          schedule();
        },
        60_000 - (d.getSeconds() * 1000 + d.getMilliseconds()),
      );
    };
    schedule();
    return () => window.clearTimeout(timer);
  }, []);
  return now;
}
