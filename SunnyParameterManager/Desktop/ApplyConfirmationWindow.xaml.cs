using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SunnyParameterManager.Desktop;

public partial class ApplyConfirmationWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;

    public ApplyConfirmationWindow(Window owner, int modelCount, string details)
    {
        InitializeComponent();
        Owner = owner;
        ModelCountText.Text = $"변경 대상 모델 {modelCount:N0}개";
        DetailsText.Text = details;
        FitOwnerScreen(owner);
    }

    private void Confirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void FitOwnerScreen(Window owner)
    {
        double width = SystemParameters.WorkArea.Width;
        double height = SystemParameters.WorkArea.Height;
        var source = PresentationSource.FromVisual(owner);
        if (source is HwndSource hwnd && hwnd.CompositionTarget is { } target)
        {
            var monitor = MonitorFromWindow(hwnd.Handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var area = target.TransformFromDevice.Transform(
                    new Vector(info.WorkRight - info.WorkLeft, info.WorkBottom - info.WorkTop));
                width = area.X;
                height = area.Y;
            }
        }
        Width = Math.Min(Width, width - 24);
        Height = Math.Min(Height, height - 24);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
    }
}
