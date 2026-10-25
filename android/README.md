# 智赋乡水 · 原生安卓客户端

完全原生实现：Java + Android 框架 API（无 WebView、无 Gradle、无 androidx、无第三方依赖）。
UI 全部由 Java 代码构建，`res/` 只放颜色、文字、样式、XML shape 圆角/徽标/按钮/图标与启动图标。

## 产物

| 项 | 值 |
| --- | --- |
| APK | `dist/zhifu-water-mobile.apk`（约 92 KB） |
| 包名 | `com.ln334.zhifuwater` |
| 版本 | 1.0.0（versionCode 1） |
| SDK | minSdk 24 / targetSdk 34 |
| 启动页 | `com.ln334.zhifuwater.LoginActivity` |
| 签名 | v2 + v3（debug 自签名，`keystore/debug.jks`，store/key 口令 `android`，alias `androiddebugkey`） |

## 构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

脚本逐步执行：aapt2 compile → aapt2 link（生成 R.java）→ javac（`-source 8 -target 8 -bootclasspath android.jar`）→ d8 → jar 写入 classes.dex → zipalign → apksigner，最后自动做
`aapt dump badging`、`apksigner verify`、源码/dex 的「无网页视图控件」检查。退出码 0 即成功。

依赖 JDK 17 与 Android SDK（build-tools 34 / platform 34）。脚本优先读取环境变量 `JAVA_HOME` 与 `ANDROID_SDK_ROOT`；未设置时回退到本机路径 `D:\AndroidBuild\jdk\jdk-17*` 与 `D:\AndroidBuild\sdk`，换机器时改脚本顶部或设环境变量即可。
注意：aapt2 对含中文的绝对路径不友好，脚本因此 `Set-Location` 到工程目录并用相对路径。

## 使用

1. 手机与运行平台的电脑同一局域网；
2. 打开 App，服务器地址填 `http://电脑IP:8765`（只填 IP 时自动补 `:8765`），账号 `admin`，密码 `water123`；
3. 登录后底部 5 个 Tab：总览 / 告警 / 工单 / 巡检 / 我的；顶栏右上角刷新，站点行可切换 site-1 / site-2。

## 页面与接口

| 页面 | 接口 |
| --- | --- |
| 登录 | `POST /api/auth/login`（`{username,password}` → `data.token`），之后全部请求带 `Authorization: Bearer <token>` |
| 总览 | `GET /api/overview?siteId=`（站点信息、KPI、工艺单元、最近告警、数据健康度） |
| 告警 | `GET /api/alarms?siteId=&status=open`；`POST /api/alarms/{id}/ack`（`{"note":"手机端确认"}`） |
| 工单 | `GET /api/workorders?siteId=&status=open` 与 `status=done`；`accept` / `start` / `finish`（`{"result":...}`）/ `verify` |
| 巡检 | `GET /api/inspections?siteId=`；`GET /api/inspections/{id}`；`POST /api/inspections/{id}/submit` |
| 我的 | `GET /api/auth/me`；`GET /api/sites`（切换站点）；修改服务器地址；退出登录 |

## 实现要点

- 网络：`HttpURLConnection`，UTF-8，连接/读取各 10s，固定线程池 + 主线程 `Handler` 回调；错误统一取 `error.message`，超时/域名/连接失败均有中文提示；401 自动清 token 回登录页。
- JSON：`org.json.JSONObject / JSONArray`（白名单内）。
- 视觉：#F2F6F8 背景、#0B7A70 主色、白底 12dp 圆角卡片；告警级别 紧急 #B32D22 / 重要 #A86200 / 次要 #1F6FB2 / 提示 #5D7484；状态「色块 + 文字」双通道；触摸目标 ≥48dp。
- 图标：`GlyphView`（Canvas 线条自绘，24×24 栅格），不依赖 vector 资源。

## 已知偏差 / 限制

- 巡检提交的 `result` 用服务端实际接受的 `ok / attention / abnormal`（平台 `server/api/ops.js` 只认这三个值，任务书里的 `normal` 会被 400 拒绝）；界面上仍显示「正常 / 关注 / 异常」。
- 工艺单元实际返回 12 个（任务书写 11 个，按接口真实数据渲染，横向滚动）。
- 无消息推送、无离线缓存、无图表绘制（KPI 用卡片与大号数字表达，趋势数据未画曲线）。
- 未做深色模式、平板适配与多语言（仅简体中文）。
- 本机没有可用的安卓设备/模拟器（adb devices 为空、SDK 无 system-images），因此只完成编译、打包、签名与包信息校验，未做真机安装验证。
