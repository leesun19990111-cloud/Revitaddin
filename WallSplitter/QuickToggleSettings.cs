using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    // 빠른 토글 버튼 하나가 다루는 대상의 종류. 뷰템플릿은 뷰당 하나만 적용 가능하므로 단일 값,
    // 필터/작업세트는 그룹으로 묶어 한 번에 켜고 끌 수 있도록 리스트로 저장한다.
    public enum QuickToggleCategory
    {
        ViewTemplate,
        Filter,
        Workset,
        // 2026-09-02 삭제된 카테고리("프리셋 버튼"/"그래픽 화면표시 검색 버튼") - 열거 멤버 자체는
        // 남겨둔다. System.Text.Json의 JsonStringEnumConverter는 모르는 문자열을 만나면 예외를 던지고,
        // QuickToggleSettings.Load는 그 예외를 "설정 파일 손상"으로 보고 빈 설정으로 갈아치우므로,
        // 멤버를 지우면 프리셋 버튼 하나 때문에 그 프로젝트에 등록된 다른 버튼까지 전부 사라진다.
        // 멤버는 남겨 역직렬화만 통과시키고, 실제 버튼은 Load/가져오기 단계에서 걸러낸다
        // (QuickToggleSettings.IsRemovedCategory). UI·서비스 쪽 구현은 전부 제거됐다.
        Preset,
        // 2026-07-29, "모델을 선택해서 색상과 투명도를 설정해줄 수 있는 버튼" 요청으로 추가. 다른
        // 카테고리들과 근본적으로 다르다 - on/off를 켜고 끄는 토글이 아니라, 클릭하면 색상 팔레트 +
        // 투명도 슬라이더가 담긴 작은 패널이 펼쳐지고(QuickToggleToolbar.ShowColorToolPopup), 그 안에서
        // 조작할 때마다 활성 뷰에 즉시 반영된다. 설정 창에서는 "어떤 모델 카테고리에 적용할지"만 미리
        // 고르고(ColorButtonCategories), 실제 색상/투명도 값은 저장하지 않는다 - 매번 클릭했을 때 그
        // 카테고리의 현재 값을 읽어와 보여준다(QuickToggleService.ReadCurrentColorAndTransparency).
        ColorTool,
        // 위 Preset과 같은 이유로 남겨둔 삭제된 카테고리 (2026-09-02).
        GraphicsDisplaySearch,
        // 2026-08-03, "커스텀 버튼 설정에 다른 툴들의 버튼도 추가할 수 있으면 좋겠다 - 재료지정, 네이머,
        // 공동작업탭의 동기화 버튼 등을 찾아서 버튼으로 추가하고 싶다"는 요청으로 추가. ColorTool처럼
        // on/off 개념이 없고, 클릭하면 지정된 Revit 명령(Sunny Tools 자체 명령 또는 Revit 기본 명령)을
        // 즉시 한 번 실행할 뿐이다(QuickToggleService.RunCommand, RevitCommandId+PostCommand 사용).
        CommandLauncher,
        // 2026-09-02, "모르는 사람이 쓰기엔 커스텀 버튼이 너무 어렵다 - 프리셋/그래픽 화면표시 검색은
        // 없애고, 대신 활성 뷰에 링크된 도면(CAD)과 링크된 모델(RVT)을 딸깍 한 번으로 끄고 켜는 버튼을
        // 넣어달라"는 요청으로 추가. 뷰템플릿/필터/작업세트처럼 on/off 토글이지만 설정에서 미리 고를
        // 대상이 없다 - 대상은 "지금 활성 뷰의 문서에 실제로 걸려 있는 링크"라서 클릭할 때마다 새로
        // 찾는다(QuickToggleService.LinkedCadCategoryIds/LinkedModelCategoryIds).
        LinkedCad,
        LinkedModel,
    }

    // CommandLauncher 버튼이 가리키는 명령의 종류 - RevitCommandId를 조회하는 API가 서로 다르다
    // (SunnyTool은 RevitCommandId.LookupCommandId(전체 클래스 이름), NativeRevit은
    // RevitCommandId.LookupPostableCommandId(PostableCommand)).
    public enum QuickToggleCommandKind
    {
        SunnyTool,
        NativeRevit,
    }

    // ElementId.IntegerValue(int)는 2023 API에만 있고, 2024+에서는 Value(long)로 바뀌면서 완전히
    // 제거됐다(2026 빌드에서 실측 확인 - Floor.SlabShapeEditor 때와 같은 종류의 연도별 API 차이).
    // 이 프로젝트에서 저장하는 ElementId는 전부 int 범위로 충분하므로 어느 쪽이든 int로 통일해 다룬다.
    internal static class ElementIdCompat
    {
        public static int ToInt(this ElementId id)
        {
#if REVIT2023
            return id.IntegerValue;
#else
            return (int)id.Value;
#endif
        }
    }

    public class QuickToggleButtonConfig
    {
        // 이름은 사용자가 언제든 바꿀 수 있어 식별자로 쓸 수 없으므로, 생성 시 발급하는 GUID로 버튼을 구분한다.
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public QuickToggleCategory Category { get; set; } = QuickToggleCategory.ViewTemplate;

        // 카테고리에 맞는 필드만 채워지고 나머지는 항상 비워둔다.
        // ElementId는 IntegerValue(int)로 저장한다 - 2023~2027 전 연도 API에서 컴파일되는 접근자이고
        // (2024+에서 추가된 Value(long)는 obsolete 경고 없이 IntegerValue와 공존), 실제 프로젝트에서
        // int 범위를 넘는 ElementId는 사실상 없다.
        public int? ViewTemplateId { get; set; }
        public List<int> FilterIds { get; set; } = new List<int>();
        public List<int> WorksetIds { get; set; } = new List<int>();

        // ID와 나란히 이름도 저장한다 - ElementId는 문서마다 다르므로 내보내기/가져오기(다른 모델 간
        // 설정 이식, 2026-07-28 요청)에서는 이름으로만 대상을 다시 찾을 수 있다. ID를 설정하는 지점
        // (QuickToggleSettingsWindow의 라디오/체크박스 핸들러)에서 항상 같이 채운다.
        public string? ViewTemplateName { get; set; }
        public List<string> FilterNames { get; set; } = new List<string>();
        public List<string> WorksetNames { get; set; } = new List<string>();

        // 사용자가 버튼마다 아이콘 모양/on 상태 색을 직접 고를 수 있게 해달라는 요청(2026-07-27)으로 추가.
        // 둘 다 null이면 예전 그대로 카테고리 기본 아이콘(QuickToggleIcons.DefaultFor)과 공용 on 색
        // (Theme.ToggleOn)을 쓴다 - 기존에 저장된 설정 파일도 그대로 호환된다.
        public QuickToggleIconShape? IconShape { get; set; }
        public string? OnColorHex { get; set; }

        // 2026-07-29, "색상 버튼" 전용 필드 - 이 버튼이 색상/투명도를 적용할 모델 카테고리 목록.
        // (2026-09-02 프리셋 삭제 전까지는 프리셋의 카테고리별 V/G 재정의를 담는 CategoryOverrides
        // 필드도 같은 타입을 공유했다 - 그 필드가 사라지면서 타입도 색상 버튼 전용으로 줄였다.)
        public List<ColorToolCategoryConfig> ColorButtonCategories { get; set; } = new List<ColorToolCategoryConfig>();

        // 2026-08-03, "기능 버튼"(CommandLauncher) 전용 - 클릭하면 실행할 명령 하나. CommandId는
        // CommandKind에 따라 다른 의미다: SunnyTool이면 IExternalCommand 구현 클래스의 전체 이름
        // (SunnyToolsCommands.All의 값, RevitCommandId.LookupCommandId로 조회), NativeRevit이면
        // PostableCommand enum 멤버 이름(RevitCommandId.LookupPostableCommand로 조회). 둘 다 문서가
        // 아니라 Revit/이 애드인 자체에 속한 식별자라 ElementId와 달리 문서마다 다르지 않으므로 -
        // ViewTemplateId/FilterIds처럼 이름을 따로 저장해 내보내기/가져오기 때 재검색할 필요가 없다
        // (그대로 복사해도 어느 문서에서나 똑같이 유효함). CommandLabel은 설정 창에 표시할 사람이 읽는
        // 이름을 저장해둔다(PostableCommand는 raw enum 이름이라 검색 목록을 매번 다시 만들지 않고도
        // 툴팁/버튼 목록에 바로 쓸 수 있게).
        public QuickToggleCommandKind? CommandKind { get; set; }
        public string? CommandId { get; set; }
        public string? CommandLabel { get; set; }
    }

    // "색상 버튼"이 색상/투명도를 적용할 카테고리 한 줄. ElementId는 문서마다 달라 이식(내보내기/
    // 가져오기)이 안 되므로 ViewTemplateId/Name과 같은 이유로 이름을 같이 저장한다.
    // 2026-09-02 프리셋 삭제 전에는 이 타입(당시 이름 CategoryOverrideConfig)이 카테고리별 V/G 재정의
    // (표시/하프톤/상세수준/투명도/선·패턴 색상 등 20여 개 필드)까지 담았지만, 그 필드들을 쓰던 곳이
    // 프리셋과 그래픽 화면표시 검색뿐이라 함께 지웠다. 예전 설정 파일에 남아 있는 그 필드들은
    // System.Text.Json이 모르는 속성으로 무시하므로 그대로 읽힌다.
    public class ColorToolCategoryConfig
    {
        public int CategoryId { get; set; }
        public string CategoryName { get; set; } = "";
        // 최상위 카테고리면 null. 같은 이름의 하위 카테고리가 서로 다른 상위 카테고리에 있을 수 있어
        // (예: 여러 카테고리가 공유하는 서브카테고리 이름) 가져오기 시 이름만으로는 매칭이 모호할 수
        // 있으므로 부모 이름까지 같이 저장해 매칭 정확도를 높인다.
        public string? ParentCategoryName { get; set; }
    }

    // 이 PC에 하나만 있는 전역 설정 (2026-09-03부터 - 그 전에는 프로젝트 파일 경로별로 따로 저장했다).
    //
    // **왜 전역으로 바꿨나** (사용자 요청): "레빗 파일마다 저장되도록 했었는데, 그것보다는 사용자 로컬
    // 컴퓨터에 저장되어서 어떤 프로젝트를 열어도 설정이 변하지 않도록 하는게 훨씬 편리할 것 같아."
    // 툴바 위치(QuickToggleGlobalSettings)가 2026-07-28에 같은 이유로 이미 전역이 됐고, 이제 버튼 목록도
    // 같은 폴더의 파일 하나에 담긴다.
    //
    // **그래서 대상은 반드시 이름으로 다시 찾아야 한다**: ViewTemplateId/FilterIds/WorksetIds에 담긴
    // ElementId는 문서마다 완전히 다른 값이라, 전역 설정을 다른 프로젝트에서 그대로 쓰면 엉뚱한 요소를
    // 가리키거나(더 나쁘게는 조용히 다른 요소를 켜고 끄거나) 아무것도 못 찾는다. 실제 해석은
    // QuickToggleService.ResolveViewTemplateId/ResolveFilterIds/ResolveWorksetIds가 **이름**으로 하고,
    // ID 필드는 이름이 비어 있는 옛 설정(2026-07-28에 이름 필드가 생기기 전에 만든 버튼)을 위한
    // fallback으로만 남는다 - 그 경우는 원래 그 프로젝트에서만 동작한다.
    public class QuickToggleSettings
    {
        // 목록의 순서가 곧 툴바에 표시되는 버튼 순서.
        public List<QuickToggleButtonConfig> Buttons { get; set; } = new List<QuickToggleButtonConfig>();
        public bool ToolbarVisible { get; set; } = true;

        // 2026-09-03, "색상버튼은 색상버튼끼리, 작업세트는 작업세트끼리 묶여서 함께 이동하고, 연결고리
        // 버튼을 눌러 고리를 끊으면 개별로도 움직일 수 있게" 요청으로 추가. 여기에 들어 있는 종류만
        // 고리가 끊긴 것이고, 나머지는 전부 묶여 있다(빈 목록 = 전부 묶임 = 기존 설정 파일과 호환).
        // 묶여 있는 종류는 NormalizeGrouping()이 Buttons 안에서 한 덩어리로 모아 준다.
        public List<QuickToggleCategory> UnlinkedCategories { get; set; } = new List<QuickToggleCategory>();

        // 전역으로 바뀌기 전(2026-09-03 이전) 이 설정이 어느 프로젝트 것이었는지 - 마이그레이션 흔적으로만
        // 남긴다. 지금은 아무 데서도 읽지 않는다.
        public string ProjectPath { get; set; } = "";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        internal static string RootDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WallSplitter", "quick-toggle");

        // 전역 설정 파일 하나. 이름이 해시가 아니라 사람이 읽을 수 있는 이름인 것도 의도한 것이다
        // (프로젝트별 시절에는 경로 해시가 파일명이었다).
        internal static string PathFile => Path.Combine(RootDir, "buttons.json");

        // 2026-09-03 이전에 쓰던 프로젝트별 설정 파일 경로 - 이제 마이그레이션에서만 읽는다.
        private static string? LegacyPathFor(Document? doc)
        {
            string? projectPath = doc?.PathName;
            if (string.IsNullOrEmpty(projectPath)) return null;

            string hash = Hash(projectPath.ToLowerInvariant());
            return Path.Combine(RootDir, hash + ".json");
        }

        private static string Hash(string text)
        {
            using SHA256 sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // doc은 이제 "전역 파일이 아직 없을 때 어느 프로젝트의 옛 설정을 이어받을지"에만 쓰인다
        // (사용자 확정: "지금 열린 프로젝트 것을 이어받기"). 옛 프로젝트별 파일은 지우지 않고 그대로 둔다 -
        // 되돌릴 일이 생겼을 때의 안전망이고, 어차피 다시 읽히지 않는다.
        public static QuickToggleSettings Load(Document? doc)
        {
            QuickToggleSettings? loaded = ReadFrom(PathFile);
            if (loaded != null) return loaded;

            string? legacy = LegacyPathFor(doc);
            if (legacy != null)
            {
                QuickToggleSettings? migrated = ReadFrom(legacy);
                if (migrated != null)
                {
                    // 이어받은 즉시 전역 파일로 굳혀 둔다 - 그래야 다음에 다른 프로젝트를 열어도 같은
                    // 설정이 나온다(이 시점에 저장하지 않으면 "마지막에 연 프로젝트 것"이 계속 따라온다).
                    try { migrated.Save(); }
                    catch
                    {
                        // 저장에 실패해도 이번 세션에는 이어받은 설정을 그대로 쓴다.
                    }
                    return migrated;
                }
            }

            return new QuickToggleSettings();
        }

        private static QuickToggleSettings? ReadFrom(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path, Encoding.UTF8);
                QuickToggleSettings? loaded = JsonSerializer.Deserialize<QuickToggleSettings>(json, JsonOptions);
                if (loaded == null) return null;

                loaded.Buttons ??= new List<QuickToggleButtonConfig>();
                loaded.UnlinkedCategories ??= new List<QuickToggleCategory>();
                loaded.Buttons.RemoveAll(b => IsRemovedCategory(b.Category));
                loaded.NormalizeGrouping();
                return loaded;
            }
            catch
            {
                // 설정 파일이 손상된 경우 빈 설정으로 대체
                return null;
            }
        }

        public void Save()
        {
            NormalizeGrouping();
            Directory.CreateDirectory(RootDir);
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(PathFile, json, Encoding.UTF8);
        }

        // ===== 종류별 묶음 (2026-09-03) =====

        public bool IsLinked(QuickToggleCategory category) => !UnlinkedCategories.Contains(category);

        public void SetLinked(QuickToggleCategory category, bool linked)
        {
            if (linked) UnlinkedCategories.RemoveAll(c => c == category);
            else if (!UnlinkedCategories.Contains(category)) UnlinkedCategories.Add(category);
            NormalizeGrouping();
        }

        // 묶여 있는 종류는 Buttons 안에서 반드시 연속된 한 덩어리여야 한다("함께 이동"이 성립하려면).
        // 각 종류를 그 종류가 처음 나온 자리로 끌어모으고, 고리가 끊긴 종류는 있던 자리에 그대로 둔다 -
        // 그래야 풀어놓은 버튼이 다른 종류 사이에 끼워둔 위치를 잃지 않는다.
        public void NormalizeGrouping()
        {
            if (Buttons.Count < 2) return;

            List<QuickToggleButtonConfig> result = new List<QuickToggleButtonConfig>(Buttons.Count);
            HashSet<QuickToggleCategory> placed = new HashSet<QuickToggleCategory>();

            foreach (QuickToggleButtonConfig cfg in Buttons)
            {
                if (!IsLinked(cfg.Category))
                {
                    result.Add(cfg);
                    continue;
                }
                if (!placed.Add(cfg.Category)) continue; // 이미 이 종류 덩어리를 통째로 넣었다
                foreach (QuickToggleButtonConfig sibling in Buttons)
                    if (sibling.Category == cfg.Category) result.Add(sibling);
            }

            Buttons.Clear();
            Buttons.AddRange(result);
        }

        // 2026-09-02에 삭제된 버튼 종류 - 예전 설정 파일/JSON에 남아 있어도 목록에 싣지 않는다
        // (열거 멤버를 남겨둔 이유는 QuickToggleCategory의 주석 참고).
        public static bool IsRemovedCategory(QuickToggleCategory category) =>
            category == QuickToggleCategory.Preset || category == QuickToggleCategory.GraphicsDisplaySearch;

        // 같은 카테고리 내에서 "뷰템플릿버튼1", "뷰템플릿버튼2"처럼 다음 번호를 붙인 기본 이름을 만든다.
        public string NextDefaultName(QuickToggleCategory category)
        {
            string prefix = category switch
            {
                QuickToggleCategory.ViewTemplate => "뷰템플릿버튼",
                QuickToggleCategory.Filter => "필터버튼",
                QuickToggleCategory.Workset => "작업세트버튼",
                QuickToggleCategory.ColorTool => "색상버튼",
                QuickToggleCategory.CommandLauncher => "기능버튼",
                QuickToggleCategory.LinkedCad => "링크도면버튼",
                QuickToggleCategory.LinkedModel => "링크모델버튼",
                _ => "버튼",
            };
            int count = 0;
            foreach (QuickToggleButtonConfig b in Buttons)
                if (b.Category == category) count++;
            return prefix + (count + 1);
        }
    }

    // 툴바 위치 - 2026-07-28까지는 프로젝트별 QuickToggleSettings에 같이 저장했었으나, "어떤 프로젝트를
    // 열더라도 위치는 그대로 있어야 한다"는 요청으로 프로젝트 경로와 무관한 PC 전역 설정으로 분리했다.
    public class QuickToggleGlobalSettings
    {
        public int ToolbarOffsetXDip { get; set; } = 6;
        public int ToolbarOffsetYDip { get; set; } = 130;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        private static string PathFile => Path.Combine(QuickToggleSettings.RootDir, "toolbar-position.json");

        public static QuickToggleGlobalSettings Load()
        {
            try
            {
                if (File.Exists(PathFile))
                {
                    string json = File.ReadAllText(PathFile, Encoding.UTF8);
                    QuickToggleGlobalSettings? loaded = JsonSerializer.Deserialize<QuickToggleGlobalSettings>(json, JsonOptions);
                    if (loaded != null) return loaded;
                }
            }
            catch
            {
                // 설정 파일이 손상된 경우 기본값으로 대체
            }
            return new QuickToggleGlobalSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(QuickToggleSettings.RootDir);
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(PathFile, json, Encoding.UTF8);
        }
    }
}
