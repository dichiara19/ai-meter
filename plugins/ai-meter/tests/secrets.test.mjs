// What keeps the managed Pi away from secrets and WSL: its keys out of what the plugin saves and reports
// (scripts/lib/secrets.mjs), the coordinator's credentials out of its environment (no_credentials in
// scripts/lib/plan.mjs), and its work on Windows folders only (scripts/lib/workspace.mjs). Files are made up here.

import assert from "node:assert/strict";
import { join } from "node:path";
import { test } from "node:test";
import { environmentFor, holdsCredential, knowsPolicy, noCredentialsEnvironment } from "../scripts/lib/plan.mjs";
import { keysRevision, settledRevision, uncoveredKeyChange, managedSecrets, mentions, redact, REDACTED, union } from "../scripts/lib/secrets.mjs";
import { isInsideWsl, workspaceInsideWsl } from "../scripts/lib/workspace.mjs";

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

test("no keys for a plan that is not managed, or a managed copy without its key file", () => {
  const read = authJson({ openrouter: { key: "sk-or-v1-aaaaaaaa" } });
  assert.deepEqual(managedSecrets({ environment: managed.environment }, read), []);
  assert.deepEqual(managedSecrets({ managed: true, environment: { PI_CODING_AGENT_DIR: "elsewhere" } }, read), [], "a missing file");
});

test("keys that cannot be known stop the caller, without quoting the file", () => {
  const unknown = /could not be kept out of the report/;
  assert.throws(() => managedSecrets({ managed: true, environment: {} }, authJson({})), unknown, "no configuration folder named");
  assert.throws(() => managedSecrets(managed, authJson('{ "a": { "key": "sk-or-v1-aaaaaaaa" ')), (error) => unknown.test(error.message) && !error.message.includes("sk-or"));
  for (const contents of ["null", "[]", '"text"']) assert.throws(() => managedSecrets(managed, authJson(contents)), unknown, contents);
  const denied = () => { throw Object.assign(new Error("EACCES: permission denied"), { code: "EACCES" }); };
  assert.throws(() => managedSecrets(managed, denied), unknown, "a file that exists but cannot be read");
});

test("the key revision is the number in keys-revision, 0 when it is missing, and stops the caller otherwise", () => {
  const revisionFile = (contents) => (path) => {
    if (path !== join(folder, "keys-revision")) throw Object.assign(new Error("ENOENT"), { code: "ENOENT" });
    if (contents instanceof Error) throw contents;
    return contents;
  };
  assert.equal(keysRevision(managed, revisionFile("12\r\n")), 12);
  assert.equal(keysRevision({ managed: true, environment: { PI_CODING_AGENT_DIR: "elsewhere" } }, revisionFile("12")), 0, "a missing file counts as 0");
  assert.equal(keysRevision({ environment: managed.environment }, revisionFile("x")), 0, "not the managed copy");
  for (const contents of ["", "seven", "-1", "1.5", "1e3"]) assert.throws(() => keysRevision(managed, revisionFile(contents)), /not a number/, JSON.stringify(contents));
  assert.throws(() => keysRevision(managed, revisionFile(Object.assign(new Error("EACCES"), { code: "EACCES" }))), /could not be read/);
});

test("the two readings cover the task only when the count after is the even count before, or two more", () => {
  assert.equal(uncoveredKeyChange(4, 4), null, "no change");
  assert.equal(uncoveredKeyChange(4, 6), null, "one complete change: the union of the two readings covers it");
  assert.equal(uncoveredKeyChange(0, 0), null, "no key file before or after");
  assert.match(uncoveredKeyChange(4, 5), /under way or did not finish/, "a change announced, not finished");
  assert.match(uncoveredKeyChange(4, 7), /under way or did not finish/);
  assert.match(uncoveredKeyChange(4, 8), /changed more than once/, "a key added and withdrawn");
  assert.match(uncoveredKeyChange(4, 2), /went back/);
  assert.match(uncoveredKeyChange(4, 0), /went back/, "the managed copy removed: its folder, revision included, is gone");
  assert.match(uncoveredKeyChange(3, 5), /being changed when the task started/, "an odd count before is never a start");
});

