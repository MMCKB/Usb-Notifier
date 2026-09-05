# U 盘助手 (UsbFlashToast)

[![Build](https://github.com/MMCKB/USB--/actions/workflows/build.yml/badge.svg)](https://github.com/MMCKB/USB--/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/MMCKB/USB)](https://github.com/MMCKB/USB--/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2B-0078D4)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![UI](https://img.shields.io/badge/UI-WinUI%203-0078D4)

一款 WinUI 3 / Fluent 风格的 U 盘伴侣工具。常驻系统托盘，插入 U 盘即从右下角滑出通知，拔出有线条动画提醒，支持安全弹出、内容分析与多分区设备归并。

## ✨ 功能特性

- **插入通知** —— U 盘插入后从屏幕右下角滑入 + 淡入弹出 Fluent 卡片：设备名、文件系统、容量用量条
- **拔出动画** —— U 盘未安全弹出时，通知切换为「U 盘已拔出」线稿动画（U 盘从 USB 口拔出 + 速度线闪现）
- **安全弹出** —— 弹窗 / 概览 / 托盘菜单三处均可一键安全弹出（`CM_Request_Device_Eject`，失败回退 IOCTL 并给出占用原因）
- **多分区归并** —— 同一物理设备的多个分区合并为一个通知、一条列表项，弹出时整体清理
- **内容分析** —— 扫描 U 盘文件构成：20+ 文件类型分类统计、容量占比、体积最大文件排行、双击定位
- **8 种材质背景** —— 亚克力 / 薄雾 / 磨砂 / 云母 / 云母 Alt / 纯色 / 透明等，弹窗与主窗口实时同步
- **托盘集成** —— 托盘图标显示已连接数量角标；右键菜单可直接打开资源管理器或弹出设备
- **概览窗口** —— 设备列表、容量详情、簇/扇区/序列号/型号/接口/分区等设备信息、智能健康提示
- **贴心细节** —— 开机自启开关；关闭窗口时询问「退出 / 隐藏到托盘」并可勾选不再询问

## 📦 下载

前往 [Releases](https://github.com/MMCKB/USB--/releases) 下载最新的 `UsbFlashToast-x.x.x-win-x64.zip`，解压后运行 `UsbFlashToast.exe` 即可（云编译产物为自包含部署，无需安装 .NET 运行时）。

## 🛠 本地构建

```bash
git clone https://github.com/MMCKB/USB--.git
cd USB
dotnet restore
dotnet build -c Release --no-restore
```

产物位于 `bin/Release/net8.0-windows10.0.22621.0/win-x64/`。要求：Windows 10 1809+ / Windows 11，.NET 8 SDK（仓库通过 `global.json` 固定 SDK 版本）。

运行方式：

| 启动方式 | 命令 | 说明 |
|----------|------|------|
| 常规启动 | `UsbFlashToast.exe` | 常驻托盘 + 打开概览窗口 |
| 静默运行 | `UsbFlashToast.exe --silent` | 仅常驻托盘 |
| 模拟插入 | `UsbFlashToast.exe --demo` | 演示插入通知 |
| 拔出动画 | `UsbFlashToast.exe --demo --demo-removed` | 演示「U 盘已拔出」动画 |

## ☁️ 云编译

仓库内置 GitHub Actions 云编译（[build.yml](.github/workflows/build.yml)），在 **Actions → Build → Run workflow** 手动触发：

| 参数 | 说明 |
|------|------|
| **上传 Release** | 选择是否将编译产物发布到 Release |
| **版本号** | 上传 Release 时必填，如 `v1.0.0` |

- 每次构建都会生成 Artifact，可从运行页面下载；
- 勾选上传 Release 时，会以 `--self-contained` 发布独立部署包并打包为 zip 附到对应版本的 Release。

## 📁 目录结构

```
├── App.xaml / App.xaml.cs            # 应用入口、设备监听、托盘菜单与角标
├── Models/UsbDriveInfo.cs            # 设备/分区/扫描结果数据模型
├── Services/
│   ├── BackgroundHost.cs             # 后台消息宿主（WM_DEVICECHANGE + 托盘）
│   ├── DriveInspector.cs             # WMI 设备探测、多分区归并、安全弹出
│   ├── ContentScanner.cs             # 文件类型扫描与分类统计
│   ├── BackdropHelper.cs             # 8 种材质背景共用逻辑
│   ├── SettingsService.cs            # 设置持久化
│   └── StartupService.cs             # 开机自启
├── Views/
│   ├── ToastWindow.xaml(.cs)         # 右下角通知（插入/提示/拔出线条动画）
│   └── OverviewWindow.xaml(.cs)      # 概览主窗口
├── Native/Win32.cs                   # Win32 P/Invoke
├── Converters/ValueConverters.cs     # XAML 值转换器
├── app.manifest                      # PerMonitorV2 DPI
└── Assets/usb.ico                    # 应用图标
```

## 📄 许可证

[MIT](LICENSE) © 2026 MMCKB
