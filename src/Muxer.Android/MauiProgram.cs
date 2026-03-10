using Muxer.Android.Services;
using Muxer.Android.ViewModels;
using Muxer.Android.Views;

namespace Muxer.Android;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<MuxerConnection>();
        builder.Services.AddSingleton<SessionListViewModel>();
        builder.Services.AddTransient<SessionListPage>();

        return builder.Build();
    }
}
