import defaults from "./default-config.json";
import type { Config } from "./schema";

/**
 * Дефолтный конфиг. Тот же JSON встраивает Rust (`src-tauri/src/config.rs`),
 * поэтому в браузерном режиме моки и первый запуск приложения совпадают.
 */
export const defaultConfig = defaults as Config;
