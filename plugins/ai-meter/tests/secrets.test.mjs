// Keeping the managed Pi's keys out of what the plugin saves and reports (scripts/lib/secrets.mjs), and the check that
// keeps the managed Pi to Windows folders (isInsideWsl in scripts/lib/workspace.mjs). Files are made up here.

import assert from "node:assert/strict";
import { join } from "node:path";
import { test } from "node:test";
import { managedSecrets, mentions, redact, REDACTED } from "../scripts/lib/secrets.mjs";
import { isInsideWsl } from "../scripts/lib/workspace.mjs";

const folder = join("config", "pi");
const managed = { managed: true, environment: { PI_CODING_AGENT_DIR: folder } };
/** A reader that knows only auth.json in the managed folder, with these contents. */
const authJson = (contents) => (path) => {
  if (path !== join(folder, "auth.json")) throw Object.assign(new Error("ENOENT"), { code: "ENOENT" });
  return typeof contents === "string" ? contents : JSON.stringify(contents);
};

test("the managed copy's keys are every key field in its auth.json, short values left out", () => {
  const read = authJson({ openrouter: { type: "api_key", key: "sk-or-v1-aaaaaaaa" }, groq: { type: "api_key", key: "gsk_bbbbbbbbbbbb" }, odd: { key: "short" }, none: {}, number: { key: 12345678 } });
  assert.deepEqual(managedSecrets(managed, read), ["sk-or-v1-aaaaaaaa", "gsk_bbbbbbbbbbbb"]);
});

test("no keys for a plan that is not managed, a missing folder, file or unreadable file", () => {
  const read = authJson({ openrouter: { key: "sk-or-v1-aaaaaaaa" } });
  assert.deepEqual(managedSecrets({ environment: managed.environment }, read), []);
  assert.deepEqual(managedSecrets({ managed: true, environment: {} }, read), []);
  assert.deepEqual(managedSecrets({ managed: true, environment: { PI_CODING_AGENT_DIR: "elsewhere" } }, read), [], "a missing file");
  assert.deepEqual(managedSecrets(managed, authJson("{ not json")), []);
  assert.deepEqual(managedSecrets(managed, authJson("null")), []);
});

test("redact replaces every occurrence of every key in nested values, and leaves the original alone", () => {
  const secrets = ["sk-or-v1-aaaaaaaa", "gsk_bbbbbbbbbbbb"];
  const value = { answer: "a sk-or-v1-aaaaaaaa b sk-or-v1-aaaaaaaa", list: [1, null, true, { deep: ["x gsk_bbbbbbbbbbbb"] }], "sk-or-v1-aaaaaaaa": "name" };
  const copy = redact(value, secrets);
  assert.deepEqual(copy, { answer: `a ${REDACTED} b ${REDACTED}`, list: [1, null, true, { deep: ["x " + REDACTED] }], [REDACTED]: "name" });
  assert.equal(value.answer, "a sk-or-v1-aaaaaaaa b sk-or-v1-aaaaaaaa", "a copy, not a change in place");
  assert.equal(mentions(value, secrets), true);
  assert.equal(mentions(copy, secrets), false);
});

test("matching is exact: a key split or changed is not recognised, and no keys change nothing", () => {
  const secrets = ["sk-or-v1-aaaaaaaa"];
  assert.equal(redact("sk-or-v1-aaaa aaaa", secrets), "sk-or-v1-aaaa aaaa");
  assert.equal(redact("SK-OR-V1-AAAAAAAA", secrets), "SK-OR-V1-AAAAAAAA");
  assert.equal(mentions({ a: "sk-or-v1-aaaaaaaa" }, []), false);
  assert.deepEqual(redact({ a: "sk-or-v1-aaaaaaaa" }, []), { a: "sk-or-v1-aaaaaaaa" });
});

test("a key that contains another is replaced whole", () => {
  const read = authJson({ a: { key: "abcdefgh" }, b: { key: "abcdefgh-longer" } });
  assert.equal(redact("abcdefgh-longer", managedSecrets(managed, read)), REDACTED);
});

test("a repository inside WSL is recognised by either UNC form, either slash and any case", () => {
  for (const path of ["\\\\wsl.localhost\\Ubuntu\\home\\u\\repo", "\\\\wsl$\\Ubuntu\\repo", "//wsl.localhost/Ubuntu/repo", "\\\\WSL.LOCALHOST\\Ubuntu", "\\\\?\\UNC\\wsl$\\Ubuntu\\repo", "//Wsl$/Debian"]) {
    assert.equal(isInsideWsl(path), true, path);
  }
  for (const path of ["C:\\Users\\u\\repo", "\\\\server\\share\\wsl.localhost", "\\\\wsl.localhostx\\a", "/home/u/repo", "C:\\wsl$\\repo", "", null]) {
    assert.equal(isInsideWsl(path), false, String(path));
  }
});
