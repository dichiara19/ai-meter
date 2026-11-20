// The shell chosen for AI Meter's managed Pi (scripts/lib/shell.mjs), with Windows environments and files made up here,
// so that the tests do not depend on what is installed and run on Linux too.

import assert from "node:assert/strict";
import { test } from "node:test";
import { findGitBash, shellFor } from "../scripts/lib/shell.mjs";

const windows = {
  ProgramFiles: "C:\\Program Files",
  ProgramW6432: "C:\\Program Files",
  "ProgramFiles(x86)": "C:\\Program Files (x86)",
  LOCALAPPDATA: "C:\\Users\\u\\AppData\\Local",
  SystemRoot: "C:\\WINDOWS",
  Path: "C:\\WINDOWS\\system32;C:\\WINDOWS;C:\\Program Files\\nodejs\\",
};

/** A file-existence check that knows only these files, compared without case as on Windows. */
const only = (...files) => (path) => files.some((file) => file.toLowerCase() === path.toLowerCase());

const powershellArguments = ["--tools", "read,powershell,edit,write"];
const bashArguments = ["--tools", "read,bash,edit,write"];
// AI Meter offers the git_bash alternative only while it has bound the managed copy to Git Bash (Pi's shellPath).
const modify = { managed: true, commands: "powershell", arguments: powershellArguments, alternatives: [{ shell: "git_bash", arguments: bashArguments }] };
const unbound = { managed: true, commands: "powershell", arguments: powershellArguments };

test("Git for Windows in its standard folder is found", () => {
  assert.equal(findGitBash(windows, only("C:\\Program Files\\Git\\bin\\bash.exe")), "C:\\Program Files\\Git\\bin");
  assert.equal(findGitBash(windows, only("C:\\Users\\u\\AppData\\Local\\Programs\\Git\\bin\\bash.exe")), "C:\\Users\\u\\AppData\\Local\\Programs\\Git\\bin", "a per-user installation");
});

test("Git only on PATH, through its cmd folder, is found by the sibling bin folder", () => {
  const env = { ...windows, Path: windows.Path + ';"D:\\Tools\\Git\\cmd"' };
  assert.equal(findGitBash(env, only("D:\\Tools\\Git\\cmd\\git.exe", "D:\\Tools\\Git\\bin\\bash.exe")), "D:\\Tools\\Git\\bin");
  assert.equal(findGitBash(env, only("D:\\Tools\\Git\\cmd\\git.exe")), null, "a git.exe without Git Bash beside it is not enough");
});

test("without the git_bash alternative, a managed modify task keeps the PowerShell arguments, even where Git is found", () => {
  assert.deepEqual(shellFor(unbound, windows, only()), { name: "powershell", arguments: powershellArguments, environment: {} });
  assert.deepEqual(shellFor(unbound, windows, only("C:\\Program Files\\Git\\bin\\bash.exe")), { name: "powershell", arguments: powershellArguments, environment: {} },
    "the plugin's own search does not bind Pi to a shell");
});

test("with the alternative but no Git found by the plugin, Git Bash is used as AI Meter bound it, with no PATH change", () => {
  assert.equal(findGitBash(windows, only()), null);
  assert.deepEqual(shellFor(modify, windows, only()), { name: "git_bash", arguments: bashArguments, environment: {} });
});

test("WSL's bash.exe in System32 is never taken for Git Bash, nor put on PATH", () => {
  const exists = only("C:\\WINDOWS\\system32\\bash.exe", "C:\\WINDOWS\\system32\\git.exe", "C:\\WINDOWS\\bin\\bash.exe");
  assert.equal(findGitBash(windows, exists), null);
  assert.deepEqual(shellFor(modify, windows, exists).environment, {});
  assert.equal(shellFor(unbound, windows, exists).name, "powershell");
});

test("with Git Bash, a managed modify task gets the git_bash arguments and Git's bin folder first on PATH", () => {
  const shell = shellFor(modify, windows, only("C:\\Program Files\\Git\\bin\\bash.exe"));
  assert.equal(shell.name, "git_bash");
  assert.deepEqual(shell.arguments, bashArguments);
  assert.deepEqual(shell.environment, { Path: "C:\\Program Files\\Git\\bin;" + windows.Path });
});

test("the PATH variable keeps the spelling the environment already uses, and names are found without case", () => {
  const { Path, ProgramFiles, ...rest } = windows;
  const env = { ...rest, PROGRAMFILES: ProgramFiles, PATH: Path };
  assert.deepEqual(Object.keys(shellFor(modify, env, only("C:\\Program Files\\Git\\bin\\bash.exe")).environment), ["PATH"]);
  const { Path: _, ...withoutPath } = windows;
  assert.deepEqual(shellFor(modify, withoutPath, only("C:\\Program Files\\Git\\bin\\bash.exe")).environment, { PATH: "C:\\Program Files\\Git\\bin" });
});

test("a read-only managed task, and any other dispatch, are started as described", () => {
  const readOnly = { managed: true, commands: "none", arguments: ["--tools", "read,grep,find,ls"] };
  const exists = only("C:\\Program Files\\Git\\bin\\bash.exe");
  assert.deepEqual(shellFor(readOnly, windows, exists), { name: "none", arguments: readOnly.arguments, environment: {} });
  const own = { arguments: ["-p"], alternatives: [{ shell: "git_bash", arguments: ["other"] }] };
  assert.deepEqual(shellFor(own, windows, exists), { name: null, arguments: ["-p"], environment: {} }, "only the managed copy is changed");
});
