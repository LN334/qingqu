'use strict';

/**
 * 智赋乡水 · 智慧水处理平台 —— 桌面端启动器
 *
 * 思路：桌面端不重写平台，只做「一键启动」的外壳。
 *  - 用 Electron 自带的 Node 运行时启动平台服务（server/index.js）；
 *  - 数据写到用户目录（%APPDATA%/智赋乡水/data），升级/卸载不丢数据；
 *  - 服务就绪后用窗口打开本机地址，菜单里可重新加载、在浏览器打开、打开数据目录。
 */

const { app, BrowserWindow, Menu, shell, dialog } = require('electron');
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const net = require('node:net');
const path = require('node:path');

const PORT_START = 8765;
const PORT_TRIES = 20;
const startedAt = Date.now();

const argOf = (name) => {
  const hit = process.argv.find((a) => a.startsWith(`--${name}=`));
  return hit ? hit.slice(name.length + 3) : null;
};
const shotPath = argOf('screenshot');
const isKiosk = process.argv.includes('--kiosk');
const isSmoke = process.argv.includes('--smoke');

let child = null;
let mainWindow = null;
let serverPort = 0;
let quitting = false;


function appRoot() {
  return app.isPackaged ? path.join(app.getAppPath(), 'app') : path.join(__dirname, '..');
}

function serverEntry() {
  return path.join(appRoot(), 'server', 'index.js');
}

function dataDir() {
  return path.join(app.getPath('userData'), 'data');
}

function logDir() {
  return path.join(app.getPath('userData'), 'logs');
}

function freePort(port) {
  return new Promise((resolve) => {
    const srv = net.createServer();
    srv.once('error', () => resolve(freePort(port + 1)));
    srv.once('listening', () => srv.close(() => resolve(port)));
    srv.listen(port, '127.0.0.1');
  });
}

async function waitForServer(port, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/api/ping`, {
        signal: AbortSignal.timeout(1500),
      });
      if (res.ok) return true;
    } catch {
      /* 服务还没起来，继续等 */
    }
    await new Promise((r) => setTimeout(r, 400));
  }
  return false;
}

function startServer(port) {
  const dir = dataDir();
  fs.mkdirSync(dir, { recursive: true });
  fs.mkdirSync(logDir(), { recursive: true });
  const logFile = path.join(logDir(), 'server.log');
  const logStream = fs.createWriteStream(logFile, { flags: 'a' });
  logStream.write(`\n===== ${new Date().toISOString()} 启动 端口 ${port} =====\n`);

  const env = { ...process.env, ELECTRON_RUN_AS_NODE: '1' };
  delete env.ELECTRON_NO_ATTACH_CONSOLE;

  child = spawn(
    process.execPath,
    [serverEntry(), '--port', String(port), '--host', '127.0.0.1', '--data', dir],
    {
      cwd: appRoot(),
      env,
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    }
  );

  child.stdout.on('data', (buf) => logStream.write(buf));
  child.stderr.on('data', (buf) => logStream.write(buf));
  child.on('exit', (code) => {
    logStream.write(`\n===== 服务退出 code=${code} =====\n`);
    logStream.end();
    if (!quitting) {
      dialog.showErrorBox(
        '平台服务已停止',
        `后台服务意外退出（code=${code}）。\n日志：${logFile}\n请重新启动应用。`
      );
      app.quit();
    }
  });
  return child;
}

function stopServer() {
  quitting = true;
  if (!child) return;
  try {
    child.kill();
  } catch {
    /* 已退出 */
  }
  child = null;
}

function buildMenu() {
  const template = [
    {
      label: '应用',
      submenu: [
        { role: 'reload', label: '重新加载' },
        { role: 'forceReload', label: '强制重新加载' },
        { type: 'separator' },
        {
          label: '在浏览器中打开',
          click: () => {
            if (serverPort) shell.openExternal(`http://127.0.0.1:${serverPort}/`);
          },
        },
        {
          label: '打开数据目录',
          click: () => {
            fs.mkdirSync(dataDir(), { recursive: true });
            shell.openPath(dataDir());
          },
        },
        {
          label: '打开日志目录',
          click: () => {
            fs.mkdirSync(logDir(), { recursive: true });
            shell.openPath(logDir());
          },
        },
        { type: 'separator' },
        { role: 'quit', label: '退出' },
      ],
    },
    {
      label: '视图',
      submenu: [
        { role: 'resetZoom', label: '实际大小' },
        { role: 'zoomIn', label: '放大' },
        { role: 'zoomOut', label: '缩小' },
        { type: 'separator' },
        { role: 'togglefullscreen', label: '全屏' },
        ...(app.isPackaged ? [] : [{ role: 'toggleDevTools', label: '开发者工具' }]),
      ],
    },
    {
      label: '帮助',
      submenu: [
        {
          label: '关于本平台',
          click: () => {
            dialog.showMessageBox({
              type: 'info',
              title: '关于',
              message: '智赋乡水 · 智慧水处理平台',
              detail:
                '方案 V2.2 可运行演示版（单站）。\n' +
                '数据由内置模拟网关生成，不连接现场 PLC。\n' +
                `版本 ${app.getVersion()} · 端口 ${serverPort || '未启动'}`,
              buttons: ['好'],
            });
          },
        },
      ],
    },
  ];
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

