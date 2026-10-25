'use strict';
const { seedAssets } = require('./assets');
const { seedOps } = require('./ops');
const { seedHistory } = require('./history');

function buildSeed(now = Date.now()) {
  const assets = seedAssets(now);
  const ops = seedOps(now, assets);
  const history = seedHistory(now, assets);
  return {
    meta: {
      version: 1,
      seededAt: new Date(now).toISOString(),
      seedVersion: '2026.10.25',
      demo: true,
      note: '演示种子数据；首次启动后由内置模拟网关持续写入实时数据。',
    },
    ...assets,
    ...ops,
    ...history,
    diagnoses: [],
    sessions: [],
    paramRequests: [],
    backups: [],
    _seq: {},
  };
}

module.exports = { buildSeed };
