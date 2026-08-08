# DingLater Android 0.1.0

Android 版是独立的本地通知客户端，不依赖 Windows、Root、Accessibility、网络或钉钉私有文件，也不与 Windows 版同步数据。最低系统为 Android 10（API 29）。

## 安装与首次授权

1. 在仓库根目录运行 `android\Build-Debug.ps1`，或直接使用交付的 `BuildOutput\DingLater-android-0.1.0-debug.apk`。
2. 用数据线连接手机并开启 USB 调试，然后运行：

   ```powershell
   adb install -r .\BuildOutput\DingLater-android-0.1.0-debug.apk
   ```

3. 打开 DingLater，阅读本地处理、通知可见范围和截断风险说明。
4. 点击“开启通知使用权”，只为 DingLater 开启通知访问。
5. 返回 DingLater，点击“开始记录新通知”。启用时间会立即写入本地；已有通知不会导入。
6. Android 13 及以上允许 DingLater 发送提醒通知。拒绝不会影响采集和状态到期，只会阻止系统提醒出现。

Debug APK 使用 Android 默认 Debug keystore。若 APK 来自另一台开发机，覆盖安装可能因签名不同而失败；要保留旧数据必须使用同一签名，否则先卸载旧版（卸载会删除本地数据）。

## 工作方式与权限边界

数据流为：

`钉钉系统通知 → 包名过滤 → 可见字段映射 → 质量标记/HMAC 去重 → AES-GCM 加密 → Room → 本地界面`

- 只接受包名 `com.alibaba.android.rimet`。其他应用通知在读取正文前即被过滤。
- 字段优先级为 `MessagingStyle messages`、展开正文、文本行、可见单行文本。
- 钉钉的连续通知更新会进入单一串行队列，避免首次生成 Keystore 密钥时发生并发竞争；单条通知解析失败只写入无明文诊断，不会停止后续捕获。
- 文本行和可见单行文本会标记“内容可能不完整”；空正文、group summary 和不可识别通知不会保存。
- 不调用原通知的 `contentIntent`、`RemoteInput`、取消通知、延后通知或已显示接口，不打开钉钉。
- 应用 Manifest 只有提醒通知和开机恢复所需权限，不含 `INTERNET`、Accessibility、存储读取或精确闹钟权限。
- 会话名、发送者、正文和来源标识使用 Android Keystore AES-256-GCM 加密，每个字段每次使用随机 nonce；去重使用独立 Keystore HMAC-SHA256 密钥。
- Android 备份和设备迁移均关闭。数据库、正文、密钥与诊断不由应用上传或导出。

## 使用

底部导航包含“待处理 / 稍后提醒 / 已处理 / 设置”。消息按会话归组，可搜索、查看详情、稍后提醒、标为已处理、恢复或删除；当前分类支持批量标为已处理或批量删除。

默认保留 7 天，可设置 1–365 天。普通稍后提醒使用 `AlarmManager.setAndAllowWhileIdle`：不会提前，但省电模式可能延迟；开机和应用更新后会恢复未来计划。点击 DingLater 提醒只打开对应的本地消息。

设置页可暂停捕获、修改保留期限/快速稍后时长、控制提醒正文预览、检查权限和钉钉安装状态，并查看不含明文的诊断计数。普通单条失败会显示次数、异常类型和时间，后续通知仍会继续处理；只有密钥失效或密文无法读取时，应用才停止采集并要求显式重置，不会静默删除或改存明文。

## 本地开发

工程固定使用 AGP 9.0.1、Gradle 9.1、JDK 17 字节码、compile/target SDK 36、Kotlin/Compose 和 Material 3。开发机需安装 Android SDK Platform 36，并在未提交的 `android/local.properties` 中配置 `sdk.dir`。

```powershell
cd .\android
.\gradlew.bat testDebugUnitTest
.\gradlew.bat lintDebug
.\gradlew.bat connectedDebugAndroidTest  # 需要已启动的模拟器或真机
.\gradlew.bat assembleDebug
```

计划中的 Lifecycle 2.11.0 已被实际 AAR 元数据证实要求 compileSdk 37 和 AGP 9.1，与既定的 SDK 36/AGP 9.0.1 冲突；工程因此固定为 Lifecycle 2.10.0，其他计划指定的核心库版本保持不变。

## 无明文 ADB 诊断

以下命令只查看安装、权限、监听服务和闹钟状态，不读取消息正文：

```powershell
adb shell dumpsys package io.github.zjay26.dinglater | Select-String "versionName|versionCode|POST_NOTIFICATIONS"
adb shell settings get secure enabled_notification_listeners
adb shell cmd appops get io.github.zjay26.dinglater POST_NOTIFICATION
adb shell dumpsys alarm | Select-String "io.github.zjay26.dinglater"
```

需要反馈采集情况时，优先截图应用设置页的“捕获诊断”。它只包含字段长度、类型、计数、时间和 HMAC 前缀。不要提交完整系统 `logcat`，其中可能包含其他应用输出。
