#!/usr/bin/env node
/**
 * 语法自检：递归遍历 src 目录下所有 .js，逐个执行 `node --check`（child_process.spawnSync）。
 * 任一文件失败即退出 1。
 *
 * 说明：子进程用 stdio: "inherit"，不依赖管道捕获输出——受限沙箱下 piped stdio 会被拒绝，
 * 而 node --check 自身会把错误位置打到 stderr（含文件名），无需再转发。
 */

import { readdirSync, statSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(dirname(fileURLToPath(import.meta.url))); // server/
const srcDir = join(root, "src");

/** 递归列出目录下的 .js 文件 */
function walk(dir) {
  const out = [];
  for (const name of readdirSync(dir).sort()) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) out.push(...walk(full));
    else if (name.endsWith(".js")) out.push(full);
  }
  return out;
}

const files = walk(srcDir);
if (!files.length) {
  console.error("未找到任何 src 下的 .js 文件，检查目录是否为空");
  process.exit(1);
}

let failed = 0;
for (const file of files) {
  const rel = relative(root, file).split("\\").join("/");
  const res = spawnSync(process.execPath, ["--check", file], { stdio: "inherit" });
  if (res.error) {
    failed++;
    console.error(`✗ ${rel}（无法执行 node --check：${res.error.message}）`);
    continue;
  }
  if (res.status !== 0) {
    failed++;
    console.error(`✗ ${rel}`);
    continue;
  }
  console.log(`✓ ${rel}`);
}

if (failed) {
  console.error(`\n语法检查失败：${failed}/${files.length} 个文件`);
  process.exit(1);
}
console.log(`\n语法检查通过：${files.length} 个文件`);
