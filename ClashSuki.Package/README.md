# ClashSuki MSIX

## Visual Studio 调试

需要调试虚拟网卡或 Windows 服务时：

1. 将 `ClashSuki.Package` 设为启动项目。
2. 选择 `x64` 和 `Debug`。
3. 使用“本地 Windows 调试器”启动。

Visual Studio 会部署 Debug MSIX，并从包内启动 `ClashSuki.exe`。普通 UI
和配置逻辑可直接启动 `ClashSuki` 项目，但未打包进程不会安装或使用另一套服务。

## 生成包

```powershell
.\ClashSuki.Package\Build-Package.ps1
```

输出使用 Windows Application Packaging Project 的默认目录
`ClashSuki.Package\AppPackages`。默认生成未签名包，只用于构建验证。

直接侧载时，应使用受目标设备信任、主题与清单中 `Publisher` 完全一致的代码签名证书：

```powershell
.\ClashSuki.Package\Build-Package.ps1 `
  -CertificatePath C:\secure\ClashSuki.pfx `
  -CertificatePassword $env:CLASHSUKI_PFX_PASSWORD
```
