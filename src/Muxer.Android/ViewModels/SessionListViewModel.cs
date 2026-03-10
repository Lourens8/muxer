using System.Collections.ObjectModel;
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

    public SessionListViewModel(MuxerConnection connection)
    {
        _connection = connection;

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
                    session.ApprovalContext = req.Context;
                    session.ApprovalOptions = req.Options;
                }
            });

        _connection.OnApprovalResolved += id =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var session = Sessions.FirstOrDefault(s => s.Id == id);
                if (session != null)
                {
                    session.Status = SessionStatus.Running;
                    session.ApprovalContext = null;
                    session.ApprovalOptions = null;
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
    private string? _approvalContext;

    [ObservableProperty]
    private string[]? _approvalOptions;

    public bool NeedsApproval => Status == SessionStatus.WaitingForApproval;

    public string StatusText => Status switch
    {
        SessionStatus.Running => "Running",
        SessionStatus.WaitingForApproval => "Needs Approval",
        SessionStatus.Stopped => "Stopped",
        _ => "Unknown"
    };

    public SessionViewModel(SessionDto dto, MuxerConnection connection)
    {
        _connection = connection;
        Id = dto.Id;
        ProjectName = dto.ProjectName;
        Status = dto.Status;
        ApprovalContext = dto.ApprovalContext;
        ApprovalOptions = dto.ApprovalOptions;
    }

    partial void OnStatusChanged(SessionStatus value)
    {
        OnPropertyChanged(nameof(NeedsApproval));
        OnPropertyChanged(nameof(StatusText));
    }

    [RelayCommand]
    private async Task ApproveAsync(string option)
    {
        await _connection.ApproveAsync(Id, int.Parse(option));
        Status = SessionStatus.Running;
        ApprovalContext = null;
        ApprovalOptions = null;
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
}
