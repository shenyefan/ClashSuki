<div align="center">
  <img src="ClashSuki/Assets/Branding/logo.png" width="96" alt="ClashSuki">
  <h1>ClashSuki</h1>
  <p>面向 Windows 的现代 Mihomo 图形客户端</p>

  [![Build](https://github.com/shenyefan/ClashSuki/actions/workflows/build.yml/badge.svg)](https://github.com/shenyefan/ClashSuki/actions/workflows/build.yml)
  ![Windows](https://img.shields.io/badge/Windows-10%202004%2B-0078D4?logo=windows)
  ![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
  ![WinUI](https://img.shields.io/badge/UI-WinUI%203-0078D4)
</div>

ClashSuki 使用 WinUI 3 构建，将订阅、代理组、连接、规则、覆写和内核管理整合在原生 Windows 体验中。项目以 Mihomo 为网络核心，并通过独立 Windows 服务为虚拟网卡模式提供受控的提权能力。

## 特色

| 模块 | 能力 |
| --- | --- |
| 原生界面 | WinUI 3、Fluent Design、Mica/Acrylic、系统主题与全局通知 |
| 代理控制 | 系统代理、虚拟网卡、规则/全局/直连模式、托盘快捷操作 |
| 订阅管理 | 远程下载、本地导入、自动更新、流量信息、Token、User-Agent 与 Age 解密 |
| 代理组 | 图标与 Emoji、节点切换、并发延迟测试、筛选及组级排序 |
| 配置体系 | 基础配置、订阅、YAML/JavaScript 覆写与运行时配置分层合成 |
| 运行观测 | 实时流量、内存、连接、日志、规则命中、代理与域名排行 |
| 内核管理 | Mihomo 启停、版本切换、配置校验、GeoData 与外部规则资源 |
| Windows 集成 | MSIX、Portable 服务安装、开机启动、系统托盘和应用修复助手 |

## 系统要求

- Windows 10 版本 2004（Build 19041）或更高版本
- x64 处理器
- [.NET 10 Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0)（Portable 包内附安装器）
- [Windows App Runtime 2.2 x64](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)（Portable 包内附安装器）
- [Microsoft Visual C++ Redistributable x64](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)（Portable 包内附安装器）

MSIX 自包含 .NET 运行时，并通过包清单声明 Windows App SDK 与 VCLibs 官方框架依赖；
Portable ZIP 不内置运行库文件，`Runtime` 目录附带上述三个 Microsoft 官方安装器，
首次使用前需要手动安装。
普通代理和系统代理无需安装服务；首次使用 TUN 时，在“虚拟网卡”页面点击“安装服务”
并确认 Windows 用户账户控制提示。商店应用代理仅在保存回环权限时请求一次管理员权限，
不会安装服务。正式 MSIX 还需要由目标设备信任、且 Publisher 与
清单一致的代码签名证书。

## 从源码构建

推荐使用 Visual Studio，并安装以下工作负载：

- .NET 桌面开发
- Windows 应用 SDK / WinUI
- Windows Application Packaging Project
- Windows 10 SDK 10.0.19041 或更高版本

Visual Studio 中以 `ClashSuki.Package` 为启动/部署项目即可生成包含主程序、Service
和 Repair 的 MSIX。命令行构建入口统一放在仓库 `build` 目录：

```powershell
.\build\Build-Msix.ps1 -Configuration Release -Platform x64
.\build\Build-Portable.ps1 -Configuration Release -Platform x64 -PrerequisiteDirectory <安装器目录>
```

## 自动构建与签名

`Build` workflow 会自动生成并校验 x64 MSIX 和附带运行库安装器的 Portable ZIP。MSIX
默认未签名；配置以下 GitHub Repository Secrets 后，将使用同一张证书自动签名：

| Secret | 内容 |
| --- | --- |
| `WINDOWS_CERTIFICATE_BASE64` | `shenyefan` PFX 文件的 Base64 内容 |
| `WINDOWS_CERTIFICATE_PASSWORD` | PFX 导出密码 |

证书 Subject 必须与清单 Publisher 完全一致，当前均为 `CN=shenyefan`。签名构建产物同时包含公钥 `.cer`；由于当前证书是自签名证书，目标电脑首次安装前仍需将 `.cer` 导入“受信任人”证书存储。

## 项目结构

```text
ClashSuki/
├─ ClashSuki/          WinUI 3 主程序
├─ ClashSuki.Service/  TUN、内核与防火墙的 Windows 服务运行时
├─ ClashSuki.Repair/   MSIX 修复、便携服务安装与一次性提权助手
├─ ClashSuki.Package/  纯 WAP/MSIX 清单与多进程打包项目（无业务代码）
└─ build/              MSIX 与 Portable 构建入口和载荷清单
```

## 致谢

- [Mihomo](https://github.com/MetaCubeX/mihomo)
- [Clash Verge Rev](https://github.com/clash-verge-rev/clash-verge-rev)
- [Mihomo Party](https://github.com/mihomo-party-org/clash-party)
- [Windows Community Toolkit](https://github.com/CommunityToolkit/Windows)
- [Jint](https://github.com/sebastienros/jint)

ClashSuki 是独立项目，与上述项目的维护者及任何代理服务提供商均无隶属关系。请仅使用可信订阅，并遵守所在地法律法规。
