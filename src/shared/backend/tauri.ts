import { invoke } from "@tauri-apps/api/core";
import { emit, listen } from "@tauri-apps/api/event";
import type { Backend } from "./types";

export const tauriBackend: Backend = {
  kind: "tauri",
  call: (command, ...args) => invoke(command, args[0]),
  on: (event, handler) => listen(event, (e) => handler(e.payload as never)),
  emit: (event, payload) => emit(event, payload),
};
