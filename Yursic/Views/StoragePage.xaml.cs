using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Portable;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Yursic.Helpers;
using Yursic.Models;
using Yursic.Plugins;
using Yursic.Storage;

namespace Yursic.Views
{
    public sealed partial class StoragePage : Page
    {
        private UsbDrive _drive;
        private readonly Stack<StorageFolder> _path = new Stack<StorageFolder>();
        private DeviceWatcher _watcher;
        private bool _refreshing;

        public StoragePage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            StartWatcher();
            RefreshButton.Focus(FocusState.Programmatic);
            await RefreshAllAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (_watcher != null)
            {
                _watcher.Added -= Watcher_Changed;
                _watcher.Removed -= Watcher_Removed;
                if (_watcher.Status == DeviceWatcherStatus.Started || _watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                    _watcher.Stop();
                _watcher = null;
            }
        }

        // Refresh automatically when a USB drive is plugged in or removed.
        private void StartWatcher()
        {
            try
            {
                _watcher = DeviceInformation.CreateWatcher(StorageDevice.GetDeviceSelector());
                _watcher.Added += Watcher_Changed;
                _watcher.Removed += Watcher_Removed;
                _watcher.Start();
            }
            catch { _watcher = null; }
        }

        private async void Watcher_Changed(DeviceWatcher sender, DeviceInformation args) =>
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await RefreshDrivesAsync());