async function createWindow() {
  const port = await freePort(PORT_START);
  serverPort = port;
  startServer(port);

  const ready = await waitForServer(port);
  if (!ready) {
    dialog.showErrorBox(
      '平台启动失败',
      `后台服务在 30 秒内没有就绪。\n日志：${path.join(logDir(), 'server.log')}\n请关闭后重试。`
    );
    stopServer();
    app.quit();
    return;
  }

  if (isSmoke) {
    console.log(`SMOKE_OK port=${port} data=${dataDir()}`);
    stopServer();
    app.quit();
    return;
  }

  const iconPath = path.join(__dirname, 'build', 'icon.png');
  mainWindow = new BrowserWindow({
    width: isKiosk ? 1920 : 1440,
    height: isKiosk ? 1080 : 900,
    minWidth: 1080,
    minHeight: 700,
    show: false,
    backgroundColor: '#f2f6f8',
    title: '智赋乡水 · 智慧水处理平台',
    autoHideMenuBar: false,
    ...(fs.existsSync(iconPath) ? { icon: iconPath } : {}),
    webPreferences: {
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  const route = isKiosk ? 'kiosk' : 'dashboard';
  await mainWindow.loadURL(`http://127.0.0.1:${port}/#/${route}`);
  if (isKiosk) mainWindow.setFullScreen(true);
  mainWindow.once('ready-to-show', () => mainWindow.show());

  if (shotPath) {
    mainWindow.show();
    await new Promise((r) => setTimeout(r, 3500));
    try {
      const image = await mainWindow.webContents.capturePage();
      fs.writeFileSync(shotPath, image.toPNG());
      console.log(`SCREENSHOT_OK ${shotPath}`);
    } catch (err) {
      console.error('SCREENSHOT_FAIL', err);
    }
    stopServer();
    app.quit();
  }

  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    shell.openExternal(url);
    return { action: 'deny' };
  });

  mainWindow.on('closed', () => {
    mainWindow = null;
  });
}

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.focus();
    }
  });

  app.whenReady().then(async () => {
    app.setAppUserModelId('com.ln334.zhifuwater');
    buildMenu();
    await createWindow();
    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) createWindow();
    });
  });

  app.on('window-all-closed', () => {
    stopServer();
    app.quit();
  });

  app.on('before-quit', stopServer);
  app.on('will-quit', stopServer);

  process.on('exit', () => {
    try {
      if (child) child.kill();
    } catch {
      /* ignore */
    }
  });
}
