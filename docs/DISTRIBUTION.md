# 便携发布

DingLater 默认发布为自包含 `win-x64` ZIP，不使用 MSIX；ZIP 同时包含 .NET 与 Windows App Runtime，不要求目标机另行安装运行时：

```powershell
.\scripts\Build-Portable.ps1 -Configuration Release -Version 2.0.0
```

脚本执行 locked restore、Release 测试、自包含 publish、文档复制、ZIP 压缩和 SHA-256 生成。输出：

```text
artifacts\release\DingLater-2.0.0-win-x64-portable.zip
artifacts\release\SHA256SUMS.txt
```

用户解压后直接运行 `DingLater.exe`；没有证书导入、指纹确认、UAC 或管理员权限。ZIP 内依赖必须和 EXE 一起保留，不能只复制 EXE。

未签名便携程序可能触发 SmartScreen。“仍要运行”不等于建立发布者身份；SHA-256 只能证明文件与 Release 清单一致。正式公开发布若希望减少警告，应使用受 Windows 信任的代码签名证书或签名服务，但签名不是 DingLater 的运行依赖，私钥不得进入仓库。

当前版本不自动联网检查更新。GitHub Release 应同时上传 ZIP、`SHA256SUMS.txt`、版本说明和兼容性链接。