        private async void Watcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args) =>
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await RefreshDrivesAsync());

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();

        private async Task RefreshAllAsync()
        {
            await RefreshDrivesAsync();
            await RefreshLocalAsync();
        }

        private async Task RefreshDrivesAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                var drives = await Services.Usb.GetDrivesAsync();
                DrivesList.ItemsSource = drives;
                NoDrives.Visibility = drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                // Close the browser if its drive went away.
                if (_drive != null && drives.All(d => d.Root.Path != _drive.Root.Path))
                {
                    _drive = null;
                    _path.Clear();
                    BrowserCard.Visibility = Visibility.Collapsed;
                }
            }
            finally
            {
                _refreshing = false;
            }
        }

        private async Task RefreshLocalAsync()
        {
            var local = ApplicationData.Current.LocalFolder;
            ProjectsSize.Text = Format.Bytes(await UsbStorageService.GetFolderSizeAsync(await ProjectStore.GetRootAsync()));
            PluginsSize.Text = Format.Bytes(await UsbStorageService.GetFolderSizeAsync(await PluginManager.GetRootAsync()));
            var exports = await local.CreateFolderAsync("Exports", CreationCollisionOption.OpenIfExists);
            var files = await exports.GetFilesAsync();
            ExportsSize.Text = $"{Format.Bytes(await UsbStorageService.GetFolderSizeAsync(exports))} · {files.Count} file(s)";
        }

        // ------------------------------------------------------------- browser

        private async void Drive_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is UsbDrive drive)) return;
            _drive = drive;
            _path.Clear();
            _path.Push(drive.Root);
            BrowserCard.Visibility = Visibility.Visible;
            await ShowFolderAsync();
            UpButton.Focus(FocusState.Keyboard);
        }

        private async Task ShowFolderAsync()
        {
            if (_path.Count == 0) return;
            var folder = _path.Peek();
            var names = _path.Reverse().Select((f, i) => i == 0 ? _drive.Name : f.Name);
            PathText.Text = string.Join(" › ", names);
            UpButton.IsEnabled = _path.Count > 1;
            BrowserStatus.Text = "Loading…";
            try
            {
                var entries = await Services.Usb.ListAsync(folder);
                EntriesList.ItemsSource = entries;
                EmptyFolder.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ulong total = (ulong)entries.Where(x => !x.IsFolder).Sum(x => (long)x.Size);
                BrowserStatus.Text = $"{entries.Count(x => x.IsFolder)} folder(s), {entries.Count(x => !x.IsFolder)} file(s), {Format.Bytes(total)}";
            }
            catch (Exception ex)
            {
                EntriesList.ItemsSource = null;
                BrowserStatus.Text = "Couldn't read this folder: " + ex.Message;
            }
        }

        private async void Up_Click(object sender, RoutedEventArgs e)
        {
            if (_path.Count > 1) _path.Pop();
            await ShowFolderAsync();
        }

        private async void Prepare_Click(object sender, RoutedEventArgs e)
        {
            if (_drive == null) return;
            try
            {
                await Services.Usb.PrepareDriveAsync(_drive);
                BrowserStatus.Text = "Created Yursic\\Plugins, Projects, Samples and Exports.";
                await ShowFolderAsync();
            }
            catch (Exception ex) { await Dialogs.InfoAsync("Couldn't create folders", ex.Message); }
        }

        private async void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_path.Count == 0) return;
            var name = await Dialogs.PromptAsync("New folder", "Name", "New folder", "Create");
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                await _path.Peek().CreateFolderAsync(name.Trim(), CreationCollisionOption.GenerateUniqueName);
                await ShowFolderAsync();
            }
            catch (Exception ex) { await Dialogs.InfoAsync("Couldn't create folder", ex.Message); }
        }

        private async void Entry_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is StorageEntry entry)) return;

            // Plain folders open; plugin folders get the action dialog.
            if (entry.IsFolder && !(await IsPluginFolderAsync(entry)))
            {
                _path.Push((StorageFolder)entry.Item);
                await ShowFolderAsync();
                return;
            }

            string primary = null;
            switch (entry.Kind)
            {
                case "Plugin": primary = "Install plugin"; break;
                case "Project": primary = "Open project"; break;
            }

            var dialog = new ContentDialog
            {
                Title = entry.Name,
                Content = new TextBlock { Text = entry.Detail, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = primary ?? "",
                SecondaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = primary != null ? ContentDialogButton.Primary : ContentDialogButton.Close
            };
            if (entry.IsFolder)
            {
                dialog.Content = new TextBlock { Text = "This folder is a plugin. Install it, or open it to see the files.", TextWrapping = TextWrapping.Wrap };
                dialog.CloseButtonText = "Open folder";
            }

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    if (entry.Kind == "Plugin")
                    {
                        var def = await Services.Plugins.InstallAsync(entry.Item);
                        BrowserStatus.Text = $"Installed {def.Name}.";
                        await RefreshLocalAsync();
                    }
                    else if (entry.Kind == "Project")
                    {
                        var id = await Services.Projects.ImportArchiveAsync((StorageFile)entry.Item);
                        MainPage.OpenStudio(id);
                    }
                }
                catch (Exception ex) { await Dialogs.InfoAsync("That didn't work", ex.Message); }
            }
            else if (result == ContentDialogResult.Secondary)
            {
                if (!await Dialogs.ConfirmAsync("Delete " + entry.Name + "?", "This permanently removes it from the USB drive.", "Delete"))
                    return;
                try
                {
                    await Services.Usb.DeleteAsync(entry.Item);
                    await ShowFolderAsync();
                    await RefreshDrivesAsync();
                }
                catch (Exception ex) { await Dialogs.InfoAsync("Couldn't delete", ex.Message); }
            }
            else if (entry.IsFolder)
            {
                _path.Push((StorageFolder)entry.Item);
                await ShowFolderAsync();
            }
        }

        private static async Task<bool> IsPluginFolderAsync(StorageEntry entry)
        {
            if (!(entry.Item is StorageFolder f)) return false;
            try
            {
                var isPlugin = await f.TryGetItemAsync(PluginManager.ManifestName) is StorageFile;
                if (isPlugin) entry.Kind = "Plugin";
                return isPlugin;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------- console storage

        private async void BackupProjects_Click(object sender, RoutedEventArgs e)
        {
            var drive = await Dialogs.PickDriveAsync();
            if (drive == null) return;
            try
            {
                var dest = await Services.Usb.GetSubFolderAsync(drive, "Projects");
                var projects = await Services.Projects.ListAsync();
                int i = 0;
                foreach (var p in projects)
                {
                    LocalStatus.Text = $"Backing up {++i} of {projects.Count}: {p.Name}…";
                    await Services.Projects.ExportArchiveAsync(p.Id, dest);
                }
                LocalStatus.Text = $"Backed up {projects.Count} project(s) to {drive.Name}\\Yursic\\Projects.";
                await RefreshDrivesAsync();
            }
            catch (Exception ex)
            {
                LocalStatus.Text = "";
                await Dialogs.InfoAsync("Backup failed", ex.Message);
            }
        }

        private async void CopyExports_Click(object sender, RoutedEventArgs e)
        {
            var exports = await ApplicationData.Current.LocalFolder.CreateFolderAsync("Exports", CreationCollisionOption.OpenIfExists);
            var files = await exports.GetFilesAsync();
            if (files.Count == 0) { LocalStatus.Text = "No exports yet. Use Export in the studio."; return; }
            var drive = await Dialogs.PickDriveAsync();
            if (drive == null) return;
            try
            {
                int i = 0;
                foreach (var f in files)
                {
                    LocalStatus.Text = $"Copying {++i} of {files.Count}…";
                    await Services.Usb.CopyToDriveAsync(f, drive, "Exports");
                }
                LocalStatus.Text = $"Copied {files.Count} file(s) to {drive.Name}\\Yursic\\Exports.";
                await RefreshDrivesAsync();
            }
            catch (Exception ex)
            {
                LocalStatus.Text = "";
                await Dialogs.InfoAsync("Copy failed", ex.Message);
            }
        }

        private async void ClearExports_Click(object sender, RoutedEventArgs e)
        {
            if (!await Dialogs.ConfirmAsync("Delete all exports?", "Mixdowns stored on this console will be removed. Copies on USB drives aren't touched.", "Delete"))
                return;
            var exports = await ApplicationData.Current.LocalFolder.CreateFolderAsync("Exports", CreationCollisionOption.OpenIfExists);
            foreach (var f in await exports.GetFilesAsync())
                await f.DeleteAsync(StorageDeleteOption.PermanentDelete);
            await RefreshLocalAsync();
            LocalStatus.Text = "Exports cleared.";
        }
    }
}
