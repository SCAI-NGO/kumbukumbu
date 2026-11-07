// SPDX-License-Identifier: AGPL-3.0-or-later
// The reader page's own entry (s.html): strangers open it on the app's origin, so it loads none of the app.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import Reader from "./Reader";
import "../index.css";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Reader />
  </StrictMode>,
);
