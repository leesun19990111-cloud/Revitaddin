using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using WpfApplication = System.Windows.Application;

namespace WallSplitter
{
    // 외부 도구(External Tools) 드롭다운 대신, 상단에 전용 리본 탭 + 패널 + 버튼을 만들어 등록한다.
    public class App : IExternalApplication
    {
        private const string TabName = "Sunny Tools";
        private const string PanelName = "벽체 분리";
        private const string FloorPanelName = "바닥 분리";
        private const string NamerPanelName = "NAMER";
        private const string MaterialPanelName = "재료 지정";

        // "단일/복수" 토글 버튼의 표시 텍스트를 ToggleTypeAssignmentPersistenceCommand가 클릭 후 갱신하기 위한 참조.
        // 벽체 분리/바닥 분리 패널 양쪽에 각각 하나씩 올라가므로(설정은 완전히 공유) 두 버튼 모두 갱신해야 한다.
        private static readonly List<PushButton> _typeAssignmentToggleButtons = new List<PushButton>();

        public Result OnStartup(UIControlledApplication application)
        {
            EnsureWpfApplication();

            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch
            {
                // 이미 같은 이름의 탭이 있으면(재로드 등) 무시하고 그대로 사용
            }

            RibbonPanel panel = application.GetRibbonPanels(TabName).Find(p => p.Name == PanelName)
                ?? application.CreateRibbonPanel(TabName, PanelName);

            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            PushButtonData buttonData = new PushButtonData(
                "WallSplitter_SplitWall",
                "벽체\n분리",
                assemblyPath,
                typeof(SplitWallCommand).FullName);

            if (panel.AddItem(buttonData) is PushButton button)
            {
                button.ToolTip = "복합 벽을 레이어별 단일 벽으로 분리합니다.\n미리 벽을 선택해 둔 상태로 누르면 그 벽들을 바로 분리하고, 아무것도 선택하지 않은 상태로 누르면 벽을 직접 고를 수 있습니다.";
                button.LargeImage = LoadIcon("WallSplitter.Resources.icon32.png");
                button.Image = LoadIcon("WallSplitter.Resources.icon16.png");
            }

            // "설정"/"단일·복수" 토글은 완전히 공유되는 하나의 설정이지만, 벽체 분리 패널에서만 접근할 수 있으면
            // 바닥 분리 작업 중엔 안 보여서 "바닥 분리에는 설정이 안 붙어 있다"고 느껴질 수 있어 두 패널에 각각 붙인다.
            NamingSettings currentSettings = NamingSettings.Load();
            AddSettingsStack(panel, assemblyPath, "", currentSettings);

            RibbonPanel floorPanel = application.GetRibbonPanels(TabName).Find(p => p.Name == FloorPanelName)
                ?? application.CreateRibbonPanel(TabName, FloorPanelName);

            PushButtonData floorButtonData = new PushButtonData(
                "WallSplitter_SplitFloor",
                "바닥\n분리",
                assemblyPath,
                typeof(SplitFloorCommand).FullName);

            if (floorPanel.AddItem(floorButtonData) is PushButton floorButton)
            {
                floorButton.ToolTip = "복합 바닥을 레이어별 단일 바닥으로 분리합니다 (벽체 분리와 이름/유형 지정 방식을 공유합니다).\n미리 바닥을 선택해 둔 상태로 누르면 그 바닥들을 바로 분리하고, 아무것도 선택하지 않은 상태로 누르면 바닥을 직접 고를 수 있습니다.";
                floorButton.LargeImage = LoadIcon("WallSplitter.Resources.icon_floor32.png");
                floorButton.Image = LoadIcon("WallSplitter.Resources.icon_floor16.png");
            }

            AddSettingsStack(floorPanel, assemblyPath, "_Floor", currentSettings);

            RibbonPanel namerPanel = application.GetRibbonPanels(TabName).Find(p => p.Name == NamerPanelName)
                ?? application.CreateRibbonPanel(TabName, NamerPanelName);

            PushButtonData namerButtonData = new PushButtonData(
                "WallSplitter_Namer",
                "NAMER",
                assemblyPath,
                typeof(NamerCommand).FullName);

            if (namerPanel.AddItem(namerButtonData) is PushButton namerButton)
            {
                namerButton.ToolTip = "뷰/시트/패밀리/유형의 이름을 한 번에 바꿉니다 (문자열 치환, 위치에 삽입, 구분자 기준 자리바꾸기).\n미리 요소를 선택해 둔 상태로 누르면 해당 항목이 먼저 체크되어 있습니다.";
                namerButton.LargeImage = LoadIcon("WallSplitter.Resources.icon_namer32.png");
                namerButton.Image = LoadIcon("WallSplitter.Resources.icon_namer16.png");
            }

            RibbonPanel materialPanel = application.GetRibbonPanels(TabName).Find(p => p.Name == MaterialPanelName)
                ?? application.CreateRibbonPanel(TabName, MaterialPanelName);

            PushButtonData materialButtonData = new PushButtonData(
                "WallSplitter_MaterialAssign",
                "재료\n지정",
                assemblyPath,
                typeof(MaterialAssignCommand).FullName);

            if (materialPanel.AddItem(materialButtonData) is PushButton materialButton)
            {
                materialButton.ToolTip = "여러 유형을 한 번에 선택해서 재료를 일괄 지정합니다.\n벽/바닥/지붕/천장처럼 레이어가 하나뿐인 유형은 그 레이어의 재료를, 그 외 유형은 '재료' 파라미터를 바꿉니다.\n미리 유형(또는 그 유형의 인스턴스)을 선택해 둔 상태로 누르면 해당 유형이 먼저 체크되어 있습니다.";
                materialButton.LargeImage = LoadIcon("WallSplitter.Resources.icon_material32.png");
                materialButton.Image = LoadIcon("WallSplitter.Resources.icon_material16.png");
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        // "설정" 버튼 바로 밑에 작은 "단일/복수" 토글 버튼을 쌓아서(stacked) 붙인다.
        // '유형 직접 지정' 모드에서 지정한 유형을 다음 벽/바닥에도 이어서 쓸지(복수) 매번 새로 지정할지(단일)
        // 클릭 한 번으로 전환한다 - 별도 창을 열 필요가 없도록 리본에 직접 노출.
        // idSuffix는 같은 명령을 가리키는 버튼을 여러 패널(벽체 분리/바닥 분리)에 중복 등록하기 위한 구분자
        // - PushButtonData의 internal name은 패널이 달라도 애플리케이션 전체에서 유일해야 하기 때문이다.
        private static void AddSettingsStack(RibbonPanel targetPanel, string assemblyPath, string idSuffix, NamingSettings currentSettings)
        {
            PushButtonData settingsButtonData = new PushButtonData(
                "WallSplitter_Settings" + idSuffix,
                "설정",
                assemblyPath,
                typeof(SettingsCommand).FullName)
            {
                ToolTip = "단일 벽/바닥 유형 이름 형식/지정 방식을 설정합니다 (벽체 분리·바닥 분리가 공유). 한 번 저장하면 계속 적용됩니다.",
                Image = LoadIcon("WallSplitter.Resources.icon_settings16.png"),
            };

            PushButtonData toggleButtonData = new PushButtonData(
                "WallSplitter_ToggleTypeAssignment" + idSuffix,
                ToggleLabel(currentSettings.TypeAssignmentPersistence),
                assemblyPath,
                typeof(ToggleTypeAssignmentPersistenceCommand).FullName)
            {
                ToolTip = "'유형 직접 지정' 모드에서, 지정한 유형을 다음 벽/바닥에도 이어서 적용할지(복수) 매번 새로 지정할지(단일) 전환합니다 (벽체 분리·바닥 분리가 공유).",
                Image = LoadIcon("WallSplitter.Resources.icon_toggle16.png"),
            };

            IList<RibbonItem> stackedItems = targetPanel.AddStackedItems(settingsButtonData, toggleButtonData);
            if (stackedItems.Count == 2 && stackedItems[1] is PushButton toggleButton)
                _typeAssignmentToggleButtons.Add(toggleButton);
        }

        private static string ToggleLabel(TypeAssignmentPersistence mode) =>
            mode == TypeAssignmentPersistence.Multiple ? "복수" : "단일";

        // ToggleTypeAssignmentPersistenceCommand가 설정을 바꾼 직후 호출해 리본 버튼 텍스트를 갱신한다.
        // 벽체 분리/바닥 분리 패널 양쪽 토글 버튼 모두 같은 설정을 가리키므로 둘 다 갱신해야 한다.
        internal static void UpdateTypeAssignmentToggleLabel(TypeAssignmentPersistence mode)
        {
            foreach (PushButton toggleButton in _typeAssignmentToggleButtons)
                toggleButton.ItemText = ToggleLabel(mode);
        }

        // Revit은 WPF 기반이 아니라서 프로세스 안에 System.Windows.Application이 하나도 없다.
        // DataGrid처럼 기본 테마 리소스(Aero2 등)에 의존하는 복잡한 컨트롤은 Application이 없으면
        // "Baml2006.TypeConverterMarkupExtension에 대한 값 제공에서 예외가 발생했습니다" 같은 알 수 없는
        // 오류로 창 생성 자체가 실패한다(Button/TextBox 같은 단순 컨트롤만 쓰는 SettingsWindow는 문제없었음).
        // Run()은 호출하지 않고 인스턴스만 만들어 리소스 조회 인프라를 부팅한다.
        private static void EnsureWpfApplication()
        {
            if (WpfApplication.Current != null) return;
            _ = new WpfApplication { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        }

        // 리소스로 포함된 PNG 아이콘을 리본 버튼용 BitmapSource로 로드한다.
        private static BitmapSource? LoadIcon(string resourceName)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
    }
}
