using System.Collections.ObjectModel;
using System.Text.Json;
using Android.Content;
using Android.OS;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muxer.Android.Services;
using Muxer.Shared;

namespace Muxer.Android.ViewModels;

public partial class SessionListViewModel : ObservableObject
{
    private readonly List<MuxerConnection> _connections = [];

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusText = "Connecting...";

    [ObservableProperty]
    private string _newProjectName = "";

    [ObservableProperty]
    private bool _isPushEnabled;

    // Settings
    [ObservableProperty]
    private bool _showSettings;

    [ObservableProperty]
    private string _newServerUrl = "";

    public ObservableCollection<string> ExtraServers { get; } = [];
    public ObservableCollection<ProjectItem> AllProjects { get; } = [];

    public SessionListViewModel()
    {
        _isPushEnabled = MuxerForegroundService.IsRunning;
        foreach (var s in ServerSettings.GetExtraServers())
            ExtraServers.Add(s);
    }

    private void WireConnection(MuxerConnection connection)
    {
        connection.OnConnectionChanged += connected =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsConnected = _connections.Any(c => c.IsConnected);
                var names = _connections.Where(c => c.IsConnected)
                    .Select(c => new Uri(c.ServerUrl).Host);
                StatusText = IsConnected
                    ? $"Connected to {string.Join(", ", names)}"
                    : "Disconnected";
            });

        connection.OnApprovalRequired += req =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var session = Sessions.FirstOrDefault(s => s.Id == req.SessionId);
                if (session != null)
                {
                    session.Status = SessionStatus.WaitingForApproval;
                    session.PendingToolName = req.ToolName;
                    session.PendingToolInput = req.ToolInput;

                    if (session.AutoApprove)
                        _ = session.ApproveCommand.ExecuteAsync("allow");
                }
            });

        connection.OnApprovalResolved += id =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var session = Sessions.FirstOrDefault(s => s.Id == id);
                if (session != null)
                {
                    session.Status = SessionStatus.Running;
                    session.PendingToolName = null;
                    session.PendingToolInput = null;
                }
            });

        connection.OnSessionCreated += dto =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (Sessions.All(s => s.Id != dto.Id))
                    Sessions.Add(new SessionViewModel(dto, connection));
            });

        connection.OnSessionDestroyed += id =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var s = Sessions.FirstOrDefault(s => s.Id == id);
                if (s != null) Sessions.Remove(s);
            });
    }

    [RelayCommand]
    private void TogglePush()
    {
        var context = global::Android.App.Application.Context;
        var serviceIntent = new Intent(context, typeof(MuxerForegroundService));

        if (IsPushEnabled)
        {
            context.StopService(serviceIntent);
            IsPushEnabled = false;
        }
        else
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(serviceIntent);
            else
                context.StartService(serviceIntent);
            IsPushEnabled = true;
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            StatusText = "Connecting...";

            foreach (var c in _connections)
                await c.DisposeAsync();
            _connections.Clear();

            foreach (var url in ServerSettings.GetAllServers())
            {
                var conn = new MuxerConnection(url);
                WireConnection(conn);
                _connections.Add(conn);
            }

            // Connect all, don't fail if one server is unreachable
            await Task.WhenAll(_connections.Select(c =>
                c.ConnectAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        System.Diagnostics.Debug.WriteLine(
                            $"Connect to {c.ServerUrl} failed: {t.Exception?.InnerException?.Message}");
                })));

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Connection failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            Sessions.Clear();
            AllProjects.Clear();

            foreach (var conn in _connections)
            {
                try
                {
                    var sessions = await conn.GetSessionsAsync();
                    foreach (var dto in sessions)
                        Sessions.Add(new SessionViewModel(dto, conn));

                    var projects = await conn.GetProjectsAsync();
                    var label = new Uri(conn.ServerUrl).Host;
                    foreach (var p in projects)
                        AllProjects.Add(new ProjectItem(conn, label, p));
                }
                catch { /* individual server failure is ok */ }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreateSessionAsync()
    {
        if (string.IsNullOrWhiteSpace(NewProjectName)) return;

        var conn = _connections.FirstOrDefault(c => c.IsConnected);
        if (conn == null) return;

        try
        {
            var dto = await conn.CreateSessionAsync(NewProjectName);
            if (dto != null && Sessions.All(s => s.Id != dto.Id))
                Sessions.Add(new SessionViewModel(dto, conn));
            NewProjectName = "";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreateSessionFromProjectAsync(ProjectItem item)
    {
        try
        {
            var dto = await item.Connection.CreateSessionAsync(item.ProjectDir);
            if (dto != null && Sessions.All(s => s.Id != dto.Id))
                Sessions.Add(new SessionViewModel(dto, item.Connection));
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleSettings() => ShowSettings = !ShowSettings;

    [RelayCommand]
    private async Task AddServerAsync()
    {
        var url = NewServerUrl?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        if (!url.StartsWith("http")) url = "http://" + url;
        url = url.TrimEnd('/');
        if (ExtraServers.Contains(url)) return;

        ExtraServers.Add(url);
        ServerSettings.SaveExtraServers([.. ExtraServers]);
        NewServerUrl = "";
        await ConnectAsync();
    }

    [RelayCommand]
    private async Task RemoveServerAsync(string url)
    {
        ExtraServers.Remove(url);
        ServerSettings.SaveExtraServers([.. ExtraServers]);
        await ConnectAsync();
    }
}

public record ProjectItem(MuxerConnection Connection, string ServerLabel, string ProjectDir);

public partial class SessionViewModel : ObservableObject
{
    private readonly MuxerConnection _connection;

    public string Id { get; }
    public string ProjectName { get; }
    public string ServerLabel { get; }

    [ObservableProperty]
    private SessionStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedToolInput))]
    private string? _pendingToolName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedToolInput))]
    private string? _pendingToolInput;

    [ObservableProperty]
    private bool _autoApprove;

    public bool NeedsApproval => Status == SessionStatus.WaitingForApproval;

    public string StatusText => Status switch
    {
        SessionStatus.Running => "Running",
        SessionStatus.WaitingForApproval => "Needs Approval",
        SessionStatus.Stopped => "Stopped",
        _ => "Unknown"
    };

    public string? FormattedToolInput => FormatToolInput(PendingToolInput);

    public SessionViewModel(SessionDto dto, MuxerConnection connection)
    {
        _connection = connection;
        Id = dto.Id;
        ProjectName = dto.ProjectName;
        Status = dto.Status;
        PendingToolName = dto.PendingToolName;
        PendingToolInput = dto.PendingToolInput;
        ServerLabel = new Uri(connection.ServerUrl).Host;
    }

    partial void OnStatusChanged(SessionStatus value)
    {
        OnPropertyChanged(nameof(NeedsApproval));
        OnPropertyChanged(nameof(StatusText));
    }

    [RelayCommand]
    private async Task ApproveAsync(string behavior)
    {
        await _connection.ApproveAsync(Id, behavior);
        Status = SessionStatus.Running;
        PendingToolName = null;
        PendingToolInput = null;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        var host = new Uri(_connection.ServerUrl).Host;
        await Launcher.OpenAsync(new Uri($"ssh://loure@{host}"));
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        await _connection.DeleteSessionAsync(Id);
    }

    private static string? FormatToolInput(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var doc = JsonDocument.Parse(json);
            var parts = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var val = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.ToString();
                parts.Add($"{prop.Name}: {val}");
            }
            return string.Join("\n", parts);
        }
        catch
        {
            return json;
        }
    }
}
