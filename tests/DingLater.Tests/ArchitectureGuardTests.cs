namespace DingLater.Tests;

[TestClass]
public sealed class ArchitectureGuardTests
{
    [TestMethod]
    public void RuntimeSource_RestrictsInteractionToTopmostUtility_AndHttpToUpdater()
    {
        var root = FindRepositoryRoot();
        var sourceFiles = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);
        var banned = new[]
        {
            "SendInput",
            "PostMessage",
            "SetForegroundWindow",
            "ShowWindow",
            "SetWindowPos",
            "SendMessage",
            "PostThreadMessage",
            "mouse_event",
            "keybd_event",
            "AttachThreadInput",
            "InvokePattern",
            "TogglePattern",
            "SelectionItemPattern",
            "RemoveNotification",
            "ClearNotifications",
            "HttpClient",
            "WebClient",
            "WebRequest",
            "ClientWebSocket",
            "TcpClient",
            "UdpClient",
            "OpenProcess",
            "ReadProcessMemory",
            "WriteProcessMemory",
            "VirtualAllocEx",
            "CreateRemoteThread",
            "DebugActiveProcess",
            "LoginAuth"
        };

        foreach (var file in sourceFiles)
        {
            var text = File.ReadAllText(file);
            foreach (var symbol in banned)
            {
                if (symbol == "HttpClient" && string.Equals(file, Path.Combine(root, "src", "DingLater.Core", "Updates", "GitHubUpdateClient.cs"), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (symbol == "SetWindowPos" && Path.GetDirectoryName(file) == Path.Combine(root, "src", "DingLater.App", "Services", "AlwaysOnTop"))
                {
                    continue;
                }

                Assert.IsFalse(text.Contains(symbol, StringComparison.Ordinal), $"Banned runtime symbol '{symbol}' appears in {Path.GetRelativePath(root, file)}");
            }
        }
    }

    [TestMethod]
    public void DingTalkDatabaseCapture_UsesOnlyReadSideFileHandles()
    {
        var root = FindRepositoryRoot();
        var captureRoot = Path.Combine(root, "src", "DingLater.Core", "Capture", "DingTalkDatabase");
        var source = string.Join(
            Environment.NewLine,
            Directory.GetFiles(captureRoot, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        var bannedWrites = new[]
        {
            "FileAccess.Write",
            "FileMode.Create",
            "FileMode.Append",
            "FileMode.OpenOrCreate",
            "File.Write",
            "File.Delete",
            "File.Move",
            "Directory.Delete",
            "Registry."
        };

        StringAssert.Contains(source, "FileAccess.Read");
        StringAssert.Contains(source, "FileShare.ReadWrite | FileShare.Delete");
        StringAssert.Contains(source, "sqlite3_deserialize");
        foreach (var symbol in bannedWrites)
        {
            Assert.IsFalse(source.Contains(symbol, StringComparison.Ordinal), $"Database capture contains write-side symbol '{symbol}'.");
        }
    }

    [TestMethod]
    public void ObsoleteNotificationAndPopupSources_AreRemoved()
    {
        var root = FindRepositoryRoot();
        Assert.IsFalse(File.Exists(Path.Combine(root, "src", "DingLater.App", "Capture", "WindowsNotificationSource.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "src", "DingLater.App", "Capture", "DingTalkPopupSource.cs")));
    }

    [TestMethod]
    public void AppLayer_ContainsNoWpfOrWindowsFormsDependencies()
    {
        var root = FindRepositoryRoot();
        var appRoot = Path.Combine(root, "src", "DingLater.App");
        var files = Directory.GetFiles(appRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".xaml")
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        var banned = new[]
        {
            "UseWPF",
            "UseWindowsForms",
            "System.Windows.",
            "System.Windows.Forms",
            "System.Windows.MessageBox"
        };

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var symbol in banned)
            {
                Assert.IsFalse(text.Contains(symbol, StringComparison.Ordinal), $"WPF/Windows Forms symbol '{symbol}' appears in {Path.GetRelativePath(root, file)}");
            }
        }
    }

    [TestMethod]
    public void TrayPopupMenu_UsesCommandsInsteadOfIgnoredClickHandlers()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DingLater.App",
            "Services",
            "TrayIconService.cs"));

        StringAssert.Contains(source, "Command = openCommand");
        StringAssert.Contains(source, "Command = pauseCommand");
        StringAssert.Contains(source, "Command = exitCommand");
        StringAssert.Contains(source, "CreateDeferredCommand");
        StringAssert.Contains(source, "LeftClickCommand = openCommand");
        Assert.IsFalse(source.Contains("DoubleClickCommand = openCommand", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("exitItem.Click", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowClose_HidesToTray_WhileTrayExitStopsApplication()
    {
        var root = FindRepositoryRoot();
        var windowSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DingLater.App",
            "Views",
            "MainWindow.xaml.cs"));
        var appSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DingLater.App",
            "App.xaml.cs"));

        StringAssert.Contains(windowSource, "args.Cancel = true");
        StringAssert.Contains(windowSource, "HideRequested?.Invoke");
        StringAssert.Contains(appSource, "_mainWindow.HideRequested += MainWindow_HideRequested");
        StringAssert.Contains(appSource, "private void MainWindow_HideRequested");
        StringAssert.Contains(appSource, "=> _mainWindow?.HideToTray();");
        StringAssert.Contains(appSource, "private void Tray_ExitRequested");
        StringAssert.Contains(appSource, "=> _ = ExitApplicationAsync();");
        Assert.IsFalse(appSource.Contains("MainWindow_CloseRequested", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DingLater.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
