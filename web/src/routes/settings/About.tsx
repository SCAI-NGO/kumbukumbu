// SPDX-License-Identifier: AGPL-3.0-or-later
import { useUpdate } from "../../update/store";
import { appVersion, parseVersion } from "../../update/version";
import { Section } from "./Section";

export default function About() {
  return <AboutSection />;
}

/** This app's build, and the server's when it differs (an update not taken yet). */
function AboutSection() {
  const app = parseVersion(appVersion);
  const server = parseVersion(useUpdate((s) => s.server));
  const differs = app && server && (app.semver !== server.semver || app.sha !== server.sha);
  return (
    <Section title="About">
      <p className="text-text-body">{app ? `Kumbu² ${app.semver} (${app.sha})` : "Kumbu² development build"}</p>
      {differs && <p className="text-text-body">{`Server: ${server.semver} (${server.sha})`}</p>}
    </Section>
  );
}
