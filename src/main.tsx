import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import { Playground } from "./playground/Playground";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { isTauri } from "./shared/backend";
import { SpikeRoot, type SpikeInfo } from "./spike/SpikeDock";
import { ShellWindow } from "./windows/ShellWindows";
import type { WindowLabel } from "./windows/shell";
import "./shared/design/tokens.css";
import "./styles.css";

// Витрина компонентов: /?playground или /#/playground.
const isPlayground =
  new URLSearchParams(window.location.search).has("playground") ||
  window.location.hash === "#/playground";

// Спайк WSH-4: Rust подкладывает роль окна (`--features spike`).
const spike = (window as { __WS_SPIKE__?: SpikeInfo }).__WS_SPIKE__;
// В Tauri у каждой части оболочки своё окно; в браузере всё на одной странице (App).
const label = isTauri() ? (getCurrentWindow().label as WindowLabel) : null;

const root = spike ? (
  <SpikeRoot {...spike} />
) : isPlayground ? (
  <Playground />
) : label ? (
  <ShellWindow label={label} />
) : (
  <App />
);

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>{root}</React.StrictMode>,
);
