# 便携发布

DingLater 默认发布为自包含 `win-x64` ZIP，不使用 MSIX；ZIP 同时包含 .NET 与 Windows App Runtime，不要求目标机另行安装运行时：

```powershell
.\scripts\Build-Portable.ps1 -Configuration Release -Version 2.5.0
```

脚本执行 locked restore、测试、自包含 publish、逐文件 SHA-256 清单生成、ZIP 压缩和全包 SHA-256 生成；随后将 ZIP 解压到独立目录，验证每个文件，再启动解压后的 EXE 检查真实窗口和退出码。测试使用隔离的合成消息，不访问真实钉钉或用户消息库。只有这些步骤成功后，才替换固定输出目录：

```text
BuildOutput\DingLater.exe
BuildOutput\DingLater-2.5.0-win-x64-portable.zip
BuildOutput\SHA256SUMS.txt
BuildOutput\package-files.json
BuildOutput\Verify-Portable.ps1
```

本机测试直接启动 `BuildOutput\DingLater.exe`，或双击仓库根目录的 `启动最新版本.cmd`。每次构建使用 `.tools` 下独立的临时目录并在结束后清理，同一个工作区的构建以文件锁串行执行。失败的构建不会先删除上一版 `BuildOutput`。如果旧程序仍在使用备份文件，脚本会保留备份并给出路径。

构建还会模拟缺失 `System.Private.CoreLib.dll` 和同尺寸 XAML 文件损坏，确认完整性检查能拒绝这两种包。独立复验已有 ZIP：

```powershell
$zip = '.\BuildOutput\DingLater-2.5.0-win-x64-portable.zip'
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
.\scripts\Test-PortablePackage.ps1 -PackagePath $zip -ExpectedSha256 $hash -ExpectedVersion 2.5.0
```

用户可在完整解压目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Verify-Portable.ps1
```

`package-files.json` 校验每个发布文件的长度及 SHA-256，用于定位解压中断或文件损坏；下载真实性仍应核对 Release 中的 ZIP 校验值。更新时先退出旧程序，将新版完整解压到新目录。本地加密消息仍位于 `%LOCALAPPDATA%\DingLater`，无需复制到程序目录。

用户解压后直接运行 `DingLater.exe`；没有证书导入、指纹确认、UAC 或管理员权限。ZIP 内依赖必须和 EXE 一起保留，不能只复制 EXE。

未签名便携程序可能触发 SmartScreen。“仍要运行”不等于建立发布者身份；SHA-256 只能证明文件与 Release 清单一致。正式公开发布若希望减少警告，应使用受 Windows 信任的代码签名证书或签名服务，但签名不是 DingLater 的运行依赖，私钥不得进入仓库。

从 v2.4.0 起支持可选应用内更新。默认每 6 小时检查 GitHub 最新正式版本，设置可关闭；用户也可立即检查、跳过当前版本、更改下载目录。自动检查不下载或重启。下载采用 GitHub Release asset 的 SHA-256 digest 校验，再校验 ZIP 内的 `package-files.json`；缺少 Windows x64 包或有效 digest 时拒绝更新。

用户确认重启后，从已校验的新版目录启动更新助手；助手检查原进程身份、目录和文件清单，确认准备完成后原应用才退出。助手等待原进程自然结束，备份旧清单内的文件，更新到原程序目录，逐文件复验后重新启动。替换失败时恢复已修改文件，备份保留在原程序目录 `.dinglater-backup/<旧版>-<唯一编号>`；断电后可退出应用，从备份目录恢复文件或完整解压正式 ZIP。原有额外文件与用户数据不会主动清理。

默认下载目录为 `%USERPROFILE%\Downloads\DingLater`，每次下载生成独立子目录；ZIP、解压目录和失败诊断保留在该目录。选定的下载目录不能与程序目录互相包含，也不能经过符号链接/目录联接。目录无写权限或文件被占用时显示失败原因，可重试或手动解压更新。

发布脚本还运行 `Test-UpdateHandoff.ps1`：在两个隔离解压目录中模拟低版本清单，真实执行助手握手、父进程退出、文件替换、备份保留和新版启动退出，全程使用合成数据。它不修改正在使用的 DingLater 实例。GitHub Release 应同时上传 ZIP、`SHA256SUMS.txt`、版本说明和兼容性链接；自动更新只接收 `v主版本.次版本.补丁` 格式的正式版本，忽略草稿、预发布、同版和旧版。
