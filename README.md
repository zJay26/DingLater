# DingLater

DingLater 是一个 Windows 本地“稍后回复箱”。它以只读方式解析当前用户的钉钉 V3 本地数据库，把启用之后的新消息保存到独立的本地收件箱；读取过程不会打开钉钉会话、操作窗口或向钉钉服务器发送请求。

> DingLater 的代码路径不会写入钉钉文件，也没有发送已读回执的网络或界面操作。发送方是否始终保持“未读”仍需用两个账号做最终验收，项目不会把静态保证描述成端到端保证。

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
- `收件箱 / 稍后 / 已处理` 都是 DingLater 本地状态，不会写回钉钉。
- 使用 WinUI 3 的会话列表＋消息详情布局，支持搜索、分钟级稍后时间、托盘运行、暂停捕获和当前用户登录启动。
- 界面字号提供小、标准、大、特大四档，默认标准；仍遵循 Windows 系统文字缩放和 High Contrast。
- 捕获和稍后到期提醒依赖 DingLater 在前台或托盘中运行；退出程序后不会在后台另留服务。
- Windows 通知可按设置显示会话、发送者和正文预览，但操作系统可能截断长预览；点击通知只打开 DingLater 中保存的完整项目，不打开钉钉。
- 默认留存 7 天，可设置 1–365 天；正文、联系人、会话名和来源标识逐记录 AES-256-GCM 加密，主密钥由 DPAPI `CurrentUser` 保护。
- 没有 HTTP/WebSocket、机器人/OpenAPI、遥测或云同步。

当前只保证 Windows x64 上钉钉 `8.3.45.260720005` 的 V3 数据格式。版本变化但结构探测仍完全匹配时可以继续运行；加密、WAL 或 schema 任一检查失败都会停止捕获并显示诊断，不会切换到注入、进程内存读取或窗口自动化。

## 下载与运行

### 普通用户

1. 打开 [GitHub Releases](https://github.com/zJay26/DingLater/releases)，进入最新版本。
2. 下载 `DingLater-<版本号>-win-x64-portable.zip`，例如 `DingLater-2.0.0-win-x64-portable.zip`。
3. 将完整 ZIP 解压到一个固定目录，然后双击其中的 `DingLater.exe`。
4. 首次启动时阅读并同意只读解析本机钉钉数据库；DingLater 会从同意时建立新消息起点，不会导入已有历史。

便携版无需安装、无需管理员权限、无需导入证书或核对证书指纹。未签名程序仍可能触发 Windows SmartScreen；这是发布者信誉提示，只有受信任代码签名才能消除。下载后的 `SHA256SUMS.txt` 可用于自愿校验文件完整性。

`SHA256SUMS.txt` 用于自愿校验下载完整性，不是运行前置步骤。首次运行会明确询问是否允许只读解析本机钉钉数据库，并从同意时建立新消息起点。

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
.\scripts\Build-Portable.ps1 -Version 2.0.0
```

发布物位于 `artifacts\release`。只有明确设置 `DINGLATER_DEMO=1` 或传入 `--demo` 的 Debug 运行才使用合成消息；普通 Debug/Release 都使用真实只读 V3 来源，Release 会忽略这两个测试入口。

更多说明：

- [隐私与安全](docs/PRIVACY.md)
- [兼容性与验收](docs/COMPATIBILITY.md)
- [测试](docs/TESTING.md)
- [便携发布](docs/DISTRIBUTION.md)

参考实现与格式研究：[dingwave-V3](https://github.com/E2ern1ty/dingwave-V3)。DingLater 的 .NET 实现独立编写，不复制其应用代码。
