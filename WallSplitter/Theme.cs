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
        // 위/아래 이동·삭제처럼 작은 아이콘 버튼에 명확한 배경을 줄 때 씀 - Theme.xaml의 SurfaceBrush와 맞춰 유지.
        public static readonly SolidColorBrush Surface = Freeze("#E9E9EA");
        public static readonly SolidColorBrush TextSecondary = Freeze("#8C1D1F20");
        public static readonly SolidColorBrush Border = Freeze("#291D1F20");
        public static readonly SolidColorBrush WarningText = Freeze("#A67B3D");
        public static readonly SolidColorBrush DangerText = Freeze("#A6595D");
        // 빠른 토글 버튼의 on 상태 아이콘 색 (off는 TextSecondary 재사용) - Theme.xaml의 ToggleOnBrush와 맞춰 유지.
        public static readonly SolidColorBrush ToggleOn = Freeze("#3D8F5C");
        public static readonly SolidColorBrush ToggleDisabled = Freeze("#98989B");
        // 목록에서 현재 선택된 행의 배경 - Theme.xaml의 SelectionBrush(#4D5980A6)와 맞춰 유지.
        public static readonly SolidColorBrush SelectionHighlight = Freeze("#4D5980A6");
        // 강조색(채워진 배경) 위에 놓이는 밝은 전경색 - Theme.xaml의 OnAccentBrush와 맞춰 유지.
        // QuickToggleIcons.ContrastingForeground가 어두운 배경 위 전경색으로 이걸 반환한다.
        public static readonly SolidColorBrush OnAccent = Freeze("#F2F2F3");
        // 강조색 자체(스틸블루) - Theme.xaml의 AccentBrush와 맞춰 유지. Industry 디자인 시스템은 강조색으로
        // "채워진" 오브젝트를 주 버튼 등 소수로 제한하므로, 코드로 만드는 단계 번호 뱃지/선택 테두리처럼
        // 그 역할에 해당하는 곳에만 쓴다.
        public static readonly SolidColorBrush Accent = Freeze("#5980A6");
        // 실제로 칠해진 구분선 - 반투명 Border로는 존재감이 옅은 자리에 쓴다. Theme.xaml의 DividerSolidBrush와 맞춰 유지.
        public static readonly SolidColorBrush Divider = Freeze("#D4D4D7");
        // 창 바탕(종이색) - Theme.xaml의 WindowBackgroundBrush와 맞춰 유지.
        public static readonly SolidColorBrush WindowBackground = Freeze("#F2F2F3");

        private static SolidColorBrush Freeze(string hex)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
