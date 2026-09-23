using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
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
        private const string ModelSyncPanelName = "모델간 변경 반영";
        private const string PatternPanelName = "패턴";
        private const string QuickTogglePanelName = "커스텀 버튼";
        private const string WarningPickPanelName = "경고Pick";
        private const string RoomSeparatorPanelName = "룸 경계";
        private const string ParamCombinePanelName = "매개변수 조합";
        private const string BatchJoinPanelName = "일괄 결합금지/허용";

        // "단일/복수" 토글 버튼의 표시 텍스트를 ToggleTypeAssignmentPersistenceCommand가 클릭 후 갱신하기 위한 참조.
        // 벽체 분리/바닥 분리 패널 양쪽에 각각 하나씩 올라가므로(설정은 완전히 공유) 두 버튼 모두 갱신해야 한다.
        private static readonly List<PushButton> _typeAssignmentToggleButtons = new List<PushButton>();

        // 빠른 토글 툴바 "표시/숨김" 리본 버튼의 표시 텍스트 갱신용 (QuickToggleVisibilityToggleCommand가 클릭 후 호출).
        private static readonly List<PushButton> _quickToggleVisibilityButtons = new List<PushButton>();

        // "매개변수 조합" 패널의 실시간 ON/OFF 토글 버튼 참조 (위 두 토글과 같은 이유 - 이미 만들어진 리본
        // 항목의 라벨/아이콘은 이 참조로만 나중에 갱신할 수 있다).
        private static readonly List<PushButton> _paramCombineToggleButtons = new List<PushButton>();

        // 빠른 토글 커스텀 툴바(QuickToggleToolbar)는 세션 내내 떠 있는 모드리스 창이라, 버튼 클릭 시점에
        // Revit API 컨텍스트가 열려있지 않다 - ExternalEvent로 요청을 넣어 Revit이 다음 기회에 실행하게 한다
        // (이 프로젝트에서 ExternalEvent를 쓰는 첫 사례. 기존 창들은 전부 커맨드 Execute 안의 모달
        // ShowDialog()라 이런 비동기 콜백이 필요 없었다).
        internal static ExternalEvent? QuickToggleEvent;
        internal static QuickToggleExternalEventHandler? QuickToggleHandler;

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
                button.LargeImage = RibbonIcons.SplitWall(32);
                button.Image = RibbonIcons.SplitWall(16);
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
                floorButton.LargeImage = RibbonIcons.SplitFloor(32);
                floorButton.Image = RibbonIcons.SplitFloor(16);
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
                namerButton.LargeImage = RibbonIcons.Namer(32);
                namerButton.Image = RibbonIcons.Namer(16);
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
                materialButton.ToolTip = "여러 유형을 한 번에 선택해서 재료를 일괄 지정합니다.\n벽/바닥/지붕/천장은 두께가 있는 레이어마다, 그 외 유형은 재료 파라미터마다 각각 지정할 수 있습니다(하나의 유형이 재료를 여러 개 동시에 쓰면 슬롯별로 행이 나뉘어 보입니다).\n미리 유형(또는 그 유형의 인스턴스)을 선택해 둔 상태로 누르면 해당 유형의 모든 슬롯이 먼저 체크되어 있습니다.";
                materialButton.LargeImage = RibbonIcons.MaterialAssign(32);
                materialButton.Image = RibbonIcons.MaterialAssign(16);
            }

            RibbonPanel modelSyncPanel = application.GetRibbonPanels(TabName).Find(p => p.Name == ModelSyncPanelName)
                ?? application.CreateRibbonPanel(TabName, ModelSyncPanelName);

            PushButtonData modelSyncButtonData = new PushButtonData(
                "WallSplitter_ModelSync",
                "모델간\n변경 반영",
                assemblyPath,
                typeof(ModelSyncCommand).FullName);

            if (modelSyncPanel.AddItem(modelSyncButtonData) is PushButton modelSyncButton)
            {
                modelSyncButton.ToolTip = "NAMER/재료 지정에서 최종 적용한 변경사항을 다른 중앙모델에도 그대로 재현합니다.\n이름이 같은 대상을 자동으로 찾아 적용하고, 모호하면 직접 고르는 창이 뜹니다.\n파일로 내보내/가져오거나, 같은 세션에 열려 있는 다른 문서에 바로 적용할 수 있습니다.";
                modelSyncButton.LargeImage = RibbonIcons.ModelSync(32);
                modelSyncButton.Image = RibbonIcons.ModelSync(16);
            }

            RibbonPanel patternPanel = application.GetRibbonPanels(TabName).Find(p => p.Name == PatternPanelName)
                ?? application.CreateRibbonPanel(TabName, PatternPanelName);

            PushButtonData patternButtonData = new PushButtonData(
                "WallSplitter_PatternStudio",
                "패턴\n스튜디오",
                assemblyPath,
                typeof(PatternStudioCommand).FullName);

            if (patternPanel.AddItem(patternButtonData) is PushButton patternButton)
            {
                patternButton.ToolTip = "기존 Revit 채우기 패턴이나 PAT 파일을 불러와 전체/선군별 회전, 스케일, 폭·높이, 간격을 자유롭게 조절하고 새 패턴으로 저장합니다.";
                patternButton.LargeImage = RibbonIcons.PatternStudio(32);
                patternButton.Image = RibbonIcons.PatternStudio(16);
            }

            PushButtonData captureButtonData = new PushButtonData(
                "WallSplitter_ModelLinePatternCapture",
                "모델선 캡처",
                assemblyPath,
                typeof(ModelLinePatternCaptureCommand).FullName)
            {
                ToolTip = "현재 평면·입면·단면에서 모델선 또는 상세선으로 그린 한 단위를 패턴으로 가져옵니다. 첫 모서리→첫 변 끝→둘째 변 끝을 ㄱ자 순서로 지정해 직사각형 범위를 만들며 Revit 기본 스냅 표식을 그대로 사용합니다.",
                Image = RibbonIcons.ModelLineCapture(16),
            };
            PushButtonData punchButtonData = new PushButtonData(
                "WallSplitter_PatternPunch",
                "패턴 타공",
                assemblyPath,
                typeof(PatternPunchCommand).FullName)
            {
                ToolTip = "패턴이 표시된 벽·바닥·천장·커튼패널 면을 고르고, 패턴의 폐영역 하나를 선택해 같은 영역을 전체 반복 타공합니다. 경계에 걸린 타공은 호스트 경계에 맞춰 잘립니다.",
                Image = RibbonIcons.PatternPunch(16, true),
            };
            PushButtonData restorePunchButtonData = new PushButtonData(
                "WallSplitter_PatternPunchRestore",
                "타공 복원",
                assemblyPath,
                typeof(PatternPunchRestoreCommand).FullName)
            {
                ToolTip = "선택한 호스트에서 Sunny Tools로 실행한 가장 최근 패턴 타공 1회를 안전하게 복원합니다. 타공 뒤 프로파일이 달라졌으면 자동 덮어쓰기를 중단합니다.",
                Image = RibbonIcons.PatternPunch(16, false),
            };
            foreach (RibbonItem patternStacked in patternPanel.AddStackedItems(captureButtonData, punchButtonData, restorePunchButtonData))
                RegisterRibbonCommandId(patternPanel, patternStacked);

            RibbonPanel quickTogglePanel = application.GetRibbonPanels(TabName).Find(p => p.Name == QuickTogglePanelName)
                ?? application.CreateRibbonPanel(TabName, QuickTogglePanelName);
            AddQuickToggleStack(quickTogglePanel, assemblyPath);

            RibbonPanel warningPickPanel = application.GetRibbonPanels(TabName).Find(p => p.Name == WarningPickPanelName)
                ?? application.CreateRibbonPanel(TabName, WarningPickPanelName);

            PushButtonData warningPickButtonData = new PushButtonData(
                "WallSplitter_WarningPick",
                "경고\nPick",
                assemblyPath,
                typeof(WarningPickCommand).FullName);

            if (warningPickPanel.AddItem(warningPickButtonData) is PushButton warningPickButton)
            {
                warningPickButton.ToolTip = "현재 문서의 경고에 걸린 요소를 모아 보여주고, 고르면 그 요소가 있는 뷰로 이동하면서 바로 선택됩니다.\nRevit 기본 경고창의 '표시'와 달리 요소를 직접 찾아 클릭할 필요가 없습니다. 창을 열어 둔 채로 모델을 계속 조작할 수 있습니다.";
                warningPickButton.LargeImage = RibbonIcons.WarningPick(32);
                warningPickButton.Image = RibbonIcons.WarningPick(16);
            }

            RibbonPanel roomSeparatorPanel = application.GetRibbonPanels(TabName).Find(p => p.Name == RoomSeparatorPanelName)
                ?? application.CreateRibbonPanel(TabName, RoomSeparatorPanelName);

            PushButtonData roomSeparatorButtonData = new PushButtonData(
                "WallSplitter_RoomSeparator",
                "룸\n구분선",
                assemblyPath,
                typeof(RoomSeparatorCommand).FullName);

            if (roomSeparatorPanel.AddItem(roomSeparatorButtonData) is PushButton roomSeparatorButton)
            {
                roomSeparatorButton.ToolTip = "고른 벽 유형의 벽 중심선을 따라 룸 구분선을 자동으로 만듭니다.\n링크된 모델의 벽도 대상으로 삼을 수 있고, 벽의 위치선이 마감면·코어면이어도 벽 두께를 계산해 실제 중심선에 맞춥니다. 레벨은 여러 개를 한 번에 고를 수 있습니다.";
                roomSeparatorButton.LargeImage = RibbonIcons.RoomSeparator(32);
                roomSeparatorButton.Image = RibbonIcons.RoomSeparator(16);
            }

            PushButtonData roomBoundingButtonData = new PushButtonData(
                "WallSplitter_RoomBounding",
                "룸경계\nON/OFF",
                assemblyPath,
                typeof(RoomBoundingCommand).FullName);

            if (roomSeparatorPanel.AddItem(roomBoundingButtonData) is PushButton roomBoundingButton)
            {
                roomBoundingButton.ToolTip = "벽·바닥·지붕·기초처럼 방의 경계를 만드는 요소들의 '룸 경계'(Room Bounding) 속성을 카테고리 단위로 한 번에 켜고 끕니다.\n목록은 모델을 훑어 룸 경계 속성을 실제로 가진 카테고리만 모으므로, 버전에 따라 늘어나는 카테고리도 그대로 나옵니다. RVT 링크가 방 경계를 만드는지도 여기서 끄고 켤 수 있습니다.";
                roomBoundingButton.LargeImage = RibbonIcons.RoomBounding(32);
                roomBoundingButton.Image = RibbonIcons.RoomBounding(16);
            }

            RibbonPanel paramCombinePanel = application.GetRibbonPanels(TabName).Find(p => p.Name == ParamCombinePanelName)
                ?? application.CreateRibbonPanel(TabName, ParamCombinePanelName);

            PushButtonData paramCombineButtonData = new PushButtonData(
                "WallSplitter_ParamCombine",
                "매개변수\n조합",
                assemblyPath,
                typeof(ParamCombineCommand).FullName);

            if (paramCombinePanel.AddItem(paramCombineButtonData) is PushButton paramCombineButton)
            {
                paramCombineButton.ToolTip = "여러 매개변수(예: 용도+지상지하+층+번호)를 정해진 자리수로 채워 하나로 합친 뒤, 결과 매개변수(예: 실번호)에 실제 값으로 써넣습니다.\n일람표의 '결합된 매개변수'와 달리 요소 자체에 저장되는 진짜 데이터이고, 실시간을 켜두면 소스 값을 고치는 순간 결과도 같이 바뀝니다(Dynamo처럼 다시 Run할 필요가 없습니다).";
                paramCombineButton.LargeImage = RibbonIcons.ParamCombine(32);
                paramCombineButton.Image = RibbonIcons.ParamCombine(16);
            }

            AddParamCombineStack(paramCombinePanel, assemblyPath);

            RibbonPanel batchJoinPanel = application.GetRibbonPanels(TabName).Find(p => p.Name == BatchJoinPanelName)
                ?? application.CreateRibbonPanel(TabName, BatchJoinPanelName);

            PushButtonData batchJoinButtonData = new PushButtonData(
                "WallSplitter_BatchJoin",
                "결합\n금지·허용",
                assemblyPath,
                typeof(BatchJoinCommand).FullName);

            if (batchJoinPanel.AddItem(batchJoinButtonData) is PushButton batchJoinButton)
            {
                batchJoinButton.ToolTip = "벽·보·가새의 끝이 맞닿은 부재와 결합되는 것을 한 번에 금지하거나 다시 허용합니다.\nRevit에서는 부재 끝을 하나씩 오른쪽 클릭해 '결합 허용 안 함'을 걸어야 하지만, 여기서는 고른 요소 전부나 고른 유형의 모든 인스턴스에 한 번에 겁니다. 양쪽 끝/한쪽 끝을 골라 적용할 수 있습니다.";
                batchJoinButton.LargeImage = RibbonIcons.BatchJoin(32, true);
                batchJoinButton.Image = RibbonIcons.BatchJoin(16, true);
            }

            AddBatchJoinStack(batchJoinPanel, assemblyPath);

            // 리본을 다 만든 뒤 한 번 훑어 "명령 클래스 → Revit 명령 id" 표를 채운다 - 커스텀 "기능 버튼"이
            // 이 id로 PostCommand한다(왜 클래스 이름으로는 안 되는지는 SunnyToolsCommands.RibbonCommandIds의
            // CONFIRMED LIVE BUG 주석 참고). 새 리본 버튼을 추가해도 여기서 자동으로 잡히므로 별도 표를
            // 손으로 유지할 필요가 없다.
            RegisterRibbonCommandIds(application);

            // 빠른 토글 커스텀 툴바: 실제 Revit 신속접근 도구모음(QAT)에는 API로 버튼을 추가할 수 없어
            // Revit 메인 창 상단에 고정되는 자체 플로팅 창으로 대체 구현했다 (CLAUDE.md 참고).
            // ExternalEvent는 OnStartup에서 바로 생성할 수 있지만(유효한 컨텍스트), MainWindowHandle/
            // ActiveUIDocument에 접근하려면 완전한 UIApplication이 필요하다 - UIControlledApplication에는
            // 그 인스턴스를 직접 만들 방법이 없다(ControlledApplication은 별개 타입이라 UIApplication
            // 생성자에 넘길 수 없음 - 실제로 빌드해서 확인). Revit이 세션 중 이 이벤트들을 실제로 발생시킬
            // 때는 sender로 살아있는 UIApplication을 넘겨주므로, 그 첫 기회(OnQuickToggleViewActivated)에
            // 지연 생성한다.
            QuickToggleHandler = new QuickToggleExternalEventHandler();
            QuickToggleEvent = ExternalEvent.Create(QuickToggleHandler);

            application.ViewActivated += OnQuickToggleViewActivated;
            application.ControlledApplication.DocumentClosing += OnDocumentClosing;
            application.Idling += OnQuickToggleIdling;

            // 경고Pick 창이 열려 있는 동안 경고가 생기거나 사라지면 새로고침을 누르지 않아도 목록이 따라
            // 바뀌게 한다(2026-09-03 요청). DocumentChanged는 트랜잭션이 커밋될 때마다 한 번씩만 발생하므로
            // Idling(초당 여러 번)보다 훨씬 적합하다 - 경고는 모델이 바뀔 때만 달라지기 때문이다.
            application.ControlledApplication.DocumentChanged += OnWarningPickDocumentChanged;

            // "매개변수 조합"의 실시간 반영은 DocumentChanged가 아니라 Revit의 Dynamic Model Update(IUpdater)로
            // 한다 - DocumentChanged는 트랜잭션이 **끝난 뒤** 알려주는 읽기 전용 알림이라 거기서 모델을 고칠 수
            // 없지만, IUpdater는 사용자의 그 트랜잭션 **안에서** 불려 결과 매개변수를 같이 고칠 수 있다.
            // 그래서 Ctrl+Z 한 번이면 소스 값과 결과 값이 함께 되돌아간다(ParamCombineUpdater 주석 참고).
            ParamCombineUpdater.Register(application.ActiveAddInId);
            application.ControlledApplication.DocumentOpened += OnParamCombineDocumentOpened;
            application.ControlledApplication.DocumentCreated += OnParamCombineDocumentCreated;

            return Result.Succeeded;
        }

        // Revit 이벤트 콜백이므로 예외가 밖으로 새면 안 된다(이 파일의 다른 콜백들과 같은 방침).
        // 여기서는 무거운 조회를 하지 않고 창에 "다시 읽어라"는 요청만 넘긴다 - 실제 조회는 기존
        // "새로고침"과 똑같이 WarningPickExternalEventHandler가 유효한 컨텍스트에서 수행한다.
        private static void OnWarningPickDocumentChanged(object? sender, DocumentChangedEventArgs e)
        {
            try { WarningPickWindow.Instance?.RequestLiveRefresh(e.GetDocument()); }
            catch
            {
                // 무시 - 실패해도 사용자가 "새로고침"으로 언제든 직접 갱신할 수 있다.
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            // 업데이터는 Revit 세션 전체에 등록되는 것이라 애드인이 내려갈 때 같이 풀어준다.
            ParamCombineUpdater.Unregister();
            return Result.Succeeded;
        }

        // IUpdater의 "트리거"(어떤 문서의 어떤 카테고리를 감시할지)는 문서 단위라, 문서가 열리거나 새로
        // 만들어질 때마다 현재 설정대로 다시 붙여야 한다. 문서 이벤트 구독을 여기(OnStartup의
        // ControlledApplication)에서만 하는 이유는 아래 OnDocumentClosing 주석과 같다.
        private static void OnParamCombineDocumentOpened(object? sender, DocumentOpenedEventArgs e)
        {
            try { ParamCombineUpdater.RefreshTriggers(e.Document); }
            catch
            {
                // 무시 - 자동 반영만 못 붙을 뿐, 리본의 "전체 갱신"은 그대로 동작한다.
            }
        }

        private static void OnParamCombineDocumentCreated(object? sender, DocumentCreatedEventArgs e)
        {
            try { ParamCombineUpdater.RefreshTriggers(e.Document); }
            catch
            {
                // 위와 같은 이유
            }
        }

        // 뷰 전환 시 즉시 툴바 상태(아이콘 색/버튼 목록)를 갱신한다. 설정이 문서(프로젝트 파일)별로
        // 저장되므로 리본의 "표시/숨김" 라벨도 활성 문서가 바뀔 때마다 다시 맞춰야 한다.
        // 커스텀 툴바는 여기서 처음 얻는 살아있는 UIApplication으로 지연 생성한다 (위 OnStartup 주석 참고).
        private static void OnQuickToggleViewActivated(object? sender, ViewActivatedEventArgs e)
        {
            // Revit 이벤트 콜백에서 예외가 밖으로 새어나가면 Revit이 오류 대화상자를 띄우거나
            // 이벤트 구독 자체를 끊어버린다. 특히 Idling은 초당 여러 번 발생해 한 번의 예외가
            // 대화상자 폭주로 이어진다. 여기서는 상태 표시만 하므로 조용히 삼키고 다음 틱에 다시 시도한다.
            try
            {
                if (QuickToggleToolbar.Instance == null && sender is UIApplication uiapp)
                    _ = new QuickToggleToolbar(uiapp); // 생성자가 자기 자신을 QuickToggleToolbar.Instance에 등록한다

                QuickToggleToolbar.Instance?.RefreshState();
                if (QuickToggleToolbar.Instance?.CurrentToolbarVisible is bool visible)
                    UpdateQuickToggleVisibilityLabel(visible);
            }
            catch
            {
                // 무시 - 다음 뷰 전환/유휴 틱에 다시 갱신된다.
            }
        }

        // 문서가 닫히는 중에는 일단 숨겨둔다 - 다른 문서가 곧이어 활성화되면 뒤따르는 ViewActivated에서
        // 다시 올바른 상태로 보이게 된다.
        // 문서가 닫힐 때 알아야 하는 기능들을 여기서 한꺼번에 처리한다. **문서 이벤트 구독은 반드시
        // 여기(OnStartup의 ControlledApplication)에서만 한다** - 명령 실행 중에만 유효한
        // UIApplication으로 구독/해지하면 나중에 해지할 때 예외가 나고, 그 예외가 Revit을 통째로
        // 죽인다(2026-09-21 NAMER v84에서 실제로 발생, docs/namer/CLAUDE.md 참고).
        // 기능별로 try를 따로 두는 이유는 하나가 실패해도 나머지는 처리되게 하기 위해서다.
        private static void OnDocumentClosing(object? sender, DocumentClosingEventArgs e)
        {
            try { QuickToggleToolbar.Instance?.HideForNoDocument(); }
            catch { /* 문서를 닫는 중이라 실패해도 사용자에게 알릴 것이 없다. */ }

            // NAMER는 모드리스라 목록이 살아 있는 동안 그 문서가 닫힐 수 있다 - 들고 있던 Element가
            // 전부 무효가 되므로 창을 같이 닫는다.
            try { NamerWindow.Instance?.CloseForDocument(e.Document); }
            catch { /* 위와 같은 이유 */ }
        }

        // Idling은 유휴 상태마다 매우 자주 발생한다 - 여기서는 디스크 재로드 없이(RefreshState/
        // EnsureSettingsLoaded 참고) 캐시된 설정으로 아이콘 상태 재판정 + 창 위치 추적(Revit 창 이동/
        // 리사이즈 대응)만 가볍게 수행한다.
        private static void OnQuickToggleIdling(object? sender, IdlingEventArgs e)
        {
            // 예외를 밖으로 내보내면 안 된다 - 위 OnQuickToggleViewActivated 주석 참고.
            try
            {
                if (QuickToggleToolbar.Instance == null && sender is UIApplication uiapp)
                    _ = new QuickToggleToolbar(uiapp);

                QuickToggleToolbar.Instance?.RefreshState();
            }
            catch
            {
                // 무시 - 다음 유휴 틱에 다시 갱신된다.
            }
        }

        // 커스텀 "기능 버튼"이 PostCommand로 쓸 Revit 명령 id를 실제 리본에서 읽어 등록한다.
        // 형식은 저널에서 실측한 `CustomCtrl_%CustomCtrl_%<탭>%<패널>%<버튼 internal name>`
        // (SunnyToolsCommands.RibbonCommandIds 주석 참고).
        private static void RegisterRibbonCommandIds(UIControlledApplication application)
        {
            try
            {
                foreach (RibbonPanel panel in application.GetRibbonPanels(TabName))
                    foreach (RibbonItem item in panel.GetItems())
                        RegisterRibbonCommandId(panel, item);
            }
            catch
            {
                // 리본 조회가 실패해도 애드인 로드 자체를 막지는 않는다 - 기능 버튼만 못 쓰게 된다.
            }
        }

        // GetItems()가 스택 안의 버튼까지 돌려주는지는 연도별 API 동작을 실측하지 못했으므로,
        // AddStackedItems를 쓰는 두 곳(AddSettingsStack/AddQuickToggleStack)에서도 반환된 항목으로
        // 직접 한 번 더 등록한다 - 첫 등록만 유지되므로 중복 호출은 무해하다.
        private static void RegisterRibbonCommandId(RibbonPanel panel, RibbonItem item)
        {
            if (item is PushButton button && !string.IsNullOrEmpty(button.ClassName))
                SunnyToolsCommands.RegisterRibbonCommandId(
                    button.ClassName,
                    "CustomCtrl_%CustomCtrl_%" + TabName + "%" + panel.Name + "%" + button.Name);
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
                Image = RibbonIcons.Settings(16),
            };

            PushButtonData toggleButtonData = new PushButtonData(
                "WallSplitter_ToggleTypeAssignment" + idSuffix,
                ToggleLabel(currentSettings.TypeAssignmentPersistence),
                assemblyPath,
                typeof(ToggleTypeAssignmentPersistenceCommand).FullName)
            {
                ToolTip = "'유형 직접 지정' 모드에서, 지정한 유형을 다음 벽/바닥에도 이어서 적용할지(복수) 매번 새로 지정할지(단일) 전환합니다 (벽체 분리·바닥 분리가 공유).",
                Image = RibbonIcons.Toggle(16, currentSettings.TypeAssignmentPersistence == TypeAssignmentPersistence.Multiple),
            };

            IList<RibbonItem> stackedItems = targetPanel.AddStackedItems(settingsButtonData, toggleButtonData);
            foreach (RibbonItem stacked in stackedItems) RegisterRibbonCommandId(targetPanel, stacked);
            if (stackedItems.Count == 2 && stackedItems[1] is PushButton toggleButton)
                _typeAssignmentToggleButtons.Add(toggleButton);
        }

        private static string ToggleLabel(TypeAssignmentPersistence mode) =>
            mode == TypeAssignmentPersistence.Multiple ? "복수" : "단일";

        // "빠른 토글" 패널에 "빠른 토글 설정"(뷰템플릿/필터/작업세트 버튼 등록) + "표시/숨김"
        // (커스텀 툴바를 껐다 켬) 두 버튼을 스택으로 붙인다 - AddSettingsStack과 같은 패턴.
        private static void AddQuickToggleStack(RibbonPanel targetPanel, string assemblyPath)
        {
            PushButtonData settingsButtonData = new PushButtonData(
                "WallSplitter_QuickToggleSettings",
                "커스텀 버튼\n설정",
                assemblyPath,
                typeof(QuickToggleSettingsCommand).FullName)
            {
                ToolTip = "현재 뷰에서 원클릭으로 켜고 끌 뷰템플릿/필터/작업세트 버튼을 등록합니다.\n등록한 버튼은 Revit 창 상단에 별도 툴바로 나타나며, 같은 종류라도 이름을 다르게 지정해 여러 개 추가할 수 있습니다.",
                Image = RibbonIcons.QuickToggle(16),
            };

            PushButtonData toggleButtonData = new PushButtonData(
                "WallSplitter_QuickToggleVisibility",
                QuickToggleVisibilityLabel(true),
                assemblyPath,
                typeof(QuickToggleVisibilityToggleCommand).FullName)
            {
                ToolTip = "커스텀 버튼 툴바를 현재 프로젝트 파일에서 표시하거나 숨깁니다.",
                // 문서가 열리기 전이라 실제 프로젝트별 표시 상태를 아직 몰라 일단 "켜짐" 아이콘으로 시작하고,
                // ViewActivated에서 UpdateQuickToggleVisibilityLabel이 실제 상태로 바로잡는다(라벨과 동일한 방식).
                Image = RibbonIcons.Toggle(16, true),
            };

            IList<RibbonItem> stackedItems = targetPanel.AddStackedItems(settingsButtonData, toggleButtonData);
            foreach (RibbonItem stacked in stackedItems) RegisterRibbonCommandId(targetPanel, stacked);
            if (stackedItems.Count == 2 && stackedItems[1] is PushButton toggleButton)
                _quickToggleVisibilityButtons.Add(toggleButton);
        }

        private static string QuickToggleVisibilityLabel(bool visible) => visible ? "켜짐" : "꺼짐";

        // "매개변수 조합" 패널: 큰 "매개변수 조합"(설정 창) 버튼 옆에 작은 "실시간 ON/OFF" + "전체 갱신"을 쌓는다
        // - AddSettingsStack/AddQuickToggleStack과 같은 패턴.
        private static void AddParamCombineStack(RibbonPanel targetPanel, string assemblyPath)
        {
            bool autoUpdate = ParamCombineSettings.Current.AutoUpdate;

            PushButtonData toggleButtonData = new PushButtonData(
                "WallSplitter_ParamCombineToggle",
                ParamCombineToggleLabel(autoUpdate),
                assemblyPath,
                typeof(ParamCombineToggleCommand).FullName)
            {
                ToolTip = "소스 매개변수를 고치는 즉시 결과 매개변수가 따라 바뀌게 할지 전환합니다.\n끄면 규칙은 그대로 남고 자동 반영만 멈춥니다(전체 갱신 버튼은 계속 쓸 수 있습니다).",
                Image = RibbonIcons.Toggle(16, autoUpdate),
            };

            PushButtonData refreshButtonData = new PushButtonData(
                "WallSplitter_ParamCombineRefresh",
                "전체 갱신",
                assemblyPath,
                typeof(ParamCombineRefreshCommand).FullName)
            {
                ToolTip = "현재 문서의 대상 요소 전체에 규칙을 한 번에 적용합니다.\n애드인을 쓰기 전부터 있던 요소를 처음 맞출 때, 또는 실시간을 꺼둔 채로 쓸 때 사용합니다(Dynamo Player의 Run에 해당).",
                Image = RibbonIcons.Refresh(16),
            };

            IList<RibbonItem> stackedItems = targetPanel.AddStackedItems(toggleButtonData, refreshButtonData);
            foreach (RibbonItem stacked in stackedItems) RegisterRibbonCommandId(targetPanel, stacked);
            if (stackedItems.Count == 2 && stackedItems[0] is PushButton toggleButton)
                _paramCombineToggleButtons.Add(toggleButton);
        }

        // "일괄 결합금지/허용" 패널: 큰 "결합 금지·허용"(창) 버튼 옆에 작은 "선택 금지"/"선택 허용"을 쌓는다 - 창을 열지 않고
        // 지금 선택한 요소의 양쪽 끝을 바로 처리하는, 가장 잦은 작업을 클릭 한 번으로 끝내기 위한 것이다.
        private static void AddBatchJoinStack(RibbonPanel targetPanel, string assemblyPath)
        {
            PushButtonData disallowButtonData = new PushButtonData(
                "WallSplitter_BatchJoinDisallowSelection",
                "선택 금지",
                assemblyPath,
                typeof(BatchJoinDisallowSelectionCommand).FullName)
            {
                ToolTip = "지금 선택한 벽·보·가새의 양쪽 끝을 바로 '결합 허용 안 함'으로 만듭니다(창을 열지 않습니다).\n한쪽 끝만 걸거나 유형 단위로 걸려면 큰 '결합 금지·허용' 버튼으로 창을 여세요.",
                Image = RibbonIcons.BatchJoin(16, true),
            };

            PushButtonData allowButtonData = new PushButtonData(
                "WallSplitter_BatchJoinAllowSelection",
                "선택 허용",
                assemblyPath,
                typeof(BatchJoinAllowSelectionCommand).FullName)
            {
                ToolTip = "지금 선택한 벽·보·가새의 양쪽 끝 결합을 다시 허용합니다(창을 열지 않습니다).\nRevit이 원래대로 맞닿은 부재와 결합합니다.",
                Image = RibbonIcons.BatchJoin(16, false),
            };

            IList<RibbonItem> stackedItems = targetPanel.AddStackedItems(disallowButtonData, allowButtonData);
            foreach (RibbonItem stacked in stackedItems) RegisterRibbonCommandId(targetPanel, stacked);
        }

        private static string ParamCombineToggleLabel(bool autoUpdate) => autoUpdate ? "실시간 켜짐" : "실시간 꺼짐";

        // ParamCombineToggleCommand/ParamCombineCommand가 설정을 바꾼 직후 호출해 리본 버튼 표시를 맞춘다.
        internal static void UpdateParamCombineToggleLabel(bool autoUpdate)
        {
            foreach (PushButton toggleButton in _paramCombineToggleButtons)
            {
                toggleButton.ItemText = ParamCombineToggleLabel(autoUpdate);
                toggleButton.Image = RibbonIcons.Toggle(16, autoUpdate);
            }
        }

        // QuickToggleVisibilityToggleCommand/OnQuickToggleViewActivated가 설정 변경 직후 호출해
        // 리본 버튼 텍스트를 현재 활성 문서(프로젝트 파일) 기준으로 갱신한다.
        internal static void UpdateQuickToggleVisibilityLabel(bool visible)
        {
            foreach (PushButton button in _quickToggleVisibilityButtons)
            {
                button.ItemText = QuickToggleVisibilityLabel(visible);
                button.Image = RibbonIcons.Toggle(16, visible);
            }
        }

        // ToggleTypeAssignmentPersistenceCommand가 설정을 바꾼 직후 호출해 리본 버튼 텍스트/아이콘을 갱신한다.
        // 벽체 분리/바닥 분리 패널 양쪽 토글 버튼 모두 같은 설정을 가리키므로 둘 다 갱신해야 한다.
        internal static void UpdateTypeAssignmentToggleLabel(TypeAssignmentPersistence mode)
        {
            foreach (PushButton toggleButton in _typeAssignmentToggleButtons)
            {
                toggleButton.ItemText = ToggleLabel(mode);
                toggleButton.Image = RibbonIcons.Toggle(16, mode == TypeAssignmentPersistence.Multiple);
            }
        }

        // Revit은 WPF 기반이 아니라서 프로세스 안에 System.Windows.Application이 하나도 없다.
        // DataGrid처럼 기본 테마 리소스(Aero2 등)에 의존하는 복잡한 컨트롤은 Application이 없으면
        // "Baml2006.TypeConverterMarkupExtension에 대한 값 제공에서 예외가 발생했습니다" 같은 알 수 없는
        // 오류로 창 생성 자체가 실패한다(Button/TextBox 같은 단순 컨트롤만 쓰는 SettingsWindow는 문제없었음).
        // Run()은 호출하지 않고 인스턴스만 만들어 리소스 조회 인프라를 부팅한다.
        //
        // CONFIRMED LIVE BUG (2026-07-27), fixed twice:
        // 1차 수정: 예전 코드는 "Application.Current != null이면 그냥 return"이었다 - Revit은 여러
        // 애드인을 같은 프로세스에서 로드하는데, Revit 자신의 리본 UI를 포함해 이 PC의 pyRevit/BIMPeers
        // BIMIL 계열/Metasheet 등 다른 WPF 기반 애드인이 WallSplitter.App.OnStartup보다 먼저 자기
        // System.Windows.Application을 만들어두면(사실상 항상 그런 상황이다 - Revit 리본 자체가 이미
        // WPF다), 저 guard 때문에 Theme.xaml이 "이번 세션 내내 단 한 번도" 병합되지 못해 NAMER/재료
        // 지정/모델간 변경 반영/빠른 토글 설정처럼 창을 여는 커맨드가 XamlParseException으로 죽었다
        // (벽체 분리는 창을 안 열어서 멀쩡해 보였을 뿐). 그래서 "Application이 이미 있어도 Theme.xaml은
        // 항상 병합"하도록 고쳤었다.
        // 2차 수정(사용자 실측 보고로 발견): 그 1차 수정이 Theme.xaml을 Application.Resources(프로세스
        // 전체 공유)에 병합했는데, 바로 그 이유(Revit 리본/다른 애드인도 같은 Application.Current를 씀)
        // 때문에 우리의 암시적(TargetType, x:Key 없는) Window/TextBlock/Button 스타일이 Revit 자체
        // 리본과 다른 애드인 창에까지 그대로 적용되어 버려, 텍스트가 전부 하얗게(우리 다크 테마의 밝은
        // 전경색으로) 보이는 광범위한 회귀가 생겼다. 근본 원인: 프로세스 전체가 공유하는 Application
        // 리소스에 "우리만 쓸" 암시적 스타일을 병합하는 것 자체가 애초에 안전하지 않다 - Application이
        // 정말 우리 소유가 아닐 수 있다는 걸 1차 수정 때는 놓쳤다. 최종 수정: Theme.xaml 병합은 각 창의
        // 로컬 `Window.Resources`로 옮겼다(SettingsWindow.xaml 등 각 XAML 파일 참고) - 이러면 암시적
        // 스타일이 그 창(과 그 창이 띄우는 팝업/드롭다운)의 시각적 트리 안에만 적용되고 프로세스 전체로
        // 번지지 않는다. 이 메서드는 이제 "Application 인스턴스 자체가 없으면 하나 만든다"는 것만 한다
        // (DataGrid처럼 기본 테마 리소스에 의존하는 복잡한 컨트롤은 Application이 아예 없으면 Baml2006
        // 예외로 실패하므로 - 이건 여전히 필요) - Theme.xaml을 여기서 전역으로 병합하지 않는다.
        private static void EnsureWpfApplication()
        {
            if (WpfApplication.Current == null)
            {
                _ = new WpfApplication { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            }
        }
    }
}
