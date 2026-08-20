using Microsoft.Win32;
using ChunkPilot.App.DesignSystem;

namespace ChunkPilot.App;

public partial class ServerIconCropWindow : Window
{
    internal const string ImageFilter =
        "Images (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff";

    private string sourcePath = "";
    public ServerIconCropSelection? Selection { get; private set; }

    public ServerIconCropWindow(string sourcePath)
    {
        InitializeComponent();
        AppWindowChrome.Apply(this);
        CropSurface.CropChanged += (_, _) => RefreshPreview();
        if (!TryLoadSource(sourcePath))
            throw new InvalidOperationException(ImageError.Message);
    }

    private bool TryLoadSource(string path)
    {
        try
        {
            var bitmap = ServerIconImageLoader.LoadDetached(path) ??
                         throw new InvalidDataException("The selected file did not contain a readable image.");
            sourcePath = path;
            CropSurface.Source = bitmap;
            CropSurface.Fit();
            ImageError.Visibility = Visibility.Collapsed;
            ApplyButton.IsEnabled = true;
            RefreshPreview();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or NotSupportedException or InvalidDataException
                                           or SixLabors.ImageSharp.UnknownImageFormatException)
        {
            ImageError.Message = exception.Message;
            ImageError.Visibility = Visibility.Visible;
            ApplyButton.IsEnabled = CropSurface.Source is not null;
            return false;
        }
    }

    private void RefreshPreview() => PreviewImage.Source = ServerIconImageLoader.CreateCropPreview(
        CropSurface.Source as System.Windows.Media.Imaging.BitmapSource,
        CropSurface.CropX, CropSurface.CropY, CropSurface.CropSize);

    private void Fit_Click(object sender, RoutedEventArgs e) => CropSurface.Fit();

    private void Reset_Click(object sender, RoutedEventArgs e) => CropSurface.Fit();

    private void ChooseAnother_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a server icon",
            Filter = ImageFilter,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            _ = TryLoadSource(dialog.FileName);
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        Selection = new ServerIconCropSelection(
            sourcePath, CropSurface.CropX, CropSurface.CropY, CropSurface.CropSize);
        DialogResult = true;
    }
}
