using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DiscForge.App;

public partial class MainWindow : Window
{
    public MainWindow()
        : this(new MainViewModel(new DiscForge.Core.LoadedDisc(
            string.Empty,
            null,
            null,
            null,
            DiscForge.Core.VerificationPlan.SizeOnly(),
            [],
            "Aperçu")))
    {
    }

    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        Closing += (_, _) => model.Cancel();
        if (!string.IsNullOrEmpty(model.ArtworkPath))
            ArtworkPlaceholder.Visibility = Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var useDark = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 19, ref useDark, sizeof(int));
    }

    private void ArtworkFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ArtworkImage.Visibility = Visibility.Collapsed;
        ArtworkPlaceholder.Visibility = Visibility.Visible;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
