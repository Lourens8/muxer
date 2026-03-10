using Muxer.Android.ViewModels;

namespace Muxer.Android.Views;

public partial class SessionListPage : ContentPage
{
    public SessionListPage(SessionListViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is SessionListViewModel vm)
        {
            await vm.ConnectCommand.ExecuteAsync(null);
        }
    }
}
