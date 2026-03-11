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
    private readonly MuxerConnection _connection;

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusText = "Connecting...";

    [ObservableProperty]
    private string[] _projects = [];

    [ObservableProperty]
    private string _newProjectName = "";

    [ObservableProperty]
    private bool _isPushEnabled;

    public SessionListViewModel(MuxerConnection connection)
    {
        _connection = connection;
        _isPushEnabled = MuxerForegroundService.IsRunning;

        _connection.OnConnectionChanged += connected =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsConnected = connected;
                StatusText = connected ? $"Connected to {MuxerConnection.ServerUrl}" : "Disconnected";
            });

        _connection.OnApprovalRequired += req =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var session = Sessions.FirstOrDefault(s => s.Id == req.SessionId);
                if (session != null)
                {
                    session.Status = SessionStatus.WaitingForApproval;
                    session.PendingToolName = req.ToolName;
                    session.PendingToolInput = req.ToolInput;

                    if (session.AutoApprove)
                    {
                        _ = session.ApproveCommand.ExecuteAsync("allow");
                    }
                }
            });

        _connection.OnApprovalResolved += id =>
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

        _connection.OnSessionCreated += dto =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (Sessions.All(s => s.Id != dto.Id))
                    Sessions.Add(new SessionViewModel(dto, _connection));
            });

        _connection.OnSessionDestroyed += id =>
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
            await _connection.ConnectAsync();
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
            var sessions = await _connection.GetSessionsAsync();
            Sessions.Clear();
            foreach (var dto in sessions)
                Sessions.Add(new SessionViewModel(dto, _connection));

            Projects = await _connection.GetProjectsAsync();
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

        try
        {
            var dto = await _connection.CreateSessionAsync(NewProjectName);
            if (dto != null && Sessions.All(s => s.Id != dto.Id))
                Sessions.Add(new SessionViewModel(dto, _connection));
            NewProjectName = "";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreateSessionFromProjectAsync(string projectName)
    {
        try
        {
            var dto = await _connection.CreateSessionAsync(projectName);
            if (dto != null && Sessions.All(s => s.Id != dto.Id))
                Sessions.Add(new SessionViewModel(dto, _connection));
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }
}

public partial class SessionViewModel : ObservableObject
{
    private readonly MuxerConnection _connection;

    public string Id { get; }
    public string ProjectName { get; }

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
        await Launcher.OpenAsync(new Uri("ssh://loure@192.168.0.65"));
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