test("an odd count before the task is read again until it is even, and skips the candidate if it stays odd", async () => {
  const counts = (...values) => {
    let index = 0;
    return (path) => {
      if (path !== join(folder, "keys-revision")) throw Object.assign(new Error("ENOENT"), { code: "ENOENT" });
      return values[Math.min(index++, values.length - 1)];
    };
  };
  const waits = [];
  const sleep = async (ms) => void waits.push(ms);
  assert.equal(await settledRevision(managed, { read: counts("4"), sleep }), 4);
  assert.deepEqual(waits, [], "an even count is taken at once");
  assert.equal(await settledRevision(managed, { read: counts("5", "5", "6"), sleep, waitMs: 400 }), 6);
  assert.deepEqual(waits, [400, 400]);
  await assert.rejects(settledRevision(managed, { read: counts("5"), sleep, tries: 3 }), /still changing the managed Pi's keys/);
  assert.equal(await settledRevision({ environment: managed.environment }, { read: counts("5"), sleep }), 0, "not the managed copy");
});

test("union keeps every key once, longest first", () => {
  assert.deepEqual(union(["abcdefgh", "abcdefgh-longer"], ["abcdefgh", "zzzzzzzzzz"]), ["abcdefgh-longer", "zzzzzzzzzz", "abcdefgh"]);
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

test("a workspace is judged inside WSL on every spelling: as given, resolved, its real path and its repository", () => {
  const nowhere = () => { throw new Error("ENOENT"); };
  const share = "\\\\wsl.localhost\\Ubuntu\\home\\u\\repo";
  assert.equal(workspaceInsideWsl(".", { cwd: share, realPath: nowhere }), true, '"." from a WSL share');
  assert.equal(workspaceInsideWsl("sub", { cwd: "//wsl$/Ubuntu/repo", realPath: nowhere }), true);
  assert.equal(workspaceInsideWsl("Z:\\repo", { cwd: "C:\\", realPath: () => "\\\\wsl$\\Ubuntu\\repo" }), true, "a mapped drive whose real path is the share");
  assert.equal(workspaceInsideWsl("Z:\\repo", { cwd: "C:\\", realPath: nowhere, top: "//wsl.localhost/Ubuntu/repo" }), true, "the repository Git reports");
  assert.equal(workspaceInsideWsl(".", { cwd: "C:\\Users\\u\\repo", realPath: (p) => p, top: "C:/Users/u/repo" }), false);
  assert.equal(workspaceInsideWsl(".", { cwd: "/home/u/repo", realPath: (p) => p, top: "/home/u/repo" }), false, "a Linux folder");
});

test("no_credentials leaves out every variable whose name holds a credential or a service address", () => {
  for (const name of ["OPENROUTER_API_KEY", "GITHUB_TOKEN", "MY_SERVICE_BASE_URL", "my_apikey", "DB_PASSWORD", "SMTP_PASSWD", "CLIENT_SECRET", "GOOGLE_APPLICATION_CREDENTIALS",
    "SSH_PRIVATE_KEY", "B2_ACCESS_KEY_ID", "STRIPE_KEY", "LLM_API_BASE", "SEARCH_ENDPOINT", "ANTHROPIC_MODEL", "NODE_OPTIONS", "HTTPS_PROXY", "AzureCredential"]) {
    assert.equal(holdsCredential(name), true, name);
  }
  for (const name of ["PATH", "Path", "SystemRoot", "HOME", "USERPROFILE", "LANG", "PI_CODING_AGENT_DIR", "KEYBOARD_LAYOUT", "TEMP", "GIT_EXEC_PATH", "NUMBER_OF_PROCESSORS"]) {
    assert.equal(holdsCredential(name), false, name);
  }
  const base = { Path: "C:\\bin", HOME: "/h", OPENROUTER_API_KEY: "k", GITHUB_TOKEN: "t", MY_SERVICE_BASE_URL: "u", EDITOR: "vim" };
  assert.deepEqual(noCredentialsEnvironment({ PI_CODING_AGENT_DIR: "C:\\cfg", Path: "C:\\git;C:\\bin" }, base),
    { Path: "C:\\git;C:\\bin", HOME: "/h", EDITOR: "vim", PI_CODING_AGENT_DIR: "C:\\cfg" }, "the agent's own variables go on top, unfiltered");
});

test("an environment policy the plugin does not know fails closed", () => {
  const base = { PATH: "/bin", OPENROUTER_API_KEY: "k" };
  assert.equal(knowsPolicy("no_credentials") && knowsPolicy("clean") && knowsPolicy(undefined) && knowsPolicy(null), true);
  assert.equal(knowsPolicy("sandboxed"), false);
  assert.throws(() => environmentFor("sandboxed", {}, base), /does not know/);
  assert.deepEqual(environmentFor(undefined, { A: "1" }, base), { ...base, A: "1" }, "no policy: this process's environment, as before");
  assert.equal(environmentFor("no_credentials", {}, base).OPENROUTER_API_KEY, undefined);
});
