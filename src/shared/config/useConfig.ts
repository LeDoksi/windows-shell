import { useCallback, useEffect, useState } from "react";
import { backend } from "../backend";
import type { Config } from "./schema";

/** Изменения этого окна — по очереди. */
let chain: Promise<unknown> = Promise.resolve();

/**
 * Каждое изменение применяется к свежему конфигу из Rust, а не к тому, что окно видело при
 * рендере: два бросания подряд иначе меняли одну и ту же старую версию, и второе затирало
 * первое или добавляло плитку повторно (WSH-59).
 */
function queued(change: (config: Config) => Config): Promise<void> {
  const run = chain.then(async () => {
    const latest = await backend.call("config_get");
    await backend.call("config_set", { config: change(latest) });
  });
  chain = run.catch(() => undefined);
  return run;
}

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
      return queued(change);
    },
    [config],
  );

  return { config, update };
}
