using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using H.NotifyIcon;
using Wpf.Ui.Appearance;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor;

public partial class App : System.Windows.Application
{
    private TaskbarIcon?        _trayIcon;
    private LayoutCoordinator?  _coordinator;
    private MonitorService?     _monitorService;
    private WorkspaceService?   _workspaceService;
    private StorageService?     _storageService;
    private SettingsService?    _settingsService;
    private HotkeyService?     _hotkeyService;
    private CommandServer?     _commandServer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── External command mode ─────────────────────────────────────────
        // Invocations like `WindowAnchor.exe --align "MAIN"` are not meant to start a second
        // instance: they hand the command to the instance already running in the tray and exit.
        // This is what external launchers (Raycast, Stream Deck, shortcuts) call.
        if (TryRunAsClient(e.Args))
        {
            Shutdown();
            return;
        }

        // ── Single instance ───────────────────────────────────────────────
        // WindowAnchor owns global hotkeys, a tray icon and the command pipe, none of which
        // tolerate duplicates. Without this guard a stray launch (a shortcut, an unrecognised
        // argument, autostart racing a manual start) silently adds another tray icon.
        if (!AcquireSingleInstanceLock())
        {
            AppLogger.Info("Another instance is already running — exiting.");
            Shutdown();
            return;
        }

        bool minimized = e.Args.Length > 0 &&
            e.Args[0].Equals("--minimized", StringComparison.OrdinalIgnoreCase);

        // Global exception handlers — prevent ghost tray icons
        DispatcherUnhandledException        += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;

        AppLogger.Info("WindowAnchor starting");

        // Apply system theme (Mica/dark/light) before any window opens
        ApplicationThemeManager.ApplySystemTheme();

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        _trayIcon.ForceCreate();

        var storageService    = new StorageService();
        _storageService       = storageService;
        _monitorService       = new MonitorService();
        // Settings must exist before WindowService: it reads the dedicated-browser URL patterns
        // to decide whether address bars are queried during a snapshot.
        _settingsService      = new SettingsService();
        var windowService     = new WindowService(_settingsService);
        var jumpListService   = new JumpListService();
        var webAppService     = new WebAppService();
        var workspaceService  = new WorkspaceService(storageService, windowService, _monitorService, jumpListService, webAppService);

        _workspaceService = workspaceService;
        _coordinator      = new LayoutCoordinator(_monitorService, windowService, workspaceService);

        // Accept commands from external launchers (see CommandServer and TryRunAsClient).
        _commandServer = new CommandServer(workspaceService, _coordinator);
        _commandServer.Start();

        // Hotkeys (settings were created above, before WindowService)
        _hotkeyService   = new HotkeyService();
        _hotkeyService.Initialise();
        ApplyHotkeySettings();

