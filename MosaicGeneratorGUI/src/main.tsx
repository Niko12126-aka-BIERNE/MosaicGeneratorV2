import React from "react";
import ReactDOM from "react-dom/client";
import { getCurrentWindow } from "@tauri-apps/api/window";
import App from "./App";
import { sidecar } from "./sidecar/client";
import "./index.css";

// Tauri force-kills child processes when the app exits, so give the sidecar a chance to
// cancel a running job and remove its partial output before the window closes.
void getCurrentWindow().onCloseRequested(async () => {
  await sidecar.shutdown(5000);
});

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
