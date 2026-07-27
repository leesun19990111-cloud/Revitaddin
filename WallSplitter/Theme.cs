using System.Windows.Media;

namespace WallSplitter
{
    // 여러 창의 코드비하인드가 행을 코드로 생성하며 Brushes.Black/Gray 등을 하드코딩해 왔는데,
    // 다크 테마에서는 검은 텍스트가 배경에 묻혀 버린다. Resources/Theme.xaml의 색상값과 반드시
    // 맞춰서 유지해야 한다(XAML 리소스와 이 클래스는 서로 다른 파일이라 자동으로 동기화되지 않음).
    internal static class Theme
    {
        public static readonly SolidColorBrush TextPrimary = Freeze("#ECEDF0");
        public static readonly SolidColorBrush TextSecondary = Freeze("#9498A3");
        public static readonly SolidColorBrush Border = Freeze("#3A3C44");
        public static readonly SolidColorBrush WarningText = Freeze("#E8A33D");
        public static readonly SolidColorBrush DangerText = Freeze("#FF6B6B");
        // 빠른 토글 버튼의 on 상태 아이콘 색 (off는 TextSecondary 재사용) - Theme.xaml의 ToggleOnBrush와 맞춰 유지.
        public static readonly SolidColorBrush ToggleOn = Freeze("#3DDC84");
        public static readonly SolidColorBrush ToggleDisabled = Freeze("#55585F");
        // 목록에서 현재 선택된 행의 배경 - Theme.xaml의 SelectionBrush(#3D5B8DEF)와 맞춰 유지.
        public static readonly SolidColorBrush SelectionHighlight = Freeze("#3D5B8DEF");

        private static SolidColorBrush Freeze(string hex)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
