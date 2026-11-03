// Where a delegated task works. Everything an agent reads goes to the model's provider, so an agent never works in
// the coordinator's folder itself:
//   - a read-only task works in a snapshot: a copy of the files Git tracks or would track (not ignored), as they are
//     on disk, without files whose names usually hold secrets (.env and the like), symbolic links or submodules;
//   - a task that may change files works in a new Git worktree on its own branch, made from the last commit, so the
//     coordinator's checkout is never touched. Ignored files such as .env are not in a commit, so not in the worktree.
// A snapshot is removed when the task ends. Nothing is merged, committed or removed from a worktree afterwards: the
// coordinator reviews it and decides. Neither is a sandbox: an agent that reads a path outside its folder can still
// reach it, which is why the agent definitions also restrict what the agent may do.

import { execFileSync } from "node:child_process";
import { copyFileSync, lstatSync, mkdirSync, rmSync } from "node:fs";
import { basename, dirname, join } from "node:path";

function git(folder, ...args) {
  // Only trailing space is trimmed: porcelain status lines start with a space. A large repository lists many files.
  return execFileSync("git", ["-C", folder, ...args], { encoding: "utf8", windowsHide: true, stdio: ["ignore", "pipe", "pipe"], maxBuffer: 256 * 1024 * 1024 }).trimEnd();
}

/** The top folder of the Git repository a folder is in. Throws when there is none. */
export function repositoryOf(workspace) {
  try {
    return git(workspace, "rev-parse", "--show-toplevel");
  } catch {
    throw new Error("Delegation needs a Git repository, so that the agent sees only the files Git tracks: " + workspace + " is not in one.");
  }
}

/**
 * Whether a file's name usually means it holds secrets: .env files and their variants (but not .env.example and
 * the like), package-registry and netrc credentials, private keys and certificates.
 */
export function looksSecret(path) {
  const name = basename(path).toLowerCase();
  if (/^\.env(\..+)?$/.test(name) || name.endsWith(".env")) return !/\.(example|sample|template|dist)$/.test(name);
  return [".npmrc", ".pypirc", ".netrc", "_netrc", ".git-credentials"].includes(name) || /\.(pem|key|p12|pfx)$/.test(name) || /^id_(rsa|dsa|ecdsa|ed25519)/.test(name);
}

/**
 * A snapshot of a workspace for a read-only task, at path. Files are copied as they are on disk, uncommitted changes
 * included. Returns the files left out because their names look like secrets, so the report can say so.
 */
export function createSnapshot(workspace, path) {
  repositoryOf(workspace);
  const listed = git(workspace, "ls-files", "-z", "--cached", "--others", "--exclude-standard").split("\0").filter(Boolean);
  const secretsLeftOut = [];
  mkdirSync(path, { recursive: true });
  for (const file of new Set(listed)) {
    if (looksSecret(file)) {
      secretsLeftOut.push(file);
      continue;
    }
    const source = join(workspace, file);
    let stat;
    try {
      stat = lstatSync(source);
    } catch {
      continue;   // Tracked but deleted on disk.
    }
    // A symbolic link could point at a file outside the workspace, and a submodule is a repository of its own.
    if (!stat.isFile()) continue;
    mkdirSync(dirname(join(path, file)), { recursive: true });
    copyFileSync(source, join(path, file));
  }
  return { path, secrets_left_out: secretsLeftOut };
}

export function removeSnapshot(snapshot) {
  rmSync(snapshot.path, { recursive: true, force: true });
}

/**
 * A new worktree for a modify task, on branch ai-meter/<job id>. Committed files whose names look like secrets are
 * in it, and are listed so that the report can say so.
 */
export function createWorktree(workspace, jobId, path) {
  const top = repositoryOf(workspace);
  const branch = "ai-meter/" + jobId;
  git(top, "worktree", "add", "-b", branch, path, "HEAD");
  const dirty = git(top, "status", "--porcelain").length > 0;
  const secrets = git(path, "ls-files", "-z").split("\0").filter((file) => file && looksSecret(file));
  return {
    path,
    branch,
    repository: top,
    base: git(top, "rev-parse", "HEAD"),
    uncommitted_changes_left_out: dirty,
    ...(secrets.length ? { committed_secrets_visible: secrets } : {}),
  };
}

/** What the agent changed in a worktree: changed and new files, and the diff size. */
export function describeChanges(worktree) {
  const status = git(worktree.path, "status", "--porcelain", "--untracked-files=all");
  const files = status ? status.split("\n").map((line) => ({ status: line.slice(0, 2).trim(), path: line.slice(3) })) : [];
  return {
    ...worktree,
    files,
    diff_stat: files.length ? git(worktree.path, "diff", "--stat", "HEAD") : "",
    review: `git -C "${worktree.path}" diff HEAD`,
    discard: `git -C "${worktree.repository}" worktree remove --force "${worktree.path}" && git -C "${worktree.repository}" branch -D ${worktree.branch}`,
  };
}
