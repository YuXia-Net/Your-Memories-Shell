# 你的记忆Shell终端 / Your Memories Shell

Windows 原生 SSH 客户端，使用 WinUI 3。无需账号，可在离线或局域网环境中使用。

支持多标签终端、SFTP、连接分组、快捷命令、代理、SSH 跳板、端口转发和 Linux 服务器探针。探针可设置网格、卡片尺寸和标签，不需要在服务器安装软件。

## 使用

解压后运行 `Your-Memories-Shell.exe`。支持 Windows 10 2004 及以上版本和 Windows 11，x64。

设置数据库后。数据库存储在 `Workspace/data`，外观设置保存在 `Workspace/Custom`。更换版本时，请务必迁移 `Workspace`文件夹

数据库密码用于查看、导出凭据和恢复备份；启动锁单独设置。选择随机密码后，不能查看、导出凭据或使用云备份。数据库位置可在“备份与恢复”中修改。

连接设置中勾选“加入服务器探针”，即可显示在探针页面。小卡片显示主要指标，右键可查看详情或编辑连接。字体、颜色和背景在“字体与终端”中实时预览。

程序以单个 EXE 分发，首次启动会解包运行组件，后续启动复用缓存。

## 数据安全

数据库使用 AES-256-GCM 加密。自动打开所需的密钥由 Windows DPAPI 绑定当前账户保存。如果这个 Windows 账户已经被他人控制，数据库密码和启动锁无法保护运行中的数据。云备份只上传加密数据库。

Excel 导出文件包含明文凭据。

## 构建

安装 .NET 8 SDK，在 Windows 中运行：

```powershell
./Build.ps1
```

首次构建需要下载 NuGet 依赖。输出位于 `Release`。

代码采用 MIT 许可证。第三方组件和图标见 `THIRD_PARTY_NOTICES.md`。

[GitHub](https://github.com/YuXia-Net/Your-Memories-Shell) · [反馈问题](https://github.com/YuXia-Net/Your-Memories-Shell/issues)
