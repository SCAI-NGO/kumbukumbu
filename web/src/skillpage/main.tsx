// SPDX-License-Identifier: AGPL-3.0-or-later
// The skill page's own entry (k.html): strangers open it on the app's origin, so it loads none of the app.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import SkillPage from "./SkillPage";
import "../index.css";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <SkillPage />
  </StrictMode>,
);
