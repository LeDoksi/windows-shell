import { useCallback, useEffect, useState } from "react";
import { backend } from "../backend";
import type { Config } from "./schema";

/**
 * Конфиг из Rust с подпиской на `config_changed`: все окна видят одно и то же.
 * `update` отправляет новую версию в Rust, а состояние обновляется по событию.
 */
export function useConfig() {
  const [config, setConfig] = useState<Config | null>(null);

  useEffect(() => {
    let alive = true;
    const unlisten = backend.on("config_changed", (next) => alive && setConfig(next));
    backend.call("config_get").then((c) => alive && setConfig(c));
    return () => {
      alive = false;
      unlisten.then((off) => off());
    };
  }, []);

  const update = useCallback(
    (change: (config: Config) => Config) => {
      if (!config) return Promise.resolve();
      return backend.call("config_set", { config: change(config) });
    },
    [config],
  );

  return { config, update };
}
