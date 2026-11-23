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
// it ends, so that a key added or rotated meanwhile is covered too, each time with the file's revision counter, which
// says whether a key could have come and gone between the two readings. The keys are held in memory for the job only:
// never written to job.json, to a log or to an error message, and never sent anywhere.
//
// It fails closed. A missing file means the copy has no keys; a file or revision that exists but cannot be read or
// parsed, or a change of the keys that stays under way, stops the candidate before the agent starts; and if the reading
// after the agent fails, or the revision does not show that the two readings saw every key (uncoveredKeyChange),
// everything the agent produced is withheld (jobs.mjs). Matching is exact: a key the agent split, encoded or changed is
// not recognised. Values shorter than MINIMUM_SECRET_CHARACTERS are ignored, so that a stray short value cannot blank
// out ordinary text.
//
// The revision protocol (AI Meter writes <PI_CODING_AGENT_DIR>/keys-revision atomically; missing counts as 0): every
// change of auth.json raises the count twice, to an odd number before the file is touched and to the next even number
// once the change is complete. So an odd count means a change under way, or one that did not finish (AI Meter finishes
// it at a later sync), and the file is never touched without the count first showing it. The plugin reads, in order:
//   r0 (count, must be even) -> k0 (keys) -> the agent runs -> k1 (keys) -> r1 (count).
// Why that order covers every key the agent could have met:
//   - r1 = r0: no change was announced between r0 and r1, and r0 even means none was still under way at r0, so the file
//     was the same from before k0 until after k1: k0 = k1 is every key it held.
//   - r1 = r0 + 2: exactly one change was announced and completed between r0 and r1; the file held its old keys, then
//     its new ones. The agent ran between k0 and k1: if the change came before k0, k0 has the only keys it could meet;
//     after k1, k1 has them; in between, k0 has the old and k1 the new. The union of k0 and k1 covers each case.
//   - A key added and withdrawn during the task is two changes: r1 = r0 + 4, or odd if the second has not finished.
//   - A change announced but not finished leaves r1 odd. A failed count write leaves the file untouched (the announce
//     comes first), so it is either no change at all, or an unfinished one, shown as odd.
//   - A smaller count means the copy's folder was removed (the count reads as missing, 0), perhaps installed again.
// Anything but r1 = r0 or r0 + 2 is therefore withheld: odd, a larger jump, a smaller number, or a count unreadable.
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

/**
 * The managed copy's key-file revision, the number in <PI_CODING_AGENT_DIR>/keys-revision (the protocol is in the
 * header); a missing file counts as 0. 0 for a plan that is not managed. Throws when the file exists but cannot be read
 * or does not hold a non-negative integer.
 */
export function keysRevision(plan, read = readFileSync) {
  if (!plan?.managed) return 0;
  const folder = plan.environment?.PI_CODING_AGENT_DIR;
  if (!folder) throw new Error("AI Meter did not name the managed Pi's configuration folder, so its keys could not be kept out of the report");
  let text;
  try {
    text = String(read(join(folder, "keys-revision"), "utf8")).trim();
  } catch (error) {
    if (error?.code === "ENOENT") return 0;
    throw new Error("the managed Pi's key revision could not be read, so a change of its keys could not be ruled out");
  }
  if (!/^\d+$/.test(text)) throw new Error("the managed Pi's key revision is not a number, so a change of its keys could not be ruled out");
  return Number(text);
}

/**
 * The revision to start a task from: an even count, read before the keys. An odd count means AI Meter is changing the
 * keys right now, so it is read again, tries times in all, waitMs apart (a couple of seconds by default); still odd,
 * it throws, and the candidate is skipped. read and sleep can be replaced for tests.
 */
export async function settledRevision(plan, { read = readFileSync, tries = 5, waitMs = 500, sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms)) } = {}) {
  for (let attempt = 1; ; attempt++) {
    const revision = keysRevision(plan, read);
    if (revision % 2 === 0) return revision;
    if (attempt >= tries) throw new Error("AI Meter was still changing the managed Pi's keys, so they could not be read before the task; try again shortly");
    await sleep(waitMs);
  }
}

/**
 * Why the keys read before and after a task (in the order the header gives) may miss a key the agent could have met,
 * or null when their union covers every one: the count after must equal the even count before, or be exactly two more.
 */
export function uncoveredKeyChange(before, after) {
  if (before % 2 !== 0) return "the managed Pi's keys were being changed when the task started";
  if (after === before || after === before + 2) return null;
  if (after % 2 !== 0) return "a change of the managed Pi's keys was under way or did not finish when the task ended";
  if (after > before) return "the managed Pi's keys changed more than once during the task, so a key may have come and gone unseen";
  return "the managed Pi's key revision went back during the task, as when its copy is removed, so a change of its keys could not be ruled out";
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
