using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace Muxer.Server.Relay;

public class RelayClient : IAsyncDisposable
{
    private readonly string _relayUrl;
    private readonly HttpClient _http;
    private readonly HubConnection _hub;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
    private readonly ILogger<RelayClient> _logger;

    public RelayClient(string relayUrl, ILogger<RelayClient> logger)
    {
        _relayUrl = relayUrl.TrimEnd('/');
        _logger = logger;
        _http = new HttpClient
        {
            BaseAddress = new Uri(_relayUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };

        _hub = new HubConnectionBuilder()
            .WithUrl($"{_relayUrl}/hubs/sessions")
            .WithAutomaticReconnect()
            .Build();

        _hub.On<string, string>("RelayDecision", OnRelayDecision);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            await _hub.StartAsync(ct);
            _logger.LogInformation("Relay: Connected to {Url}", _relayUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Relay: Failed to connect to {Url}, will retry via auto-reconnect", _relayUrl);
        }
    }

    public async Task<string> ForwardApprovalAsync(string projectName, string projectDir,
        string toolName, string toolInput, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("/api/relay/approval-request", new
        {
            ServerName = Environment.MachineName,
            ProjectName = projectName,
            ProjectDir = projectDir,
            ToolName = toolName,
            ToolInput = toolInput
        }, ct);

        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<RelayResponse>(ct);
        var sessionId = result!.SessionId;

        _logger.LogInformation("Relay: Forwarded {Tool} in {Project}, relay session {Id}",
            toolName, projectName, sessionId);

        // Wait for decision via SignalR
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[sessionId] = tcs;

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromMinutes(30), ct);
        }
        finally
        {
            _pending.TryRemove(sessionId, out _);
        }
    }

    private void OnRelayDecision(string sessionId, string behavior)
    {
        _logger.LogInformation("Relay: Decision {Behavior} for session {Id}", behavior, sessionId);
        if (_pending.TryRemove(sessionId, out var tcs))
            tcs.TrySetResult(behavior);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var tcs in _pending.Values)
            tcs.TrySetCanceled();
        _pending.Clear();
        await _hub.DisposeAsync();
        _http.Dispose();
    }
}

record RelayResponse(string SessionId);
