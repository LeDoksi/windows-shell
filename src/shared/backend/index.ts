import { createMockBackend } from "./mock";
import { tauriBackend } from "./tauri";
import type { Backend } from "./types";

export type * from "./types";

/** В окне Tauri есть `__TAURI_INTERNALS__`; в обычном браузере его нет. */
export function isTauri(): boolean {
  return typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;
}

export const backend: Backend = isTauri() ? tauriBackend : createMockBackend();
