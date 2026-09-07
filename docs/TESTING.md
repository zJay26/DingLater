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
- 待处理/稍后提醒/已处理、留存边界和设置迁移；
- schema 2→3 原位迁移、Direct/Group/Unknown 标题规则和纯数字旧会话名；
- 会话分组、排序、搜索、选择保持、提醒激活定位、分钟级稍后格式，以及托盘数量角标/隐私摘要文本；
- 四档字号序列化、非法设置归一化与即时保存失败回滚；
- 架构守卫禁止钉钉文件写入、进程内存 API、界面操作和网络客户端，并固定“窗口 × 隐藏到托盘、托盘退出才结束进程”的生命周期边界。
- 便携构建校验 ZIP 完整解压后的全部发布文件、PRI/XBF、真实窗口句柄和退出码；窗口显示真实 Shell、消息模板、托盘及隔离的合成数据。关闭到托盘的代码路径另由架构守卫覆盖，不将其描述为 GUI 自动化验收。

v2.3.0 新增回归：

- 留存期调整、过期删除、失效提醒释放与设置写入同一事务；注入 SQLite 写入失败验证整体回滚。
- 拒绝未来版本的数据库 schema，保留原版本号和数据。
- 捕获恢复失败时恢复暂停设置；被取消的批次向来源传播取消，防止把未提交的数据视为已接收。
- 会话对象复用、合并 40 次并发刷新、退出期间忽略迟到刷新，以及带搜索条件的提醒定位。
- 按提醒时间排序、严格早于清理时间的稍后边界、系统时钟前后跳变、过期提醒抑制、取消、替换和重复释放。
- 便携包逐文件校验、缺失运行时和同尺寸损坏资源的拒绝检查。

GitHub Actions 的 `Windows` 工作流在 Windows runner 上执行 locked restore、格式检查、Release 测试和 PowerShell 脚本语法检查，并保留 TRX。真实 GUI 启动和 ZIP 冒烟在发布机运行，不依赖 CI 图形桌面。

本地交互验收可运行 `BuildOutput\DingLater.exe --package-smoke-test --interactive-smoke-test`。该模式使用独立的临时合成数据、空捕获来源，并在 10 分钟后自动退出；普通 `--package-smoke-test` 在 2 秒后退出。它不读取或修改真实用户消息，可用于检查搜索、选择和不同字号的布局。

本机真实数据库探针默认跳过，明确启用后只输出测试结果，不输出路径、账号、联系人或正文：

```powershell
$env:DINGLATER_REAL_DB_TEST='1'
.\.tools\dotnet\dotnet.exe test .\tests\DingLater.Tests\DingLater.Tests.csproj `
  -c Release --filter 'FullyQualifiedName~LiveV3Snapshot'
```

该探针验证账号匹配、真实 DB/WAL、一致性、128 表 schema、真实增量消息解析，以及捕获前后本机未读总数不变。双账号发送方回执、SmartScreen 信誉以及独立 Windows 用户的 DPAPI 隔离仍是环境/人工验收。
