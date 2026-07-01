# ClashSuki MSIX

## 生成包

```powershell
.\ClashSuki.Package\Build-Package.ps1
```

输出目录为 `artifacts\msix`。默认生成未签名包，只用于构建验证。

直接侧载时，应使用受目标设备信任、主题与清单中 `Publisher` 完全一致的代码签名证书：

```powershell
.\ClashSuki.Package\Build-Package.ps1 `
  -CertificatePath C:\secure\ClashSuki.pfx `
  -CertificatePassword $env:CLASHSUKI_PFX_PASSWORD
```

