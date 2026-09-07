# 便携发布

DingLater 默认发布为自包含 `win-x64` ZIP，不使用 MSIX；ZIP 同时包含 .NET 与 Windows App Runtime，不要求目标机另行安装运行时：

```powershell
.\scripts\Build-Portable.ps1 -Configuration Release -Version 2.3.0
```

脚本执行 locked restore、测试、自包含 publish、逐文件 SHA-256 清单生成、ZIP 压缩和全包 SHA-256 生成；随后将 ZIP 解压到独立目录，验证每个文件，再启动解压后的 EXE 检查真实窗口和退出码。测试使用隔离的合成消息，不访问真实钉钉或用户消息库。只有这些步骤成功后，才替换固定输出目录：

```text
BuildOutput\DingLater.exe
BuildOutput\DingLater-2.3.0-win-x64-portable.zip
BuildOutput\SHA256SUMS.txt
BuildOutput\package-files.json
BuildOutput\Verify-Portable.ps1
```

本机测试直接启动 `BuildOutput\DingLater.exe`，或双击仓库根目录的 `启动最新版本.cmd`。每次构建使用 `.tools` 下独立的临时目录并在结束后清理，同一个工作区的构建以文件锁串行执行。失败的构建不会先删除上一版 `BuildOutput`。如果旧程序仍在使用备份文件，脚本会保留备份并给出路径。

构建还会模拟缺失 `System.Private.CoreLib.dll` 和同尺寸 XAML 文件损坏，确认完整性检查能拒绝这两种包。独立复验已有 ZIP：

```powershell
$zip = '.\BuildOutput\DingLater-2.3.0-win-x64-portable.zip'
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
.\scripts\Test-PortablePackage.ps1 -PackagePath $zip -ExpectedSha256 $hash -ExpectedVersion 2.3.0
```

用户可在完整解压目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Verify-Portable.ps1
```

`package-files.json` 校验每个发布文件的长度及 SHA-256，用于定位解压中断或文件损坏；下载真实性仍应核对 Release 中的 ZIP 校验值。更新时先退出旧程序，将新版完整解压到新目录。本地加密消息仍位于 `%LOCALAPPDATA%\DingLater`，无需复制到程序目录。

用户解压后直接运行 `DingLater.exe`；没有证书导入、指纹确认、UAC 或管理员权限。ZIP 内依赖必须和 EXE 一起保留，不能只复制 EXE。

未签名便携程序可能触发 SmartScreen。“仍要运行”不等于建立发布者身份；SHA-256 只能证明文件与 Release 清单一致。正式公开发布若希望减少警告，应使用受 Windows 信任的代码签名证书或签名服务，但签名不是 DingLater 的运行依赖，私钥不得进入仓库。

当前版本不自动联网检查更新。GitHub Release 应同时上传 ZIP、`SHA256SUMS.txt`、版本说明和兼容性链接。
