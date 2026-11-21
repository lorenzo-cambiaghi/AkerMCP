#!/usr/bin/env node
/**
 * Zero-dependency launcher for AkerMcp.Server.
 *
 * Runs the standalone server binary of this package's version, downloaded once from the GitHub Release into the
 * per-user install folder the editor plugins use too (AkerMcp.Shared.Setup.ServerInstall, same layout):
 *   Windows  %LOCALAPPDATA%\AkerMcp\server\v<version>\win-x64\AkerMcp.Server.exe
 *   macOS    ~/Library/Application Support/AkerMcp/server/v<version>/osx-x64/AkerMcp.Server
 *   Linux    ${XDG_DATA_HOME:-~/.local/share}/AkerMcp/server/v<version>/linux-x64/AkerMcp.Server
 * AKER_MCP_HOME moves the whole folder; AKER_MCP_BINARY runs a binary of your own instead.
 *
 * When an AI client starts it and only an older server is on disk (the day a release lands), it starts that one at
 * once and downloads the new one in the background for the next start: a ~30 MB download inside the client's MCP
 * start-up window would time out exactly when the user expects the update to just work.
 *
 * IMPORTANT: never write to stdout. It belongs to MCP JSON-RPC; every diagnostic goes to stderr.
 */

import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const REPO = "lorenzo-cambiaghi/AkerMCP";
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const pkg = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "package.json"), "utf8"));
const CLI_COMMANDS = new Set(["setup", "doctor", "update", "--version", "-v", "version", "--help", "-h", "help"]);

function log(msg) {
  process.stderr.write(`[aker-mcp] ${msg}\n`);
}

function getTarget() {
  const { platform, arch } = process;
  // The release publishes x64 builds only: Apple Silicon runs osx-x64 under Rosetta, Windows on Arm under emulation.
  if (platform === "win32" && (arch === "x64" || arch === "arm64")) {
    return { rid: "win-x64", exe: "AkerMcp.Server.exe" };
  }
  if (platform === "darwin" && (arch === "x64" || arch === "arm64")) {
    return { rid: "osx-x64", exe: "AkerMcp.Server" };
  }
  if (platform === "linux" && arch === "x64") {
    return { rid: "linux-x64", exe: "AkerMcp.Server" };
  }
  throw new Error(`No AkerMcp.Server build for ${platform}/${arch}. Build it from source with the .NET 8 SDK: https://github.com/${REPO}`);
}

function installRoot() {
  if (process.env.AKER_MCP_HOME) return path.resolve(process.env.AKER_MCP_HOME);
  const home = os.homedir();
  if (process.platform === "win32") {
    return path.join(process.env.LOCALAPPDATA || path.join(home, "AppData", "Local"), "AkerMcp", "server");
  }
  if (process.platform === "darwin") {
    return path.join(home, "Library", "Application Support", "AkerMcp", "server");
  }
  return path.join(process.env.XDG_DATA_HOME || path.join(home, ".local", "share"), "AkerMcp", "server");
}

function binaryPath(version, target) {
  return path.join(installRoot(), `v${version}`, target.rid, target.exe);
}

function compareVersions(a, b) {
  const pa = a.split(".").map(Number);
  const pb = b.split(".").map(Number);
  for (let i = 0; i < 3; i++) {
    if ((pa[i] || 0) !== (pb[i] || 0)) return (pa[i] || 0) - (pb[i] || 0);
  }
  return 0;
}

/** The newest server already on disk, whatever its version, or null. */
function newestInstalled(target) {
  let names;
  try {
    names = fs.readdirSync(installRoot());
  } catch {
    return null;
  }
  const versions = names
    .map((n) => /^v(\d+\.\d+\.\d+)$/.exec(n))
    .filter(Boolean)
    .map((m) => m[1])
    .filter((v) => fs.existsSync(binaryPath(v, target)))
    .sort(compareVersions);
  return versions.length ? { version: versions[versions.length - 1], path: binaryPath(versions[versions.length - 1], target) } : null;
}

async function download(url, destPath, showProgress) {
  const response = await fetch(url, { redirect: "follow", headers: { "User-Agent": `aker-mcp-npm/${pkg.version}` } });
  if (!response.ok) throw new Error(`GitHub answered HTTP ${response.status} for ${url}`);
  const total = Number(response.headers.get("content-length")) || 0;
  const out = fs.createWriteStream(destPath);
  let done = 0;
  let shown = -10;
  try {
    for await (const chunk of response.body) {
      if (!out.write(chunk)) await new Promise((r) => out.once("drain", r));
      done += chunk.length;
      const pct = total ? Math.floor((done / total) * 100) : 0;
      if (showProgress && total && pct >= shown + 10) {
        shown = pct - (pct % 10);
        log(`  ${shown}% of ${(total / 1048576).toFixed(0)} MB`);
      }
    }
  } finally {
    await new Promise((r) => out.end(r));
  }
}

