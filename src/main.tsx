import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import { Playground } from "./playground/Playground";
import { SpikeRoot, type SpikeInfo } from "./spike/SpikeDock";
import "./shared/design/tokens.css";
import "./styles.css";

// Витрина компонентов: /?playground или /#/playground.
const isPlayground =
  new URLSearchParams(window.location.search).has("playground") ||
  window.location.hash === "#/playground";

// Спайк WSH-4: Rust подкладывает роль окна (`--features spike`).
const spike = (window as { __WS_SPIKE__?: SpikeInfo }).__WS_SPIKE__;

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    {spike ? <SpikeRoot {...spike} /> : isPlayground ? <Playground /> : <App />}
  </React.StrictMode>,
);
