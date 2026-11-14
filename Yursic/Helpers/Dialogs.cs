using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Yursic.Storage;

namespace Yursic
{
    /// <summary>Controller-friendly dialogs. Only one ContentDialog can be open at a time.</summary>
    public static class Dialogs
    {
        private static bool _open;

        private static async Task<ContentDialogResult> ShowAsync(ContentDialog d)
        {
            if (_open) return ContentDialogResult.None;
            _open = true;
            try { return await d.ShowAsync(); }
            finally { _open = false; }
        }

        public static async Task InfoAsync(string title, string message)
        {
            await ShowAsync(new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK"
            });
        }

        public static async Task<bool> ConfirmAsync(string title, string message, string confirm)
        {
            var r = await ShowAsync(new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = confirm,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            });
            return r == ContentDialogResult.Primary;
        }

        /// <summary>Returns the text, or null if cancelled.</summary>
        public static async Task<string> PromptAsync(string title, string label, string initial, string confirm)
        {
            var box = new TextBox { Header = label, Text = initial ?? "", SelectionStart = 0, SelectionLength = (initial ?? "").Length };
            var d = new ContentDialog
            {
                Title = title,
                Content = box,
                PrimaryButtonText = confirm,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            return await ShowAsync(d) == ContentDialogResult.Primary ? box.Text : null;
        }

        /// <summary>Lets the user choose from a list. Returns null if cancelled.</summary>
        public static async Task<T> PickAsync<T>(string title, IList<T> items, string emptyText = "Nothing here.") where T : class
        {
            var list = new ListView
            {
                ItemsSource = items,
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = true,
                MaxHeight = 420
            };
            if (typeof(T) == typeof(StorageEntry))
                list.ItemTemplate = (DataTemplate)Application.Current.Resources["EntryTemplate"];
            T picked = null;
            ContentDialog d = null;
            list.ItemClick += (s, e) => { picked = e.ClickedItem as T; d?.Hide(); };
            d = new ContentDialog
            {
                Title = title,
                Content = items.Count == 0 ? (UIElement)new TextBlock { Text = emptyText } : list,
                CloseButtonText = "Cancel"
            };
            await ShowAsync(d);
            return picked;
        }

        /// <summary>Picks a USB drive (skips the question when only one is plugged in).</summary>
        public static async Task<UsbDrive> PickDriveAsync()
        {
            var drives = await Services.Usb.GetDrivesAsync();
            if (drives.Count == 0)
            {
                await InfoAsync("No USB drive", "Plug in a USB drive and try again. Drives formatted as NTFS or exFAT work best.");
                return null;
            }
            if (drives.Count == 1) return drives[0];
            var list = new ListView
            {
                ItemsSource = drives,
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = true,
                DisplayMemberPath = "Name"
            };
            UsbDrive picked = null;
            ContentDialog d = null;
            list.ItemClick += (s, e) => { picked = e.ClickedItem as UsbDrive; d?.Hide(); };
            d = new ContentDialog { Title = "Choose a USB drive", Content = list, CloseButtonText = "Cancel" };
            await ShowAsync(d);
            return picked;
        }
    }
}
