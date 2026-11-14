using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Yursic.Models;
using Yursic.Storage;

namespace Yursic.Views
{
    public sealed partial class HomePage : Page
    {
        public HomePage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            await RefreshAsync();
            NewButton.Focus(FocusState.Programmatic);
        }

        internal async Task RefreshAsync()
        {
            var list = await Services.Projects.ListAsync();
            ProjectsGrid.ItemsSource = list;
            Empty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Grid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ProjectSummary p) MainPage.OpenStudio(p.Id);
        }

        private async void New_Click(object sender, RoutedEventArgs e)
        {
            var name = await Dialogs.PromptAsync("New project", "Name", "My song " + DateTime.Now.ToString("MMM d"), "Create");
            if (name == null) return;
            var p = await Services.Projects.CreateAsync(string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim());
            MainPage.OpenStudio(p.Id);
        }

        private void Open_Click(object sender, RoutedEventArgs e) =>
            MainPage.OpenStudio((string)((FrameworkElement)sender).Tag);

        private async void Rename_Click(object sender, RoutedEventArgs e)
        {
            var id = (string)((FrameworkElement)sender).Tag;
            var current = (ProjectsGrid.ItemsSource as System.Collections.Generic.List<ProjectSummary>)?.FirstOrDefault(p => p.Id == id);
            var name = await Dialogs.PromptAsync("Rename project", "Name", current?.Name ?? "", "Rename");
            if (string.IsNullOrWhiteSpace(name)) return;
            await Services.Projects.RenameAsync(id, name.Trim());
            await RefreshAsync();
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            var id = (string)((FrameworkElement)sender).Tag;
            if (!await Dialogs.ConfirmAsync("Delete project?", "This removes the project and its recordings from this console. It can't be undone.", "Delete"))
                return;
            await Services.Projects.DeleteAsync(id);
            await RefreshAsync();
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            var id = (string)((FrameworkElement)sender).Tag;
            var drive = await Dialogs.PickDriveAsync();
            if (drive == null) return;
            try
            {
                Status.Text = "Copying to " + drive.Name + "…";
                var dest = await Services.Usb.GetSubFolderAsync(drive, "Projects");
                var file = await Services.Projects.ExportArchiveAsync(id, dest);
                Status.Text = $"Saved {file.Name} to {drive.Name}\\Yursic\\Projects";
            }
            catch (Exception ex)
            {
                Status.Text = "";
                await Dialogs.InfoAsync("Couldn't copy to USB", ex.Message);
            }
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            Status.Text = "Looking for projects on USB…";
            var archives = await Services.Usb.FindProjectArchivesAsync();
            Status.Text = "";
            if (archives.Count == 0)
            {
                await Dialogs.InfoAsync("No projects found",
                    "Put .yproj files in the root of a USB drive or in Yursic\\Projects, then try again.");
                return;
            }
            var pick = await Dialogs.PickAsync("Open from USB", archives);
            if (pick == null) return;
            try
            {
                Status.Text = "Importing…";
                var id = await Services.Projects.ImportArchiveAsync((StorageFile)pick.Item);
                Status.Text = "";
                MainPage.OpenStudio(id);
            }
            catch (Exception ex)
            {
                Status.Text = "";
                await Dialogs.InfoAsync("Couldn't open project", ex.Message);
            }
        }
    }
}
