// SPDX-License-Identifier: AGPL-3.0-or-later
import { useByok } from "../../api/notes";
import ByokSection from "./ByokSection";

/**
 * The services outside this server that a person brings along, Embeddings first (her own TensorX key). Its own page
 * because the key was hard to find among the vaults; more may follow, an LLM perhaps.
 */
export default function External() {
  const byok = useByok();
  if (byok.data && !byok.data.offered) return <p className="text-text-body">This server offers no external services.</p>;
  return <ByokSection />;
}
