// Keeping the managed Pi's keys out of the plugin's answers (ADR 0004). AI Meter's managed copy keeps the keys the user
// gave it in auth.json, in its own configuration folder (PI_CODING_AGENT_DIR). Pi's tools are not confined to the
// task's folder, so a model can read that file and quote a key in its answer, its error or a tool result. That the
// model's provider then sees the key cannot be prevented here; what the plugin can do is never relay it to the
// coordinator. So once the agent may have run, the whole job is redacted before every save of job.json, from which
// every report is made (report.mjs): its answer and activity, and also Git's description of a worktree (commit
// subjects, file names), error messages and anything else in it.
//
// This means the plugin reads the managed copy's key file, which it otherwise never touches (ADR 0004 keeps the keys
// with AI Meter). It reads it only to remove the keys from what it returns: once before the agent starts and once after
// it ends, so that a key added or rotated meanwhile is covered too. The keys are held in memory for the job only: never
// written to job.json, to a log or to an error message, and never sent anywhere.
//
// It fails closed. A missing file means the copy has no keys; a file that exists but cannot be read or parsed stops
// the candidate before the agent starts, and if the reading after the agent fails, its answer and activity are
// withheld (jobs.mjs). Matching is exact: a key the agent split, encoded or changed is not recognised. Values shorter
// than MINIMUM_SECRET_CHARACTERS are ignored, so that a stray short value cannot blank out ordinary text.
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
 * A plan that is not managed, and a managed copy without the file, give none. Throws when the keys cannot be known:
 * a managed plan that names no configuration folder, or a file that exists but cannot be read, is not JSON or is not
 * an object. The error never quotes the file (a JSON parser's message can).
 */
export function managedSecrets(plan, read = readFileSync) {
  if (!plan?.managed) return [];
  const folder = plan.environment?.PI_CODING_AGENT_DIR;
  if (!folder) throw new Error("AI Meter did not name the managed Pi's configuration folder, so its keys could not be kept out of the report");
  let auth;
  try {
    auth = JSON.parse(read(join(folder, "auth.json"), "utf8"));
  } catch (error) {
    if (error?.code === "ENOENT") return [];
    throw new Error("the managed Pi's key file could not be read, so its keys could not be kept out of the report");
  }
  if (!auth || typeof auth !== "object" || Array.isArray(auth)) throw new Error("the managed Pi's key file is not in the expected form, so its keys could not be kept out of the report");
  const keys = Object.values(auth).map((entry) => entry?.key).filter((key) => typeof key === "string" && key.length >= MINIMUM_SECRET_CHARACTERS);
  return union(keys);
}

/** Every secret of these lists once, longest first, so that a key that contains another is replaced whole. */
export function union(...lists) {
  return [...new Set(lists.flat())].sort((a, b) => b.length - a.length);
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
