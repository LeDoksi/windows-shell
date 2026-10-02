import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import { Playground } from "./playground/Playground";
import "./shared/design/tokens.css";
import "./styles.css";

// Витрина компонентов: /?playground или /#/playground.
const isPlayground =
  new URLSearchParams(window.location.search).has("playground") ||
  window.location.hash === "#/playground";

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>{isPlayground ? <Playground /> : <App />}</React.StrictMode>,
);
