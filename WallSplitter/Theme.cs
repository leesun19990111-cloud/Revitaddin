using System.Windows.Media;

namespace WallSplitter
{
    // 여러 창의 코드비하인드가 행을 코드로 생성하며 Brushes.Black/Gray 등을 하드코딩해 왔는데,
    // 라이트 테마 배경에서도 색이 묻히지 않도록 Resources/Theme.xaml의 색상값과 반드시 맞춰서
    // 유지해야 한다(XAML 리소스와 이 클래스는 서로 다른 파일이라 자동으로 동기화되지 않음).
    // 2026-07-27: "Industry" 디자인 시스템(Claude Design 제작)으로 전면 교체 - Theme.xaml의 같은 이름
    // 토큰과 값을 맞췄다. 자세한 값 선정 근거는 Theme.xaml 상단 주석 참고.
    internal static class Theme
    {
        public static readonly SolidColorBrush TextPrimary = Freeze("#1D1F20");
        public static readonly SolidColorBrush TextSecondary = Freeze("#8C1D1F20");
        public static readonly SolidColorBrush Border = Freeze("#291D1F20");
        public static readonly SolidColorBrush WarningText = Freeze("#A67B3D");
        public static readonly SolidColorBrush DangerText = Freeze("#A6595D");
        // 빠른 토글 버튼의 on 상태 아이콘 색 (off는 TextSecondary 재사용) - Theme.xaml의 ToggleOnBrush와 맞춰 유지.
        public static readonly SolidColorBrush ToggleOn = Freeze("#3D8F5C");
        public static readonly SolidColorBrush ToggleDisabled = Freeze("#98989B");
        // 목록에서 현재 선택된 행의 배경 - Theme.xaml의 SelectionBrush(#4D5980A6)와 맞춰 유지.
        public static readonly SolidColorBrush SelectionHighlight = Freeze("#4D5980A6");

        private static SolidColorBrush Freeze(string hex)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
