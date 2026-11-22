// SPDX-License-Identifier: AGPL-3.0-or-later
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import "./index.css";
import { initScheme } from "./theme/apply";
import { registerShell } from "./update/serviceWorker";
import { runEngines } from "./offline/engines";
import { keepStrayDrops } from "./upload/strayDrops";

initScheme();
keepStrayDrops();
void registerShell();
runEngines();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
