using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Muxer.Shared;

const string ServerUrl = "http://localhost:5199";
var PsmuxPath = Path.Combine(AppContext.BaseDirectory, "psmux.exe");

using var http = new HttpClient { BaseAddress = new Uri(ServerUrl), Timeout = TimeSpan.FromSeconds(3) };

// Check server is running
try
{
    await http.GetAsync("/api/projects");
}
catch
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("  Muxer server is not running.");
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("  Start it first, or check that it's in your startup programs.");
    Console.ResetColor();
    Console.Write("  Press any key to exit...");
    Console.ReadKey(true);
    return;
}

while (true)
{
    // Main menu
    Console.Title = "Muxer";
    Console.Clear();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("  Muxer");
    Console.WriteLine("  =====");
    Console.ResetColor();
    Console.WriteLine();

    // Show active sessions
    var sessions = await http.GetFromJsonAsync<SessionDto[]>("/api/sessions") ?? [];
    if (sessions.Length > 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  Active Sessions:");
        Console.ResetColor();

        for (int i = 0; i < sessions.Length; i++)
        {
            var s = sessions[i];
            var tag = s.Status == SessionStatus.WaitingForApproval ? " [NEEDS APPROVAL]" : "";

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  S{i + 1} ");
            Console.ResetColor();
            Console.Write(s.ProjectName);
            if (tag.Length > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write(tag);
            }
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  (K{i + 1} to kill)");
            Console.ResetColor();
            Console.WriteLine();
        }
        Console.WriteLine();
    }

    // Show projects
    var projects = await http.GetFromJsonAsync<string[]>("/api/projects") ?? [];
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine("  New Session:");
    Console.ResetColor();

    for (int i = 0; i < projects.Length; i++)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  [{i + 1,2}] ");
        Console.ResetColor();
        Console.WriteLine(projects[i]);
    }

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write("  Pick #, S# attach, K# kill, new name, or 'q': ");
    Console.ResetColor();

    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(input) || input.Equals("q", StringComparison.OrdinalIgnoreCase))
        return;

    // Attach to existing session
    if (input.StartsWith("s", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(input[1..], out int si) && si >= 1 && si <= sessions.Length)
    {
        AttachPsmux(PsmuxPath, sessions[si - 1].PsmuxSessionName, sessions[si - 1].ProjectName);
        continue; // return to menu after detach
    }

    // Kill a session
    if (input.StartsWith("k", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(input[1..], out int ki) && ki >= 1 && ki <= sessions.Length)
    {
        var target = sessions[ki - 1];
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  Killing {target.ProjectName}...");
        await http.DeleteAsync($"/api/sessions/{target.Id}");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" done.");
        Console.ResetColor();
        await Task.Delay(500);
        continue;
    }

    // Resolve project
    string projectDir;
    if (int.TryParse(input, out int choice) && choice >= 1 && choice <= projects.Length)
    {
        projectDir = projects[choice - 1];
    }
    else if (int.TryParse(input, out _))
    {
        Console.WriteLine("  Invalid selection.");
        continue;
    }
    else
    {
        projectDir = input;
    }

    // Create session via API
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write($"  Starting Claude in {projectDir}...");
    Console.ResetColor();

    try
    {
        var resp = await http.PostAsJsonAsync("/api/sessions", new { ProjectDir = projectDir });
        resp.EnsureSuccessStatusCode();
        var created = await resp.Content.ReadFromJsonAsync<SessionDto>();

        if (created is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(" failed.");
            Console.ResetColor();
            continue;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" done!");
        Console.ResetColor();

        // Give psmux a moment to initialize
        await Task.Delay(500);

        AttachPsmux(PsmuxPath, created.PsmuxSessionName, created.ProjectName);
        // return to menu after detach
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  Error: {ex.Message}");
        Console.ResetColor();
        Console.Write("  Press any key...");
        Console.ReadKey(true);
    }
}

static void AttachPsmux(string psmuxPath, string sessionName, string projectName)
{
    Console.Title = $"Muxer - {projectName}";
    Console.Clear();

    var proc = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = psmuxPath,
            Arguments = $"attach -t {sessionName}",
            UseShellExecute = false
        }
    };

    try
    {
        proc.Start();
        proc.WaitForExit();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  Failed to attach: {ex.Message}");
        Console.ResetColor();
        Console.Write("  Press any key...");
        Console.ReadKey(true);
    }

    // psmux leaves console mode in a bad state after detach — reset it
    ResetConsoleMode();
}

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr GetStdHandle(int nStdHandle);

static void ResetConsoleMode()
{
    const int STD_INPUT_HANDLE = -10;
    const uint ENABLE_ECHO_INPUT = 0x0004;
    const uint ENABLE_LINE_INPUT = 0x0002;
    const uint ENABLE_PROCESSED_INPUT = 0x0001;

    var handle = GetStdHandle(STD_INPUT_HANDLE);
    SetConsoleMode(handle, ENABLE_ECHO_INPUT | ENABLE_LINE_INPUT | ENABLE_PROCESSED_INPUT);
}
