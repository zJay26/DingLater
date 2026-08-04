# DingLater 隐私与安全说明

## 数据来源

DingLater 只读访问当前 Windows 用户 `%APPDATA%\DingTalk` 下经过格式验证的 V3 数据：

- `user_config`：只取 salt；
- `log\gaea.log*`：逐行匹配严格的 `real_uid=数字@dingding`，不保留日志行；
- `DBFiles\dingtalk.db` 和 `dingtalk.db-wal`：使用 `FileAccess.Read` 与 `FileShare.ReadWrite | FileShare.Delete`；
- `tbconversation`、`tbuser_profile_v2` 和 128 个 `tbmsg_*` 表：只在内存 SQLite 中执行固定只读查询。

应用不会写入、删除、重命名钉钉文件，不访问附件内容，不连接钉钉网络，不调用机器人/OpenAPI，不附加、注入、调试钉钉进程，也不发送鼠标键盘或窗口激活指令。

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
