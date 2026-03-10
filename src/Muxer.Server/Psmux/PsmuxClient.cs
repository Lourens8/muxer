using System.Diagnostics;

namespace Muxer.Server.Psmux;

public class PsmuxClient
{
    public const string PsmuxPath = @"C:\Users\loure\AppData\Local\Microsoft\WinGet\Packages\marlocarlo.psmux_Microsoft.Winget.Source_8wekyb3d8bbwe\psmux.exe";

    private readonly ILogger<PsmuxClient> _logger;

    public PsmuxClient(ILogger<PsmuxClient> logger)
    {
        _logger = logger;
    }

    public async Task<string> CapturePaneAsync(string sessionName)
    {
        return await RunAsync($"capture-pane -p -t {sessionName}");
    }

    public async Task SendKeysAsync(string sessionName, string keys)
    {
        await RunAsync($"send-keys -t {sessionName} {keys} Enter");
        _logger.LogInformation("Sent keys '{Keys}' to session {Session}", keys, sessionName);
    }

    public async Task NewSessionAsync(string sessionName, string workingDir, string command)
    {
        await RunAsync($"new-session -d -s {sessionName} -c \"{workingDir}\" {command}",
            clearClaudeEnv: true);
        _logger.LogInformation("Created session {Session} in {Dir}", sessionName, workingDir);
    }

    public async Task<bool> HasSessionAsync(string sessionName)
    {
        var output = await RunAsync("list-sessions", ignoreErrors: true);
        return output.Contains(sessionName, StringComparison.OrdinalIgnoreCase);
    }

    public async Task KillSessionAsync(string sessionName)
    {
        await RunAsync($"kill-session -t {sessionName}", ignoreErrors: true);
        _logger.LogInformation("Killed session {Session}", sessionName);
    }

    private async Task<string> RunAsync(string args, bool ignoreErrors = false, bool clearClaudeEnv = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PsmuxPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (clearClaudeEnv)
        {
            // psmux inherits our environment; clear CLAUDECODE so Claude CLI
            // doesn't think it's nested inside another Claude Code session.
            psi.Environment.Remove("CLAUDECODE");
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start psmux");

        var output = await proc.StandardOutput.ReadToEndAsync();
        var error = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0 && !ignoreErrors)
        {
            _logger.LogWarning("psmux {Args} exited {Code}: {Error}", args, proc.ExitCode, error);
        }

        return output;
    }
}
