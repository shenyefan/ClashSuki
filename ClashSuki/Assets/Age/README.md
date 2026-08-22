# age 运行时

这里仅保留 ClashSuki 实际调用的 Windows x64 工具：

- `age.exe`：配置加密与订阅解密。
- `age-keygen.exe`：生成接收方公钥和私钥。

二进制来自 age 官方 `v1.3.1` Windows amd64 发布包：

- 上游：https://github.com/FiloSottile/age/releases/tag/v1.3.1
- 压缩包：`age-v1.3.1-windows-amd64.zip`
- 压缩包 SHA-256：`c56e8ce22f7e80cb85ad946cc82d198767b056366201d3e1a2b93d865be38154`
- `age.exe` SHA-256：`90f5cc37249c06e0b302e476a8a63bcefeecd9437c192b8af33e6ff2d69558dd`
- `age-keygen.exe` SHA-256：`8b9c27ef2ab6f215f689bf1e609bf82c8faf4c041f32452fa80396b3f8c4f687`

上游压缩包中的 `age-inspect.exe` 和 `age-plugin-batchpass.exe` 没有运行时调用，
因此不保存在仓库，也不进入应用包。升级时必须重新核对官方发布包校验值、
二进制架构及本目录中的 `LICENSE`。

订阅解密通过 `--identity -` 从标准输入传递私钥；临时目录中只会短暂保存
已经加密的订阅密文。Gist 加密直接使用标准输入/输出，不创建明文临时文件。
