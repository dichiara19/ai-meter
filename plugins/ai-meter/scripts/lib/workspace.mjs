// Where a delegated task works. A read-only task works in the coordinator's folder. A task that may change files works
// in a new Git worktree on its own branch, made from the last commit, so the coordinator's checkout is never touched.
// Nothing is merged, committed or removed afterwards: the coordinator reviews the worktree and decides.

import { execFileSync } from "node:child_process";

function git(folder, ...args) {
  // Only trailing space is trimmed: porcelain status lines start with a space.
  return execFileSync("git", ["-C", folder, ...args], { encoding: "utf8", windowsHide: true, stdio: ["ignore", "pipe", "pipe"] }).trimEnd();
}

/** The top folder of the Git repository a folder is in. Throws when there is none. */
export function repositoryOf(workspace) {
  try {
    return git(workspace, "rev-parse", "--show-toplevel");
  } catch {
    throw new Error("A task that changes files needs a Git repository: " + workspace + " is not in one.");
  }
}

/** A new worktree for a modify task, on branch ai-meter/<job id>. */
export function createWorktree(workspace, jobId, path) {
  const top = repositoryOf(workspace);
  const branch = "ai-meter/" + jobId;
  git(top, "worktree", "add", "-b", branch, path, "HEAD");
  const dirty = git(top, "status", "--porcelain").length > 0;
  return { path, branch, repository: top, base: git(top, "rev-parse", "HEAD"), uncommitted_changes_left_out: dirty };
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
