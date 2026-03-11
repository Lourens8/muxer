using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Muxer.Shared;

namespace Muxer.Android.Services;

[Service(ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public class MuxerForegroundService : Service
{
    public const string ChannelId = "muxer_approvals";
    public const string PersistentChannelId = "muxer_persistent";
    public const int PersistentNotificationId = 1;
    private const string ActionApprove = "com.muxer.ACTION_APPROVE";
    private const string ActionDeny = "com.muxer.ACTION_DENY";

    public static bool IsRunning { get; private set; }

    private MuxerConnection? _connection;
    private int _notificationCounter = 100;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        IsRunning = true;
        CreateChannels();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionApprove || intent?.Action == ActionDeny)
        {
            HandleApprovalAction(intent);
            return StartCommandResult.Sticky;
        }

        var notification = new NotificationCompat.Builder(this, PersistentChannelId)
            .SetContentTitle("Muxer")
            .SetContentText("Monitoring Claude sessions")
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetOngoing(true)
            .SetPriority(NotificationCompat.PriorityLow)
            .Build();

        StartForeground(PersistentNotificationId, notification,
            global::Android.Content.PM.ForegroundService.TypeDataSync);

        _ = StartMonitoringAsync();

        return StartCommandResult.Sticky;
    }

    private async Task StartMonitoringAsync()
    {
        _connection = new MuxerConnection();
        _connection.OnApprovalRequired += ShowApprovalNotification;
        _connection.OnApprovalResolved += DismissApprovalNotification;

        try
        {
            await _connection.ConnectAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Connection failed: {ex.Message}");
        }
    }

    private void ShowApprovalNotification(ApprovalRequestDto request)
    {
        var id = ++_notificationCounter;
        _sessionNotificationMap[request.SessionId] = id;

        var approveIntent = new Intent(this, typeof(MuxerForegroundService))
            .SetAction(ActionApprove)
            .PutExtra("sessionId", request.SessionId);
        var approvePending = PendingIntent.GetService(this, id * 10,
            approveIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;

        var denyIntent = new Intent(this, typeof(MuxerForegroundService))
            .SetAction(ActionDeny)
            .PutExtra("sessionId", request.SessionId);
        var denyPending = PendingIntent.GetService(this, id * 10 + 1,
            denyIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;

        // Tap to open app
        var launchIntent = Platform.CurrentActivity?.PackageManager?
            .GetLaunchIntentForPackage(Platform.CurrentActivity.PackageName!);
        var launchPending = launchIntent != null
            ? PendingIntent.GetActivity(this, id * 10 + 2,
                launchIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)
            : null;

        var description = $"{request.ToolName}: {request.ToolInput}";
        var contextPreview = description.Length > 100
            ? description[..100] + "..."
            : description;

        var builder = new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle($"Approval needed: {request.ProjectName}")
            .SetContentText(contextPreview)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(description))
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogAlert)
            .SetPriority(NotificationCompat.PriorityHigh)
            .SetAutoCancel(true)
            .AddAction(global::Android.Resource.Drawable.IcInputAdd, "Allow Once", approvePending)
            .AddAction(global::Android.Resource.Drawable.IcDelete, "Deny", denyPending);

        if (launchPending != null)
            builder.SetContentIntent(launchPending);

        NotificationManagerCompat.From(this).Notify(id, builder.Build());
    }

    private readonly Dictionary<string, int> _sessionNotificationMap = new();

    private void DismissApprovalNotification(string sessionId)
    {
        if (_sessionNotificationMap.TryGetValue(sessionId, out var id))
        {
            NotificationManagerCompat.From(this).Cancel(id);
            _sessionNotificationMap.Remove(sessionId);
        }
    }

    private void HandleApprovalAction(Intent intent)
    {
        var sessionId = intent.GetStringExtra("sessionId");
        if (sessionId == null || _connection == null) return;

        var behavior = intent.Action == ActionApprove ? "allow" : "deny";
        _ = _connection.ApproveAsync(sessionId, behavior);

        if (_sessionNotificationMap.TryGetValue(sessionId, out var id))
        {
            NotificationManagerCompat.From(this).Cancel(id);
            _sessionNotificationMap.Remove(sessionId);
        }
    }

    private void CreateChannels()
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager == null) return;

        var approvalChannel = new NotificationChannel(ChannelId, "Approval Requests",
            NotificationImportance.High)
        {
            Description = "Notifications for Claude approval requests"
        };
        manager.CreateNotificationChannel(approvalChannel);

        var persistentChannel = new NotificationChannel(PersistentChannelId, "Service Status",
            NotificationImportance.Low)
        {
            Description = "Persistent service notification"
        };
        manager.CreateNotificationChannel(persistentChannel);
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        _ = _connection?.DisposeAsync();
        base.OnDestroy();
    }
}
