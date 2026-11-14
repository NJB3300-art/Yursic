using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.System.Profile;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Yursic.Audio;
using Yursic.Models;
using Yursic.Plugins;
using Yursic.Storage;

namespace Yursic
{
    /// <summary>App-wide services. Created once at launch.</summary>
    public static class Services
    {
        public static PluginManager Plugins { get; } = new PluginManager();
        public static ProjectStore Projects { get; } = new ProjectStore();
        public static UsbStorageService Usb { get; } = new UsbStorageService();
        public static AudioEngine Engine { get; } = new AudioEngine();

        public static bool IsXbox =>
            AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox";
    }

    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            // On Xbox, use controller focus navigation rather than the mouse-cursor mode.
            RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
            Suspending += OnSuspending;
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                // Draw to the full screen; pages add their own TV-safe padding.
                ApplicationView.GetForCurrentView().SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);

                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;

                SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;

                await Services.Plugins.LoadInstalledAsync();
            }

            if (!e.PrelaunchActivated)
            {
                if (rootFrame.Content == null)
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                Window.Current.Activate();
            }
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null) return;

            if (rootFrame.Content is IBackHandler handler && handler.HandleBack())
            {
                e.Handled = true;
                return;
            }
            if (rootFrame.CanGoBack)
            {
                rootFrame.GoBack();
                e.Handled = true;
            }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load page " + e.SourcePageType.FullName);
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                if (Services.Projects.Current != null)
                    await Services.Projects.SaveAsync(Services.Projects.Current);
            }
            catch { /* never block suspension */ }
            deferral.Complete();
        }
    }

    /// <summary>Pages that want first chance at the B button / Back.</summary>
    public interface IBackHandler
    {
        bool HandleBack();
    }
}
