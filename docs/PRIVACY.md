# DingLater 隐私与安全说明

## 数据来源

DingLater 只读访问当前 Windows 用户 `%APPDATA%\DingTalk` 下经过格式验证的 V3 数据：

- `user_config`：只取 salt；
- `log\gaea.log*`：逐行匹配严格的 `real_uid=数字@dingding`，不保留日志行；
- `DBFiles\dingtalk.db` 和 `dingtalk.db-wal`：使用 `FileAccess.Read` 与 `FileShare.ReadWrite | FileShare.Delete`；
- `tbconversation`、`tbuser_profile_v2` 和 128 个 `tbmsg_*` 表：只在内存 SQLite 中执行固定只读查询。

应用不会写入、删除、重命名钉钉文件，不访问附件内容，不连接钉钉网络，不调用机器人/OpenAPI，不附加、注入、调试钉钉进程，也不发送鼠标键盘或窗口激活指令。

## 可选窗口置顶

“窗口置顶”使用同一 DingLater 进程注册 `Ctrl + Win + T`。只有用户按下快捷键时才对当前应用窗口切换置顶；关闭功能或正常退出时可恢复由本次运行设置的置顶。描边通过进程外 Windows 窗口事件跟随位置和可见性，不注入目标进程、不模拟输入、不激活窗口，不读取或保存目标窗口标题、内容或截图。该模块与钉钉数据捕获分离，不参与消息读取，也不会主动打开钉钉会话。

描边和窗口归属记录仅保存在内存中。被强制结束或崩溃时 Windows 可能保留目标窗口的置顶状态；重新运行后选择该窗口并按快捷键即可取消，或关闭目标窗口。

## 内存快照

V3 密钥通过 PBKDF2-HMAC-SHA1 + MD5 在内存中派生。DB 页面和 WAL payload 使用 AES-128-ECB 解密；WAL header、salt、checksum、完整 frame 和最后提交大小全部验证后才构造快照。解密数据库传给 `sqlite3_deserialize(..., SQLITE_DESERIALIZE_READONLY)`，查询完成后清零托管与非托管缓冲区。

DingLater 不主动把明文快照、聊天截图或消息诊断写入磁盘。和所有处理明文的桌面程序一样，消息在显示和加密前必然短暂存在于进程内存；操作系统 pagefile/崩溃转储属于 Windows 的系统级行为，不能被描述为绝对不存在。

## DingLater 本地数据

- 会话名、发送者、正文和来源标识分别使用随机 96 位 nonce 的 AES-256-GCM 加密。
- 去重键使用主密钥派生的 HMAC-SHA256；不会保存正文或会话名的普通哈希。
- 32 字节主密钥由 DPAPI `CurrentUser` 保护。
- checkpoint 仅保存来源类别、随机化账号指纹、0–127 分表编号和自增位置，不保存 UID、salt、联系人或正文。
- 明文元数据仅包括 UUID、时间、状态、来源/消息类别、置信度、兼容版本和应用设置。

默认留存 7 天，可配置 1–365 天。所有状态都从 DingLater 捕获时间计算硬到期日；缩短期限前显示删除数量并取消相关提醒。

## 诊断

应用不创建持久运行日志或上传崩溃报告。复制的诊断只包含版本、适配阶段布尔值、错误码、计数和时间，不包含文件路径、账号 ID、联系人、会话名、正文、密钥或 salt。

## 可选更新联网

从 v2.4.0 起，应用运行时默认每 6 小时尝试检查本项目 GitHub 最新正式 Release。首次检查在初始化完成后进行；检查时间保存在本地，重启不会重置间隔。关闭“设置 → 软件更新 → 自动检查更新”会停止后续定时请求并取消尚未完成的自动检查，仍可点击“立即检查”。

唯一网络入口 `GitHubUpdateClient` 不接收消息库或账号数据，只发送 HTTPS GET 请求和固定的产品 User-Agent。GitHub 及其下载 CDN 会收到正常连接所需的 IP 地址与请求元数据；不会上传联系人、消息、钉钉账号、密钥或诊断。允许的主机仅为 `api.github.com`、`github.com`、`release-assets.githubusercontent.com` 和 `objects.githubusercontent.com`。查看版本说明由用户点击链接后通过浏览器打开。

发现新版仅显示可关闭的应用内提示。下载必须由用户点击，应用依据 GitHub Release asset 的 SHA-256 摘要校验 ZIP，再验证解压文件清单。下载与解压内容存放在所选目录，错误诊断仅存放于本次更新请求旁的 `.error` 文件，不上传。SHA-256 保证文件与 GitHub 发布信息一致，不代替代码签名。

重启更新需要再次确认，更新助手只在原进程自然退出后替换程序文件，并在 `.dinglater-backup` 保留旧文件；消息库和密钥仍在原本的本地数据目录中。下载目录、程序目录和备份目录均应为普通可写文件夹，更新不申请管理员权限。备份及已完成的下载包不会自动删除，可在退出应用后自行整理。
