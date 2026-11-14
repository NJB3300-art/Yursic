using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Yursic.Views;

namespace Yursic
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            // Keep content inside the TV-safe area (5% / ~48x27 px at 1080p).
            if (Services.IsXbox)
                SafeArea.Margin = new Thickness(48, 27, 48, 27);
            Loaded += (s, e) =>
            {
                if (Nav.SelectedItem == null)
                    Nav.SelectedItem = HomeItem;
            };
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            // Coming back from the studio: the cached project list may be stale.
            if (e.NavigationMode == NavigationMode.Back && ContentFrame.Content is HomePage home)
                await home.RefreshAsync();
        }

        private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            var item = args.SelectedItem as NavigationViewItem;
            if (item == null) return;
            switch (item.Tag as string)
            {
                case "home": Go(typeof(HomePage)); break;
                case "plugins": Go(typeof(PluginsPage)); break;
                case "storage": Go(typeof(StoragePage)); break;
            }
        }

        private void Go(System.Type page)
        {
            if (ContentFrame.CurrentSourcePageType != page)
                ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
        }

        /// <summary>Opens a project full screen in the studio.</summary>
        public static void OpenStudio(string projectId)
        {
            var root = Window.Current.Content as Frame;
            root?.Navigate(typeof(StudioPage), projectId, new DrillInNavigationTransitionInfo());
        }
    }
}
