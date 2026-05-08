#!/usr/bin/env node
// Publish AIMemory.Api and AIMemory.Ingestor into the Tauri bundle-resources tree so
// the NSIS installer can copy them into the install directory.
//
// Phase 11: previously the Tauri bundle shipped only the desktop shell + metadata, leaving
// the .NET services missing from the installer entirely. This script is invoked by
// `npm run prepublish:dotnet` (also chained from `npm run tauri:build`) and writes its
// output to `src-tauri/bundle-resources/{api,ingestor}/`. `tauri.conf.json` then references
// those paths via `bundle.resources`, which Tauri's NSIS template copies to
// `$INSTDIR/resources/{api,ingestor}/` at install time. The `installer.nsh` hooks register
// `aimemory-api` / `aimemory-ingestor` Windows services pointing at those paths.
//
// Cross-platform Node script (rather than a .ps1 / .sh) so devs on any platform can run
// `npm run prepublish:dotnet` and get the same behavior. The publish itself is `dotnet
// publish` which is universal.

import { spawnSync } from "node:child_process";
import { rmSync, mkdirSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

// Resolve repo root: apps/desktop/scripts -> ../../.. -> repo root
const repoRoot = resolve(__dirname, "..", "..", "..");
const bundleRoot = resolve(__dirname, "..", "src-tauri", "bundle-resources");

// One publish target per .NET service we ship. Output dirs are blown away each run so
// stale binaries from a previous publish don't survive into the installer.
const targets = [
    {
        project: resolve(repoRoot, "src", "AIMemory.Api", "AIMemory.Api.csproj"),
        outDir: resolve(bundleRoot, "api"),
        label: "AIMemory.Api",
    },
    {
        project: resolve(repoRoot, "src", "AIMemory.Ingestor", "AIMemory.Ingestor.csproj"),
        outDir: resolve(bundleRoot, "ingestor"),
        label: "AIMemory.Ingestor",
    },
];

// Match the publish profile in src/Publish.props: framework-dependent, single-file,
// win-x64 (the only RID we ship Windows installers for in phase 11).
const configuration = process.env.AIMEMORY_PUBLISH_CONFIG || "Release";
const runtime = process.env.AIMEMORY_PUBLISH_RID || "win-x64";

function run(cmd, args, cwd) {
    const result = spawnSync(cmd, args, {
        cwd,
        stdio: "inherit",
        shell: process.platform === "win32",
    });
    if (result.status !== 0) {
        process.exit(result.status ?? 1);
    }
}

console.log(`[publish-dotnet] repoRoot=${repoRoot}`);
console.log(`[publish-dotnet] bundleRoot=${bundleRoot}`);
console.log(`[publish-dotnet] configuration=${configuration} runtime=${runtime}`);

if (existsSync(bundleRoot)) {
    rmSync(bundleRoot, { recursive: true, force: true });
}
mkdirSync(bundleRoot, { recursive: true });

for (const t of targets) {
    console.log(`\n[publish-dotnet] publishing ${t.label} -> ${t.outDir}`);
    mkdirSync(t.outDir, { recursive: true });
    run(
        "dotnet",
        [
            "publish",
            t.project,
            "-c", configuration,
            "-r", runtime,
            "-o", t.outDir,
            "--nologo",
        ],
        repoRoot,
    );
}

console.log("\n[publish-dotnet] done.");
