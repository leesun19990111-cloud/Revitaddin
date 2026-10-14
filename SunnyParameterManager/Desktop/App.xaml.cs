using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SunnyParameterManager.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        if (e.Args.Length == 2 && e.Args[0] == "--render-preview")
        {
            // 개발용 렌더: Revit을 실행하거나 실제 RVT에 접근하지 않는다.
            window.LoadSample();
            window.Width = 1320; window.Height = 820;
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1320, 820)); content.Arrange(new Rect(0, 0, 1320, 820)); content.UpdateLayout();
            var image = new RenderTargetBitmap(1320, 820, 96, 96, PixelFormats.Pbgra32);
            image.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(e.Args[1])) encoder.Save(stream);
            Shutdown(); return;
        }
        window.Show();
    }
}
