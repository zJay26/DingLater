# 第三方组件许可与声明

DingLater 自身代码采用 [MIT 许可证](LICENSE)。Windows 自包含便携包中的第三方组件各自适用其许可。使用或再分发包内的 Microsoft 组件须同意随包列出的 Microsoft 许可条款。

构建时从实际还原的 NuGet 包复制原始许可和声明，保存在 `licenses/<包名>/<版本>/`。其中包括 Windows App SDK 和 WinUI、Windows ML、WebView2、.NET 运行时、CommunityToolkit、SQLite 和其余随包提供的库。

部分 NuGet 包只声明了许可证标识。以下文件随便携包提供对应的上游许可及版权声明。

| 组件 | 许可及声明 |
| --- | --- |
| `H.NotifyIcon`、`H.NotifyIcon.WinUI`、`H.GeneratedIcons.System.Drawing` | [H.NotifyIcon 的 MIT 许可证](licenses/H.NotifyIcon-LICENSE.txt) |
| `Microsoft.Data.Sqlite`、`Microsoft.Data.Sqlite.Core`、`Microsoft.Win32.SystemEvents`、`System.Security.Cryptography.ProtectedData` | [.NET Foundation and Contributors 的 MIT 许可证](licenses/dotnet-LICENSE.txt) |
| `SQLitePCLRaw` 组件 | [Apache 2.0 许可证](licenses/SQLitePCLRaw-LICENSE.txt)及 [上游 NOTICE](licenses/SQLitePCLRaw-NOTICE.txt) |
| `Microsoft.Windows.SDK.NET.Ref` | [NuGet 包指向的 Windows SDK 软件许可条款](https://aka.ms/WinSDKLicenseURL) |

Windows 版 .NET 的 `coreclr.dll` 和 `Microsoft.DiaSymReader.Native.amd64.dll` 适用 [.NET Library License Terms](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm)。其他 .NET 组件的许可见 [Microsoft 的 Windows 版 .NET 许可说明](https://github.com/dotnet/core/blob/main/license-information-windows.md)和随包的相关许可文件。
