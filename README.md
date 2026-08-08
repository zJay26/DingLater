<div align="center">

# DingLater

**本地“稍后回复箱”：Windows 只读解析钉钉 V3 本地数据，Android 只读取用户授权后的新通知。**

[Windows x64 下载](https://github.com/zJay26/DingLater/releases/latest) · [Android 开发版](docs/ANDROID.md) · [隐私与安全](docs/PRIVACY.md) · [兼容性与验收](docs/COMPATIBILITY.md)

[![Release](https://img.shields.io/github/v/release/zJay26/DingLater?display_name=tag)](https://github.com/zJay26/DingLater/releases/latest)
[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4?logo=windows11&logoColor=white)](https://github.com/zJay26/DingLater/releases/latest)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Android 10+](https://img.shields.io/badge/Android-10%2B-3DDC84?logo=android&logoColor=white)](docs/ANDROID.md)

</div>

DingLater 是一个本地“稍后回复箱”，目前包含彼此独立的 Windows 客户端和 Android 0.1.0 通知版。Windows 客户端以只读方式解析当前用户的钉钉 V3 本地数据库；Android 客户端只读取用户授权后新出现的钉钉系统通知。两端都不会打开钉钉会话、发送消息或同步彼此数据。

> DingLater 的代码路径不会写入钉钉文件，也没有发送已读回执的网络或界面操作。发送方是否始终保持“未读”仍需用两个账号做最终验收，项目不会把静态保证描述成端到端保证。

## Android 0.1.0（开发版）

Android 版支持 Android 10 及以上，工程位于 `android/`，包名为 `io.github.zjay26.dinglater`。它没有 `INTERNET`、Accessibility、存储读取或精确闹钟权限；正文、发送者和会话名使用 Android Keystore AES-GCM 逐字段加密，只保存在设备本地。

本地构建后安装：

```powershell
.\android\Build-Debug.ps1
adb install -r .\BuildOutput\DingLater-android-0.1.0-debug.apk
```

当前修复版 APK 的 SHA-256：

```text
5a3ec05c858c08a0ed10be8db7a126a653e54d04d67d42f7ec6c229f21410c18
```

首次打开后按界面说明开启“通知使用权”；Android 13 及以上还需单独允许 DingLater 发送提醒通知。修复版会串行处理钉钉的连续通知更新，普通单条解析失败不会再永久停止后续捕获。通知载荷可能受钉钉版本、会话通知设置和系统隐私设置影响，来源不是 `MessagingStyle` 或展开正文时，界面会显示“内容可能不完整”。

详细说明见 [Android 安装与开发](docs/ANDROID.md) 和 [Android 真机验收](docs/ANDROID_TESTING.md)。Debug APK 使用开发机默认签名，更换开发机或 Debug keystore 后可能无法覆盖安装并保留旧数据。

## 下载与运行

普通用户只需要下面几步：

1. 打开 [GitHub Releases](https://github.com/zJay26/DingLater/releases)，进入最新版本。
2. 下载名称类似 `DingLater-2.1.2-win-x64-portable.zip` 的 ZIP 文件。
3. 把 ZIP 解压到一个固定文件夹，然后双击里面的 `DingLater.exe`。
4. 第一次启动时，允许 DingLater 只读读取本机钉钉数据；它只会从此刻开始记录新消息。

便携版不用安装，也不需要管理员权限。最好先用 `SHA256SUMS.txt` 校验下载文件，再运行程序。Windows 如果弹出 SmartScreen 提示，这是因为程序暂未签名。

校验方法：把 ZIP 和 `SHA256SUMS.txt` 放在同一个文件夹，在该文件夹打开 PowerShell，运行下面的命令（文件名不同时请替换第一行）：

```powershell
$zip = ".\DingLater-2.1.2-win-x64-portable.zip"
$expected = (Get-Content .\SHA256SUMS.txt).Trim().Split()[0].ToLower()
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
if ($actual -eq $expected) { "校验通过，可以解压运行" } else { "校验失败，请重新下载" }
```

## 工作原理

1. 自动发现 `%APPDATA%\DingTalk\*_v3` 中当前活动账号。
2. 从 `user_config` 和日志中的严格 `real_uid=` 字段在内存中推导数据库密钥。
3. 以 `FileAccess.Read` 和共享读写方式读取 `dingtalk.db`、`dingtalk.db-wal`。
4. 校验 WAL header、salt、checksum 和最后一个完整提交，在内存中构造只读 SQLite 快照。
5. 以 128 个 `tbmsg_000`–`tbmsg_127` 表的自增 `primaryKey` 作为增量进度。
6. DingLater 消息和 checkpoint 在同一个事务中提交，崩溃重放通过 HMAC 来源指纹去重。

第一次启用只记录当前 128 个分表的最大 `primaryKey`，不会导入已有历史。私聊默认全部捕获；群聊默认捕获 `@我` 和 `@所有人`，可在设置中改为全部群消息。

## 功能与边界

- 普通文字保存完整 `text` 字段，支持长文本、emoji、换行和同一分钟内多条相同消息。
- 富文本、引用和卡片从钉钉本地记录中提取可见文字字段。
- 图片、语音和文件只保存类型及本地记录已有的文件名占位，不读取或下载附件。
- `待处理 / 稍后提醒 / 已处理` 都是 DingLater 本地状态，不会写回钉钉。
- 使用 WinUI 3 的会话列表＋消息详情布局，支持搜索、分钟级稍后时间、托盘运行、清晰的待处理数量角标、全透明到完整显示的闪烁、短消息悬停摘要、批量处理、暂停捕获和当前用户登录启动。
- 界面字号提供小、标准、大、特大四档，默认标准；仍遵循 Windows 系统文字缩放和 High Contrast。
- 捕获和稍后到期提醒依赖 DingLater 在前台或托盘中运行；点击窗口右上角“×”只会隐藏界面并继续在托盘运行，只有右键托盘图标选择“退出 DingLater”才会真正结束进程。
- Windows 通知可按设置显示会话、发送者和正文预览，但操作系统可能截断长预览；点击通知只打开 DingLater 中保存的完整项目，不打开钉钉。
- 默认留存 7 天，可设置 1–365 天；正文、联系人、会话名和来源标识逐记录 AES-256-GCM 加密，主密钥由 DPAPI `CurrentUser` 保护。
- 没有 HTTP/WebSocket、机器人/OpenAPI、遥测或云同步。

当前只保证 Windows x64 上钉钉 `8.3.45.260720005` 的 V3 数据格式。版本变化但结构探测仍完全匹配时可以继续运行；加密、WAL 或 schema 任一检查失败都会停止捕获并显示诊断，不会切换到注入、进程内存读取或窗口自动化。

随 Windows 登录启动使用当前用户 `HKCU\...\Run` 中唯一的 `DingLater` 值；关闭设置会删除该值。卸载时退出应用并删除解压目录即可；本地数据位于 `%LOCALAPPDATA%\DingLater`，可先在应用设置中选择“删除全部本地消息”。

旧的自签名 MSIX 用户首次运行便携版时，DingLater 会从 `DingLater.Local_*\LocalState` 做一次只读 SQLite backup 迁移，成功前不会改动旧数据。之后可从 Windows“已安装的应用”卸载旧 MSIX。

## 本地开发

要求 Windows 10 `19041` 或更新版本；界面基于 WinUI 3，仓库通过 `global.json` 固定 .NET SDK `10.0.302`。便携发布同时自包含 .NET 与 Windows App Runtime，开发机只需按锁文件恢复 NuGet 依赖。

```powershell
.\scripts\Install-Dotnet.ps1
$env:DOTNET_CLI_HOME="$PWD\.tools\cli-home"
$env:NUGET_PACKAGES="$PWD\.tools\nuget-packages"
.\.tools\dotnet\dotnet.exe restore .\DingLater.slnx --locked-mode
.\.tools\dotnet\dotnet.exe test .\DingLater.slnx -c Release
.\scripts\Build-Portable.ps1 -Version 2.1.2
```

最新本地测试版始终位于 `BuildOutput\DingLater.exe`，也可以直接双击仓库根目录的 `启动最新版本.cmd`。每次执行构建脚本都会覆盖这个固定目录，不再创建按日期变化的发布目录；ZIP 和 `SHA256SUMS.txt` 也放在同一目录。只有明确设置 `DINGLATER_DEMO=1` 或传入 `--demo` 的 Debug 运行才使用合成消息；普通 Debug/Release 都使用真实只读 V3 来源，Release 会忽略这两个测试入口。

更多说明：

- [隐私与安全](docs/PRIVACY.md)
- [兼容性与验收](docs/COMPATIBILITY.md)
- [测试](docs/TESTING.md)
- [便携发布](docs/DISTRIBUTION.md)

参考实现与格式研究：[dingwave-V3](https://github.com/E2ern1ty/dingwave-V3)。DingLater 的 .NET 实现独立编写，不复制其应用代码。
