# DingLater 测试说明

运行默认测试：

```powershell
$env:DOTNET_CLI_HOME="$PWD\.tools\cli-home"
$env:NUGET_PACKAGES="$PWD\.tools\nuget-packages"
.\.tools\dotnet\dotnet.exe test .\DingLater.slnx -c Release --nologo
```

覆盖内容：

- V3 PBKDF2/MD5 固定向量、AES 页面解密；
- 长文字、emoji、富文本、卡片和附件占位解析；
- 群聊“仅提到我”模式同时识别个人 `@` 与 `@所有人`，并排除普通群消息；
- 稳定来源 ID 保留连续相同消息，重放仍幂等；
- 消息与 128 分表 checkpoint 原子提交；
- AES-GCM、HMAC、明文标记泄漏扫描和损坏数据库隔离；
- 收件箱/稍后/已处理、留存边界和设置迁移；
- schema 2→3 原位迁移、Direct/Group/Unknown 标题规则和纯数字旧会话名；
- 会话分组、排序、搜索、选择保持、提醒激活定位和分钟级稍后格式；
- 四档字号序列化、非法设置归一化与即时保存失败回滚；
- 架构守卫禁止钉钉文件写入、进程内存 API、界面操作和网络客户端。
- 便携构建校验应用 PRI/XBF 完整性，并从 publish 目录启动 EXE 验证真实窗口句柄。

本机真实数据库探针默认跳过，明确启用后只输出测试结果，不输出路径、账号、联系人或正文：

```powershell
$env:DINGLATER_REAL_DB_TEST='1'
.\.tools\dotnet\dotnet.exe test .\tests\DingLater.Tests\DingLater.Tests.csproj `
  -c Release --filter 'FullyQualifiedName~LiveV3Snapshot'
```

该探针验证账号匹配、真实 DB/WAL、一致性、128 表 schema、真实增量消息解析，以及捕获前后本机未读总数不变。双账号发送方回执、SmartScreen 信誉以及独立 Windows 用户的 DPAPI 隔离仍是环境/人工验收。