        string initialFingerprint = _monitorService.GetCurrentMonitorFingerprint();
        AppLogger.Info($"Initial monitor fingerprint: {initialFingerprint}");
        if (minimized) AppLogger.Info("Started with --minimized — staying in tray.");

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // ── Startup workspace restore (deferred so the tray icon settles) ──
        var startupBehavior = _settingsService.Settings.StartupBehavior;
        if (startupBehavior != StartupBehavior.None)
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(2000);
                await HandleStartupRestoreAsync(startupBehavior);
            }, DispatcherPriority.Background);
        }
    }

    // ── Startup workspace restore ─────────────────────────────────────────

    private async Task HandleStartupRestoreAsync(StartupBehavior behavior)
    {
        try
        {
            var workspaces = _workspaceService!.GetAllWorkspaces();
            if (workspaces.Count == 0) return;

            WorkspaceSnapshot? target = null;

            switch (behavior)
            {
                case StartupBehavior.RestoreDefault:
                    string? defaultName = _settingsService!.Settings.DefaultWorkspaceName;
                    if (!string.IsNullOrEmpty(defaultName))
                        target = workspaces.FirstOrDefault(w =>
                            w.Name.Equals(defaultName, StringComparison.OrdinalIgnoreCase));
                    break;

                case StartupBehavior.RestoreLastUsed:
                    target = workspaces.OrderByDescending(w => w.SavedAt).FirstOrDefault();
                    break;

                case StartupBehavior.AskUser:
                    var dialog = new UI.StartupWorkspaceDialog(workspaces);
                    if (dialog.ShowDialog() == true)
                        target = dialog.SelectedWorkspace;
                    break;
            }

            if (target != null)
            {
                AppLogger.Info($"Startup restore: restoring '{target.Name}'");
                await _coordinator!.RestoreWorkspaceAsync(target);
                ShowBalloon("Workspace Restored", $"\u201c{target.Name}\u201d restored on startup");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("HandleStartupRestoreAsync failed", ex);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        string fingerprint = _monitorService?.GetCurrentMonitorFingerprint() ?? "unknown";
        AppLogger.Info($"DisplaySettingsChanged — new fingerprint: {fingerprint}");
        _coordinator?.HandleDisplayChangeAsync();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        var settings = new UI.SettingsWindow(_workspaceService!, _storageService!, _coordinator!, _settingsService!, _monitorService!);
        settings.Show();
    }

    private async void ShowSaveWorkspaceDialog()
    {
        // Build per-monitor window lists for the selective-save dialog
        List<(MonitorInfo Monitor, List<WindowRecord> Windows)> windowPreview;
        try
        {
            windowPreview = await Task.Run(() => _workspaceService!.GetWindowPreviewForDialog());
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"ShowSaveWorkspaceDialog: could not enumerate windows: {ex.Message}");
            windowPreview = new();
        }

        var dialog = new UI.SaveWorkspaceDialog(windowPreview, _settingsService);
        if (dialog.ShowDialog() != true) return;

        // Read all dialog properties on the UI thread before Task.Run.
        var name            = dialog.WorkspaceName;
        var saveFiles       = dialog.SaveFiles;
        var selectedWindows = dialog.SelectedWindows;

        // Show progress window when file detection is enabled (can take several seconds).
        UI.SaveProgressWindow? progressWindow = null;
        if (saveFiles)
        {
            progressWindow = new UI.SaveProgressWindow(name);
            progressWindow.Show();
        }

        var progress = progressWindow != null
            ? new Progress<Services.SaveProgressReport>(r => progressWindow.ApplyReport(r))
            : (IProgress<Services.SaveProgressReport>?)null;

        try
        {
            await Task.Run(
                () => _workspaceService!.TakeSnapshot(name, saveFiles: saveFiles,
                    selectedWindows: selectedWindows, progress: progress));
            AppLogger.Info($"Workspace '{name}' saved (files={saveFiles})");
            ShowBalloon("Workspace Saved",
                $"\u201c{name}\u201d saved \u2014 {selectedWindows.Count} window(s)");
        }
        catch (Exception ex)
        {
            AppLogger.Error("ShowSaveWorkspaceDialog: TakeSnapshot failed", ex);
            System.Windows.MessageBox.Show($"Failed to save workspace: {ex.Message}", "WindowAnchor",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            progressWindow?.Close();
        }
    }

    private void OnTrayMenuOpened(object sender, RoutedEventArgs e)
    {
        PopulateWorkspacesMenu();
    }

    private void PopulateWorkspacesMenu()
    {
        var trayMenu = _trayIcon?.ContextMenu;
        if (trayMenu == null) return;

        System.Windows.Controls.MenuItem? workspacesItem = null;
        foreach (var item in trayMenu.Items)
        {
            if (item is System.Windows.Controls.MenuItem mi && mi.Name == "WorkspacesMenu")
            {
                workspacesItem = mi;
                break;
            }
        }
        if (workspacesItem is null) return;

        workspacesItem.Items.Clear();

        var workspaces = GetOrderedWorkspaces();

        if (workspaces.Count == 0)
        {
            workspacesItem.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "(no saved workspaces)", IsEnabled = false
            });
        }
        else
        {
            foreach (var ws in workspaces)
            {
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = $"Restore: {ws.Name}"
                };
                var captured = ws;
                item.Click += (_, _) => OnRestoreWorkspaceClick(captured);
                workspacesItem.Items.Add(item);
            }

            workspacesItem.Items.Add(new System.Windows.Controls.Separator());

            foreach (var ws in workspaces)
            {
                var switchItem = new System.Windows.Controls.MenuItem
                {
                    Header = $"Switch to: {ws.Name}"
                };
                var captured = ws;
                switchItem.Click += (_, _) => OnSwitchWorkspaceClick(captured);
                workspacesItem.Items.Add(switchItem);
            }

            workspacesItem.Items.Add(new System.Windows.Controls.Separator());

            foreach (var ws in workspaces)
            {
                var alignItem = new System.Windows.Controls.MenuItem
                {
                    Header = $"Align + minimize others: {ws.Name}"
                };
                var captured = ws;
                alignItem.Click += (_, _) => OnAlignWorkspaceClick(captured);
                workspacesItem.Items.Add(alignItem);
            }
        }

        // Always append Save + Manage at the bottom
        workspacesItem.Items.Add(new System.Windows.Controls.Separator());
        var saveItem = new System.Windows.Controls.MenuItem { Header = "Save Current Workspace..." };
        saveItem.Click += (_, _) => ShowSaveWorkspaceDialog();
        workspacesItem.Items.Add(saveItem);

        var manageItem = new System.Windows.Controls.MenuItem { Header = "Manage Workspaces" };
        manageItem.Click += (_, _) => OnOpenSettingsClick(manageItem, new RoutedEventArgs());
        workspacesItem.Items.Add(manageItem);
    }

    private void OnRestoreWorkspaceClick(WindowAnchor.Models.WorkspaceSnapshot snapshot)
    {
        _coordinator?.RestoreWorkspaceAsync(snapshot);
    }

    private void OnSwitchWorkspaceClick(WindowAnchor.Models.WorkspaceSnapshot snapshot)
    {
        _coordinator?.SwitchWorkspaceAsync(snapshot);
    }

    private void OnAlignWorkspaceClick(WindowAnchor.Models.WorkspaceSnapshot snapshot)
    {
        _coordinator?.AlignAndMinimizeOthersAsync(snapshot);
    }

    // ── Single-instance guard ────────────────────────────────────────────────

    private static System.Threading.Mutex? _instanceMutex;

    /// <summary>
    /// Takes a per-user named mutex. Returns <c>false</c> when another instance already holds it,
    /// in which case the caller must exit. The mutex is released automatically when the process
    /// ends, including on a crash.
    /// </summary>
    private static bool AcquireSingleInstanceLock()
    {
        try
        {
            _instanceMutex = new System.Threading.Mutex(
                initiallyOwned: true,
                $"Local\\WindowAnchor.SingleInstance.{Environment.UserName}",
                out bool createdNew);

            return createdNew;
        }
        catch (Exception ex)
        {
            // Never let the guard itself stop the app from starting.
            AppLogger.Warn($"Single-instance check failed, continuing: {ex.Message}");
            return true;
        }
    }

    // ── External command client ──────────────────────────────────────────────

    /// <summary>Verbs accepted on the command line and forwarded to the running instance.</summary>
    private static readonly string[] ClientVerbs = { "--restore", "--align", "--switch", "--save", "--list", "--ping" };

    /// <summary>
    /// When <paramref name="args"/> carries one of <see cref="ClientVerbs"/>, sends it to the
    /// running instance over the command pipe and returns <c>true</c> so startup aborts.
    /// Returns <c>false</c> for a normal launch.
    /// <para>
    /// The result is written to stdout, which a parent process that redirects the handle (a
    /// Raycast extension, PowerShell) can read; the exit code is 0 on success and 1 on failure.
    /// </para>
    /// </summary>
    private static bool TryRunAsClient(string[] args)
    {
        if (args.Length == 0) return false;

        string verb = args[0].ToLowerInvariant();

        // Anything switch-like other than --minimized is meant as a command. Unknown verbs are
        // reported as an error instead of falling through to a normal launch, which would start
        // a second tray instance and (with a startup workspace configured) restore a layout the
        // caller never asked for.
        bool looksLikeCommand = verb.StartsWith("--", StringComparison.Ordinal)
                             && !verb.Equals("--minimized", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeCommand) return false;

        EnsureConsoleOutput();

        if (!ClientVerbs.Contains(verb))
        {
            Console.Out.WriteLine($"ERR|Unknown command '{verb}'. Supported: {string.Join(", ", ClientVerbs)}");
            Console.Out.Flush();
            Environment.ExitCode = 1;
            return true;
        }

        // Pull recognised flags out before treating the remainder as the workspace name, so a
        // name containing spaces still survives when the caller did not quote it.
        var rest = args.Skip(1).ToList();
        bool noFiles = rest.RemoveAll(a => a.Equals("--no-files", StringComparison.OrdinalIgnoreCase)) > 0;

        string argument = string.Join(" ", rest);
        string pipeVerb = verb.TrimStart('-');
        if (pipeVerb == "save" && noFiles) pipeVerb = "savenofiles";

        string message = $"{pipeVerb}|{argument}";

        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".", Services.CommandServer.PipeName, System.IO.Pipes.PipeDirection.InOut);

            // Short timeout: either WindowAnchor is running and answers at once, or it is not.
            pipe.Connect(3000);

            using var writer = new System.IO.StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new System.IO.StreamReader(pipe, leaveOpen: true);

            writer.WriteLine(message);
            string reply = reader.ReadLine() ?? "ERR|No response";

            Console.Out.WriteLine(reply.StartsWith("OK|", StringComparison.Ordinal)
                ? reply[3..]
                : reply);
            Console.Out.Flush();

            Environment.ExitCode = reply.StartsWith("ERR", StringComparison.Ordinal) ? 1 : 0;
        }
        catch (TimeoutException)
        {
            Console.Out.WriteLine("ERR|WindowAnchor is not running");
            Console.Out.Flush();
            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine($"ERR|{ex.Message}");
            Console.Out.Flush();
            Environment.ExitCode = 1;
        }

        return true;
    }

    // ── Console plumbing for command mode ────────────────────────────────────
    // WindowAnchor is a WinExe, so it starts without a console and Console.Out goes nowhere.
    // For --ping/--list to return anything we must bind stdout explicitly:
    //   • launched from a terminal  → attach to the caller's console
    //   • stdout redirected to a pipe (a Raycast extension, `... | Out-String`)
    //     → the inherited handle is already usable and must NOT be replaced
    // Both cases end with Console.Out pointing at a real stream.

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetFileType(IntPtr hFile);

    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE     = -11;
    private const int FILE_TYPE_UNKNOWN     = 0;

    private static void EnsureConsoleOutput()
    {
        try
        {
            // A usable stdout handle means the caller redirected it — leave it alone,
            // because AttachConsole would re-point the standard handles at the console.
            IntPtr handle = GetStdHandle(STD_OUTPUT_HANDLE);
            bool redirected = handle != IntPtr.Zero
                           && handle != new IntPtr(-1)
                           && GetFileType(handle) != FILE_TYPE_UNKNOWN;

            if (!redirected)
                AttachConsole(ATTACH_PARENT_PROCESS);   // no console to attach to → harmless

            var stdout = Console.OpenStandardOutput();
            var writer = new System.IO.StreamWriter(stdout) { AutoFlush = true };
            Console.SetOut(writer);
        }
        catch
        {
            // Output is best effort: the command itself still runs and the exit code still
            // reports success or failure, which is what a launcher actually needs.
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("User requested exit.");
        _commandServer?.Dispose();
        _hotkeyService?.Dispose();
        _trayIcon?.Dispose();
        Current.Shutdown();
    }

    // ── Hotkey integration ────────────────────────────────────────────────

    /// <summary>
    /// (Re)registers or unregisters all global hotkeys based on the current
    /// settings.  Called from OnStartup and from SettingsWindow when the user
    /// toggles the switch or changes a shortcut.
    /// </summary>
    public void ApplyHotkeySettings()
    {
        if (_hotkeyService == null || _settingsService == null) return;

        _hotkeyService.UnregisterAll();

        if (!_settingsService.Settings.HotkeysEnabled) return;

        // Merge defaults with any user-customised shortcuts
        var shortcuts = HotkeyService.GetResolvedShortcuts(_settingsService.Settings);

        foreach (var shortcut in shortcuts)
        {
            Action? callback = shortcut.ActionId switch
            {
                "QuickSave"      => () => Dispatcher.Invoke(ShowSaveWorkspaceDialog),
                "RestoreDefault" => () => Dispatcher.Invoke(RestoreDefaultWorkspace),
                "RestoreSlot1"   => () => Dispatcher.Invoke(() => RestoreWorkspaceByIndex(0)),
                "RestoreSlot2"   => () => Dispatcher.Invoke(() => RestoreWorkspaceByIndex(1)),
                "RestoreSlot3"   => () => Dispatcher.Invoke(() => RestoreWorkspaceByIndex(2)),
                "SwitchSlot1"    => () => Dispatcher.Invoke(() => SwitchWorkspaceByIndex(0)),
                "SwitchSlot2"    => () => Dispatcher.Invoke(() => SwitchWorkspaceByIndex(1)),
                "SwitchSlot3"    => () => Dispatcher.Invoke(() => SwitchWorkspaceByIndex(2)),
                "OpenSettings"   => () => Dispatcher.Invoke(() => OnOpenSettingsClick(this, new RoutedEventArgs())),
                "SwitchDefault"  => () => Dispatcher.Invoke(SwitchDefaultWorkspace),
                _ => null,
            };

            if (callback != null)
                _hotkeyService.Register(shortcut.Modifiers, shortcut.Key, callback);
        }
    }

    private void RestoreDefaultWorkspace()
    {
        string? name = _settingsService?.Settings.DefaultWorkspaceName;
        if (string.IsNullOrEmpty(name)) return;

        var ws = _workspaceService?.GetAllWorkspaces()
            .FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (ws != null)
            _ = _coordinator!.RestoreWorkspaceAsync(ws);
    }

    private void SwitchDefaultWorkspace()
    {
        string? name = _settingsService?.Settings.DefaultWorkspaceName;
        if (string.IsNullOrEmpty(name)) return;

        var ws = _workspaceService?.GetAllWorkspaces()
            .FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (ws != null)
            _ = _coordinator!.SwitchWorkspaceAsync(ws);
    }

    private void RestoreWorkspaceByIndex(int index)
    {
        var workspaces = GetOrderedWorkspaces();
        if (index < workspaces.Count)
            _ = _coordinator!.RestoreWorkspaceAsync(workspaces[index]);
    }

    private void SwitchWorkspaceByIndex(int index)
    {
        var workspaces = GetOrderedWorkspaces();
        if (index < workspaces.Count)
            _ = _coordinator!.SwitchWorkspaceAsync(workspaces[index]);
    }

    /// <summary>
    /// Returns workspaces in the user's preferred display order (matching the
    /// Settings UI).  The first three entries map to Ctrl+Alt+1/2/3.
    /// </summary>
    private List<Models.WorkspaceSnapshot> GetOrderedWorkspaces()
    {
        var all   = _workspaceService?.GetAllWorkspaces() ?? new();
        var order = _settingsService?.Settings.WorkspaceOrder;
        if (order == null || order.Count == 0)
            return all.OrderByDescending(w => w.SavedAt).ToList();

        var result = new List<Models.WorkspaceSnapshot>();
        foreach (var name in order)
        {
            var ws = all.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (ws != null) result.Add(ws);
        }
        foreach (var ws in all.OrderByDescending(w => w.SavedAt))
        {
            if (!result.Any(r => r.Name.Equals(ws.Name, StringComparison.OrdinalIgnoreCase)))
                result.Add(ws);
        }
        return result;
    }

    // ── Balloon helper ────────────────────────────────────────────────────────

    public void ShowBalloon(string title, string message,
        H.NotifyIcon.Core.NotificationIcon icon = H.NotifyIcon.Core.NotificationIcon.Info)
    {
        try
        {
            _trayIcon?.ShowNotification(title, message, icon);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"ShowBalloon failed: {ex.Message}");
        }
    }

    // ── Global exception handlers ─────────────────────────────────────────────

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled dispatcher exception", e.Exception);
        _trayIcon?.Dispose();   // prevent ghost tray icon
        e.Handled = false;      // let Windows show the crash dialog
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            AppLogger.Error("Unhandled domain exception", ex);
        _trayIcon?.Dispose();   // prevent ghost tray icon
    }
}

