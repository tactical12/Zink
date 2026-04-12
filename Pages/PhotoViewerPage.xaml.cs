using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

// IMPORTANT: use aliases so Zink.Windows doesn't collide with WinRT Windows.*
using WStorage = global::Windows.Storage;
using WPickers = global::Windows.Storage.Pickers;
using WFileProps = global::Windows.Storage.FileProperties;

using WinRT.Interop;

namespace Zink.Pages
{
    public sealed partial class PhotoViewerPage : Page
    {
        public sealed class PhotoItem
        {
            public string Name { get; set; } = "";
            public WStorage.StorageFile? File { get; set; }
            public BitmapImage? Thumbnail { get; set; }
        }

        public ObservableCollection<PhotoItem> Photos { get; } = new();

        public PhotoViewerPage()
        {
            InitializeComponent();
        }

        private async void OpenPhotos_Click(object sender, RoutedEventArgs e)
        {
            await PickAndLoadPhotosAsync();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            Photos.Clear();
            PreviewImage.Source = null;
            EmptyPreviewText.Visibility = Visibility.Visible;
            StatusText.Text = "";
        }

        private async void ThumbsGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is PhotoItem item && item.File != null)
            {
                await ShowPreviewAsync(item.File);
            }
        }

        private async Task PickAndLoadPhotosAsync()
        {
            try
            {
                var picker = new WPickers.FileOpenPicker
                {
                    SuggestedStartLocation = WPickers.PickerLocationId.PicturesLibrary,
                    ViewMode = WPickers.PickerViewMode.Thumbnail
                };

                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".gif");
                picker.FileTypeFilter.Add(".webp");
                picker.FileTypeFilter.Add(".tif");
                picker.FileTypeFilter.Add(".tiff");

                // WinUI 3: initialize picker with window handle
                try
                {
                    var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                    InitializeWithWindow.Initialize(picker, hwnd);
                }
                catch
                {
                    // keep resilient
                }

                var files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0)
                {
                    StatusText.Text = "No photos selected.";
                    return;
                }

                Photos.Clear();

                foreach (var f in files)
                {
                    var item = new PhotoItem
                    {
                        Name = f.Name,
                        File = f,
                        Thumbnail = await CreateThumbnailAsync(f)
                    };

                    Photos.Add(item);
                }

                StatusText.Text = $"{Photos.Count} photo(s) loaded.";

                if (Photos.Count > 0 && Photos[0].File != null)
                    await ShowPreviewAsync(Photos[0].File);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to load photos: {ex.Message}";
            }
        }

        private static async Task<BitmapImage?> CreateThumbnailAsync(WStorage.StorageFile file)
        {
            try
            {
                using var thumb = await file.GetThumbnailAsync(WFileProps.ThumbnailMode.PicturesView, 512);
                if (thumb == null) return null;

                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumb);
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private async Task ShowPreviewAsync(WStorage.StorageFile file)
        {
            try
            {
                using var stream = await file.OpenReadAsync();

                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(stream);

                PreviewImage.Source = bmp;
                EmptyPreviewText.Visibility = Visibility.Collapsed;

                StatusText.Text = file.Name;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to preview: {ex.Message}";
            }
        }
    }
}
