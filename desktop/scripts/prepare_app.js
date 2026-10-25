'use strict';

/**
 * 把平台本体（server/ 与 web/）复制到 desktop/app/，供 electron-builder 打包。
 * desktop/app/ 是构建产物，不进入 git。
 */

const fs = require('node:fs');
const path = require('node:path');

const here = __dirname;
const projectRoot = path.resolve(here, '..', '..');
const target = path.join(here, '..', 'app');

function copyDir(name) {
  const src = path.join(projectRoot, name);
  const dst = path.join(target, name);
  if (!fs.existsSync(src)) {
    console.error(`[prepare] 缺少目录：${src}`);
    process.exit(1);
  }
  fs.rmSync(dst, { recursive: true, force: true });
  fs.cpSync(src, dst, { recursive: true });
  console.log(`[prepare] ${name} -> ${dst}`);
}

fs.mkdirSync(target, { recursive: true });
copyDir('server');
copyDir('web');

// 桌面端不需要演示数据库：首次启动由服务端自动生成种子数据。
const dataDir = path.join(target, 'data');
fs.rmSync(dataDir, { recursive: true, force: true });

console.log('[prepare] 完成');
