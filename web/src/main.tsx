// SPDX-License-Identifier: AGPL-3.0-or-later
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App, { queryClient } from "./App";
import "./index.css";
import { endPendingSignOut } from "./offline/unlock";
import { initScheme } from "./theme/apply";
import { registerShell } from "./update/serviceWorker";
import { runEngines } from "./offline/engines";
import { runOfflineHost } from "./offline/host";
import { keepStrayDrops } from "./upload/strayDrops";

initScheme();
keepStrayDrops();
void registerShell();
runEngines();
runOfflineHost(queryClient);

// A sign-out made offline ends its session before anything else reaches the server (offline mode design §3a).
void endPendingSignOut().then(() =>
  createRoot(document.getElementById("root")!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  ),
);
