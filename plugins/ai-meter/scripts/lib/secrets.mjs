// Keeping the managed Pi's keys out of the plugin's answers (ADR 0004). AI Meter's managed copy keeps the keys the user
// gave it in auth.json, in its own configuration folder (PI_CODING_AGENT_DIR). Pi's tools are not confined to the
// task's folder, so a model can read that file and quote a key in its answer, its error or a tool result. That the
// model's provider then sees the key cannot be prevented here; what the plugin can do is never relay it to the
// coordinator. So every string derived from the agent's output is redacted before it is saved in job.json, from which
// every report is made (report.mjs).
//
// The keys are held in memory for the job only: they are never written to job.json, to a log or to an error message.
// Matching is exact: a key the agent split, encoded or changed is not recognised. Values shorter than
// MINIMUM_SECRET_CHARACTERS are ignored, so that a stray short value cannot blank out ordinary text.
//
// events.jsonl and stderr.txt in the job's folder keep the agent's raw output, key included if it printed one. They
// are left as they are: they stay on the user's own disk, under the same account that can read auth.json itself, and
// no tool of this plugin returns their content; the report gives only their paths (report.mjs, mcp-server.mjs,
// delegate.mjs).

import { readFileSync } from "node:fs";
import { join } from "node:path";

export const REDACTED = "[key removed by AI Meter]";
const MINIMUM_SECRET_CHARACTERS = 8;

/**
 * The keys of AI Meter's managed copy that a plan's agent could read: for a plan with managed: true, the string value of
 * every `key` field in <PI_CODING_AGENT_DIR>/auth.json ({ "<provider>": { "type": "api_key", "key": "..." } }).
 * A plan that is not managed, a missing folder, and a file that is missing or cannot be read or parsed give none.
 */
export function managedSecrets(plan, read = readFileSync) {
  const folder = plan?.managed ? plan.environment?.PI_CODING_AGENT_DIR : null;
  if (!folder) return [];
  let auth;
  try {
    auth = JSON.parse(read(join(folder, "auth.json"), "utf8"));
  } catch {
    return [];
  }
  if (!auth || typeof auth !== "object") return [];
  const keys = Object.values(auth).map((entry) => entry?.key).filter((key) => typeof key === "string" && key.length >= MINIMUM_SECRET_CHARACTERS);
  // Longest first, so that a key that contains another is replaced whole.
  return [...new Set(keys)].sort((a, b) => b.length - a.length);
}

/** Whether any string in a JSON-like value contains one of the secrets. */
export function mentions(value, secrets) {
  if (!secrets.length) return false;
  if (typeof value === "string") return secrets.some((secret) => value.includes(secret));
  if (Array.isArray(value)) return value.some((item) => mentions(item, secrets));
  if (value && typeof value === "object") return Object.entries(value).some(([key, item]) => mentions(key, secrets) || mentions(item, secrets));
  return false;
}

/**
 * A deep copy of a JSON-like value with every occurrence of a secret, in any string (property names included),
 * replaced by REDACTED.
 */
export function redact(value, secrets) {
  if (typeof value === "string") return secrets.reduce((text, secret) => text.split(secret).join(REDACTED), value);
  if (Array.isArray(value)) return value.map((item) => redact(item, secrets));
  if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).map(([key, item]) => [redact(key, secrets), redact(item, secrets)]));
  return value;
}
