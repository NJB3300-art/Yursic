using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Yursic.Plugins;
using Yursic.Storage;

namespace Yursic.Views
{
    public sealed partial class PluginsPage : Page
    {
        public string ExampleJson { get; } =
@"Yursic\Plugins\SpaceEcho\plugin.json
{
  ""id"": ""me.spaceecho"",
  ""name"": ""Space Echo"",
  ""version"": ""1.0.0"",
  ""author"": ""You"",
  ""category"": ""Delay"",
  ""params"": [
    { ""id"": ""time"", ""name"": ""Time"", ""min"": 50, ""max"": 1200, ""default"": 400, ""unit"": ""ms"", ""curve"": ""log"" },
    { ""id"": ""mix"",  ""name"": ""Mix"",  ""min"": 0,  ""max"": 1,    ""default"": 0.35, ""unit"": ""%"" }
  ],
  ""chain"": [
    { ""node"": ""delay"", ""time"": ""$time"", ""feedback"": 0.55, ""tone"": 3500, ""mix"": ""$mix"" },
    { ""node"": ""reverb"", ""size"": 0.7, ""mix"": ""$mix*0.5"" }
  ]
}";

        private List<StorageEntry> _candidates = new List<StorageEntry>();

        public PluginsPage()
        {
            InitializeComponent();
            InstalledList.ItemsSource = Services.Plugins.Installed;
            BuiltInList.ItemsSource = BuiltInEffects.All;
            Services.Plugins.Installed.CollectionChanged += (s, e) => UpdateEmpty();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            UpdateEmpty();
            ScanButton.Focus(FocusState.Programmatic);
            await ScanAsync(quiet: true);
        }

        private void UpdateEmpty()
        {
            NoneInstalled.Visibility = Services.Plugins.Installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadErrors.Text = Services.Plugins.LoadErrors.Count == 0
                ? ""
                : "Couldn't load: " + string.Join("; ", Services.Plugins.LoadErrors);
        }

        private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync(quiet: false);

        private async Task ScanAsync(bool quiet)
        {
            Working.IsActive = true;
            ScanStatus.Text = "Scanning…";
            try
            {
                var drives = await Services.Usb.GetDrivesAsync();
                if (drives.Count == 0)
                {
                    _candidates = new List<StorageEntry>();
                    ScanStatus.Text = quiet ? "" : "No USB drive found. Plug one in and scan again.";
                }
                else
                {
                    _candidates = await Services.Usb.FindPluginsAsync();
                    ScanStatus.Text = _candidates.Count == 0
                        ? $"No plugins on {string.Join(", ", drives.Select(d => d.Name))}."
                        : $"Found {_candidates.Count} plugin(s). Choose one to install.";
                }
                Candidates.ItemsSource = _candidates;
                InstallAllButton.Visibility = _candidates.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally
            {
                Working.IsActive = false;
            }
        }

        private async void Candidate_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is StorageEntry entry)
            {
                var def = await InstallAsync(entry);
                if (def != null)
                    await Dialogs.InfoAsync("Installed", $"{def.Name} is ready. {(def.IsEffect ? "Find it under Add effect in the studio." : "Its sounds are in the studio's Sounds panel.")}");
            }
        }

        private async void InstallAll_Click(object sender, RoutedEventArgs e)
        {
            int ok = 0;
            foreach (var c in _candidates.ToList())
                if (await InstallAsync(c) != null) ok++;
            ScanStatus.Text = $"Installed {ok} of {_candidates.Count}.";
        }

        private async Task<PluginDefinition> InstallAsync(StorageEntry entry)
        {
            Working.IsActive = true;
            ScanStatus.Text = "Installing " + entry.Name + "…";
            try
            {
                var def = await Services.Plugins.InstallAsync(entry.Item);
                ScanStatus.Text = $"Installed {def.Name} v{def.Version}.";
                return def;
            }
            catch (Exception ex)
            {
                ScanStatus.Text = "";
                await Dialogs.InfoAsync("Couldn't install " + entry.Name, ex.Message);
                return null;
            }
            finally
            {
                Working.IsActive = false;
                UpdateEmpty();
            }
        }

        private async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is PluginDefinition def)) return;
            if (!await Dialogs.ConfirmAsync("Remove " + def.Name + "?",
                    "Tracks using this effect will skip it until you install it again.", "Remove"))
                return;
            try { await Services.Plugins.UninstallAsync(def); }
            catch (Exception ex) { await Dialogs.InfoAsync("Couldn't remove plugin", ex.Message); }
        }
    }
}