function extractZip(zipPath, targetDir) {
  fs.mkdirSync(targetDir, { recursive: true });
  if (process.platform === "win32") {
    // System32's bsdtar reads zip; a GNU tar earlier on PATH (Git for Windows) does not, and reads "C:" as a host name.
    const systemTar = path.join(process.env.SystemRoot || "C:\\Windows", "System32", "tar.exe");
    try {
      execFileSync(fs.existsSync(systemTar) ? systemTar : "tar", ["-xf", zipPath, "-C", targetDir], { stdio: "ignore" });
      return;
    } catch {
      execFileSync(
        "powershell",
        ["-NoProfile", "-NonInteractive", "-Command",
          `Expand-Archive -LiteralPath '${zipPath.replace(/'/g, "''")}' -DestinationPath '${targetDir.replace(/'/g, "''")}' -Force`],
        { stdio: "ignore" }
      );
      return;
    }
  }
  try {
    execFileSync("unzip", ["-qo", zipPath, "-d", targetDir], { stdio: "ignore" });
  } catch {
    execFileSync("tar", ["-xf", zipPath, "-C", targetDir], { stdio: "ignore" });
  }
}

/** Leftovers of a start that ended mid-download (the server exited, the client killed it): older than an hour. */
function sweepLeftovers(root) {
  const hourAgo = Date.now() - 3600_000;
  for (const name of fs.readdirSync(root)) {
    if (!name.startsWith(".download-") && !name.startsWith(".staging-")) continue;
    const full = path.join(root, name);
    try {
      if (fs.statSync(full).mtimeMs < hourAgo) fs.rmSync(full, { recursive: true, force: true });
    } catch {
      // in use by another start, or already gone
    }
  }
}

/** Downloads and unpacks the server of `version`; returns its path. Concurrent callers are fine: the folder moves in whole. */
async function install(version, target, showProgress) {
  const finalPath = binaryPath(version, target);
  if (fs.existsSync(finalPath)) return finalPath;

  const root = installRoot();
  fs.mkdirSync(root, { recursive: true });
  sweepLeftovers(root);
  const asset = `AkerMcp.Server-${target.rid}.zip`;
  const url = `https://github.com/${REPO}/releases/download/v${version}/${asset}`;
  const stamp = `${process.pid}-${Date.now()}`;
  const zipPath = path.join(root, `.download-${stamp}.zip`);
  const stageDir = path.join(root, `.staging-${stamp}`);

  try {
    if (showProgress) log(`Downloading AkerMcp.Server ${version} (${target.rid})...`);
    await download(url, zipPath, showProgress);
    extractZip(zipPath, stageDir);

    const staged = path.join(stageDir, target.exe);
    if (!fs.existsSync(staged)) throw new Error(`${asset} has no ${target.exe}.`);
    for (const f of fs.readdirSync(stageDir)) {
      if (f !== target.exe && !f.endsWith(".pdb")) fs.rmSync(path.join(stageDir, f), { recursive: true, force: true });
    }
    if (process.platform !== "win32") fs.chmodSync(staged, 0o755);

    const versionDir = path.dirname(path.dirname(finalPath));
    fs.mkdirSync(versionDir, { recursive: true });
    try {
      fs.renameSync(stageDir, path.dirname(finalPath));
    } catch (err) {
      if (!fs.existsSync(finalPath)) throw err; // otherwise another start got there first
    }
    if (showProgress) log(`Installed at ${finalPath}`);
    return finalPath;
  } finally {
    fs.rmSync(zipPath, { force: true });
    fs.rmSync(stageDir, { recursive: true, force: true });
  }
}

/** The binary to run now, and the download to finish in the background, if any. */
async function resolveBinary(interactive) {
  if (process.env.AKER_MCP_BINARY) return { binary: process.env.AKER_MCP_BINARY };

  const target = getTarget();
  const wanted = pkg.version;
  const exact = binaryPath(wanted, target);
  if (fs.existsSync(exact)) return { binary: exact };

  if (!interactive) {
    const fallback = newestInstalled(target);
    if (fallback) {
      log(`Server ${wanted} is not installed yet: starting ${fallback.version} now, downloading ${wanted} for the next start.`);
      const background = install(wanted, target, false).then(
        () => log(`Server ${wanted} is ready; it starts the next time your AI client starts AkerMCP.`),
        (err) => log(`Background download of ${wanted} failed (${err.message}); still using ${fallback.version}.`)
      );
      return { binary: fallback.path, background };
    }
  }
  return { binary: await install(wanted, target, true) };
}

async function main() {
  const cliArgs = process.argv.slice(2);
  const interactive = cliArgs.length > 0 && CLI_COMMANDS.has(cliArgs[0]);

  if (cliArgs[0] === "update") {
    // `npx -y aker-mcp@latest update`: npx has already fetched the newest launcher; this fetches its server now,
    // so the next client start does not have to.
    const { binary } = await resolveBinary(true);
    log(`AkerMCP ${pkg.version} is installed: ${binary}`);
    return;
  }

  const { binary, background } = await resolveBinary(interactive);
  const child = spawn(binary, cliArgs, {
    stdio: "inherit",
    windowsHide: true,
    env: { ...process.env, AKER_MCP_LAUNCHER: process.env.AKER_MCP_LAUNCHER || "npx" }
  });

  const forward = (signal) => {
    if (!child.killed) child.kill(signal);
  };
  process.on("SIGINT", () => forward("SIGINT"));
  process.on("SIGTERM", () => forward("SIGTERM"));

  child.on("error", (err) => {
    log(`Could not start ${binary}: ${err.message}`);
    process.exit(1);
  });

  child.on("exit", (code, signal) => {
    // A background download still running when the server ends is simply abandoned: the next start begins again.
    void background;
    if (signal) process.kill(process.pid, signal);
    else process.exit(code ?? 0);
  });
}

main().catch((err) => {
  log(`Error: ${err.message}`);
  process.exit(1);
});
