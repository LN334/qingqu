# 当前架构与端能力

本文记录仓库当前实现，作为继续开发和试点决策的基线。平台尚未接入现场 PLC；界面显示的在线遥测由模拟引擎生成，除明确标注的人工化验数据外，不代表现场实测。

## 版本口径

| 组件 | 当前版本 | 版本来源 |
| --- | --- | --- |
| 平台发布版（Web / 服务端） | 2.0.8 | 根目录 `package.json`；服务端运行时读取 |
| Windows 桌面端 / kiosk | 2.0.8 | .NET 项目与 NSIS 安装脚本，需随平台发布版同步 |
| Android | 2.1.1（versionCode 5） | `android/AndroidManifest.xml`；构建和界面读取 Manifest / APK 元数据 |
| Electron 遗留壳 | 1.0.8 | `desktop/package.json`；v2 Windows 桌面版已由 WPF 取代 |

## 各端能力与数据

| 端 | 业务实现与运行模式 | 本地数据位置与持久性 | 设备接入 / 同步 |
| --- | --- | --- | --- |
| Web | 浏览器 ES Modules + Node.js 服务；服务端规则和模拟引擎运行于单进程 | 服务端 JSON 文件 `data/db.json`；遥测历史在内存，服务重启后重新生成 | 当前为模拟网关；安卓服务器模式通过 HTTP 使用服务端数据。默认仅监听 `127.0.0.1`，局域网需 `--host 0.0.0.0` |
| Windows 桌面 | WPF 原生界面 + `ZhifuWater.Core`，本机驱动模拟业务时钟 | `%LOCALAPPDATA%\ZhifuWater` 下 JSON 数据；桌面与同机 kiosk 使用本地业务层 | 不连接现场 PLC；不与 Web 服务或安卓离线库自动同步 |
| 现场 kiosk | 独立 WPF 全屏入口，共用桌面端 Core 服务和本机数据目录 | 与同机桌面端共用 `%LOCALAPPDATA%\ZhifuWater` 数据 | 自动登录本机账号；仅模拟业务数据，不连接 PLC |
| Android 本机模式 | Java `LocalApi` + `LocalEngine`；本机模拟引擎每 5 秒推进 site-1 | 私有 SQLite 数据库；遥测、告警、工单和巡检操作跨 App 重启保留 | 不连接 PLC，也不与服务器自动同步 |
| Android 服务器模式 | Android API 客户端连接 Node 服务 | 业务数据保存在服务器；手机本机模式数据库与服务器数据分开 | 通过 HTTP API 操作服务器；局域网连接需服务监听局域网地址 |

## 当前边界

- Node、C# Core 和 Android LocalCore 各自实现部分业务规则；目前有契约和自测，但不是同一套可执行业务代码。
- Web 与 Windows 桌面分别维护自己的数据文件；Android 离线数据独立保存在手机。除 Android 服务器模式外，没有跨端同步。
- Node JSON 存储适用于单进程演示；桌面 Core 的定时器与共享 JSON 数据树仍需继续审视线程安全和并发写入。
- 真实现场接入、设备断连恢复、真实命令回执和持续运行指标均未在仓库演示环境中验证。
- Android APK 当前由本地 debug keystore 签名，适合开发和演示；正式发布需要稳定保管发布密钥，并验证升级签名兼容。
