using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Muxer.Shared;

namespace Muxer.Android.Services;

public class MuxerConnection : IAsyncDisposable
{
    public const string ServerUrl = "http://192.168.0.65:5199";

    private readonly HttpClient _http = new() { BaseAddress = new Uri(ServerUrl) };
    private HubConnection? _hub;

    public event Action<ApprovalRequestDto>? OnApprovalRequired;
    public event Action<string>? OnApprovalResolved;
    public event Action<SessionDto>? OnSessionCreated;
    public event Action<string>? OnSessionDestroyed;
    public event Action<bool>? OnConnectionChanged;

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    public async Task ConnectAsync()
    {
        _hub = new HubConnectionBuilder()
            .WithUrl($"{ServerUrl}/hubs/sessions")
            .WithAutomaticReconnect(new[] {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            })
            .Build();

        _hub.On<ApprovalRequestDto>("ApprovalRequired", req => OnApprovalRequired?.Invoke(req));
        _hub.On<string>("ApprovalResolved", id => OnApprovalResolved?.Invoke(id));
        _hub.On<SessionDto>("SessionCreated", s => OnSessionCreated?.Invoke(s));
        _hub.On<string>("SessionDestroyed", id => OnSessionDestroyed?.Invoke(id));

        _hub.Reconnected += _ => { OnConnectionChanged?.Invoke(true); return Task.CompletedTask; };
        _hub.Closed += _ => { OnConnectionChanged?.Invoke(false); return Task.CompletedTask; };

        await _hub.StartAsync();
        OnConnectionChanged?.Invoke(true);
    }

    public async Task<SessionDto[]> GetSessionsAsync()
    {
        return await _http.GetFromJsonAsync<SessionDto[]>("/api/sessions") ?? [];
    }

    public async Task<string[]> GetProjectsAsync()
    {
        return await _http.GetFromJsonAsync<string[]>("/api/projects") ?? [];
    }

    public async Task<SessionDto?> CreateSessionAsync(string projectDir)
    {
        var resp = await _http.PostAsJsonAsync("/api/sessions", new { ProjectDir = projectDir });
        return await resp.Content.ReadFromJsonAsync<SessionDto>();
    }

    public async Task ApproveAsync(string sessionId, string behavior)
    {
        await _http.PostAsJsonAsync($"/api/sessions/{sessionId}/approve", new { Behavior = behavior });
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        await _http.DeleteAsync($"/api/sessions/{sessionId}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
            await _hub.DisposeAsync();
        _http.Dispose();
    }
}
