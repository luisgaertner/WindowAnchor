using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Exposes workspace actions to other processes over a named pipe, so external launchers
/// (Raycast, Stream Deck, AutoHotkey, a shortcut, …) can drive WindowAnchor without
/// simulating its global hotkeys.
///
/// <para><b>Why a pipe and not just command-line arguments.</b> WindowAnchor lives in the tray
/// as a single long-running instance that owns the hotkey registrations and the tray icon.
/// Starting the executable again would create a second instance rather than act on the running
/// one. The second invocation therefore connects to this pipe, hands over the command, and
/// exits immediately — see <c>App.TryRunAsClient</c>.</para>
///
/// <para><b>Protocol.</b> One UTF-8 line per connection, <c>verb|argument</c>. The server replies
/// with a single line: <c>OK</c>, <c>OK|payload</c>, or <c>ERR|message</c>.</para>
/// </summary>
public sealed class CommandServer : IDisposable
{
    /// <summary>
    /// Pipe name. Named pipes are per-machine, so the current user name is appended to keep
    /// concurrent sessions (fast user switching, RDP) from talking to each other's instance.
    /// </summary>
    public static string PipeName => $"WindowAnchor.Command.{Environment.UserName}";

    private readonly WorkspaceService  _workspaceService;
    private readonly LayoutCoordinator _coordinator;
    private readonly CancellationTokenSource _cts = new();

    public CommandServer(WorkspaceService workspaceService, LayoutCoordinator coordinator)
    {
        _workspaceService = workspaceService;
        _coordinator      = coordinator;
    }

    /// <summary>Starts accepting connections in the background. Returns immediately.</summary>
    public void Start()
    {
        Task.Run(() => AcceptLoopAsync(_cts.Token));
        AppLogger.Info($"CommandServer listening on \\\\.\\pipe\\{PipeName}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // One server instance per connection: create, wait, serve, dispose, repeat.
                using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) continue;

                string response = await HandleCommandAsync(line.Trim()).ConfigureAwait(false);
                await writer.WriteLineAsync(response).ConfigureAwait(false);

                try { pipe.WaitForPipeDrain(); } catch { /* client already gone */ }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                AppLogger.Warn($"CommandServer: connection failed — {ex.Message}");
                // Brief pause so a persistent fault cannot spin the loop at full speed.
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { return; }
            }
        }
    }

    /// <summary>
    /// Parses and executes one command line. Never throws — failures come back as
    /// <c>ERR|message</c> so the caller can surface them.
    /// </summary>
    private async Task<string> HandleCommandAsync(string line)
    {
        try
        {
            string[] parts = line.Split('|', 2);
            string verb = parts[0].Trim().ToLowerInvariant();
            string arg  = parts.Length > 1 ? parts[1].Trim() : "";

            AppLogger.Info($"CommandServer: received '{verb}' arg='{arg}'");

            switch (verb)
            {
                case "ping":
                    return "OK|WindowAnchor";

                case "list":
                {
                    var names = _workspaceService.GetAllWorkspaces()
                        .OrderByDescending(w => w.SavedAt)
                        .Select(w => w.Name);
                    return "OK|" + string.Join("\t", names);
                }

                case "save":
                case "savenofiles":
                {
                    if (string.IsNullOrWhiteSpace(arg))
                        return "ERR|A workspace name is required";

                    bool saveFiles = verb == "save";

                    // Runs on the pipe's worker thread: window enumeration and file detection
                    // must not block the UI, and the caller waits for the real result rather
                    // than a fire-and-forget acknowledgement.
                    var snapshot = await Task.Run(
                        () => _workspaceService.TakeSnapshot(arg, saveFiles: saveFiles))
                        .ConfigureAwait(false);

                    Notify("Workspace Saved",
                        $"\u201c{snapshot.Name}\u201d \u2014 {snapshot.Entries.Count} window(s)");

                    return $"OK|Saved '{snapshot.Name}' ({snapshot.Entries.Count} windows)";
                }

                case "restore":
                case "align":
                case "switch":
                {
                    var ws = FindWorkspace(arg);
                    if (ws == null) return $"ERR|No workspace named '{arg}'";

                    // Marshal to the UI thread: these paths show tray balloons and touch WPF state.
                    await InvokeOnUiAsync(() => verb switch
                    {
                        "restore" => _coordinator.RestoreWorkspaceAsync(ws),
                        "align"   => _coordinator.AlignAndMinimizeOthersAsync(ws),
                        _         => _coordinator.SwitchWorkspaceAsync(ws),
                    }).ConfigureAwait(false);

                    return $"OK|{verb} '{ws.Name}'";
                }

                default:
                    return $"ERR|Unknown command '{verb}'";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"CommandServer: command failed — {ex.Message}");
            return $"ERR|{ex.Message}";
        }
    }

    /// <summary>
    /// Resolves a workspace by name, case-insensitively. An empty argument selects the
    /// configured default workspace, falling back to the most recently saved one.
    /// </summary>
    private WorkspaceSnapshot? FindWorkspace(string name)
    {
        var all = _workspaceService.GetAllWorkspaces();

        if (!string.IsNullOrWhiteSpace(name))
            return all.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        return all.OrderByDescending(w => w.SavedAt).FirstOrDefault();
    }

    /// <summary>Shows a tray balloon from a background thread.</summary>
    private static void Notify(string title, string message)
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.BeginInvoke(() =>
        {
            if (app is App a) a.ShowBalloon(title, message);
        });
    }

    /// <summary>Runs <paramref name="action"/> on the WPF dispatcher and awaits the task it returns.</summary>
    private static async Task InvokeOnUiAsync(Func<Task> action)
    {
        var app = System.Windows.Application.Current;
        if (app == null) { await action().ConfigureAwait(false); return; }

        // The restore/switch pipeline is long-running; fire it on the UI thread but do not block
        // the pipe on completion — the caller only needs to know the command was accepted.
        await app.Dispatcher.InvokeAsync(() => _ = action());
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* already disposed */ }
        _cts.Dispose();
    }
}
