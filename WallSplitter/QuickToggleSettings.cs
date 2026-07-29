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
        // 2026-07-28, "여러 설정 조합(뷰템플릿+필터+작업세트)을 한 번에 켜고 끄는 버튼을 만들고 싶다"는
        // 요청으로 추가. 위 세 카테고리와 필드 자체는 공유하지만(ViewTemplateId/FilterIds/WorksetIds를
        // 동시에 채울 수 있음), 비어있는 필드는 "이 프리셋에 그 항목은 포함되지 않음"으로 해석되어
        // 건드리지 않는다는 점이 단일 카테고리 버튼과 다르다(QuickToggleService 참고).
        Preset,
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

        // 2026-07-29, "V/G 편집창에 있는 것들을 그대로 옮겨서 프리셋에 담고 싶다"는 요청으로 추가 - 프리셋에
        // 포함된 카테고리별 표시 여부 + 그래픽 재정의(선/패턴/투명도/하프톤/상세수준)를 담는다. 카테고리가
        // 이 리스트에 있다는 것 자체가 "이 프리셋에 포함됨"을 뜻하고(리스트에 없으면 그 카테고리는 아예
        // 건드리지 않음 - Preset의 다른 필드들과 같은 "비어있으면 안 건드림" 규칙), 켜질 때 설정을 적용하고
        // 꺼질 때는 표시로 되돌리고 재정의를 지운다(QuickToggleService.ApplyCategoryOverrides 참고).
        public List<CategoryOverrideConfig> CategoryOverrides { get; set; } = new List<CategoryOverrideConfig>();
    }

    // 프리셋의 카테고리(V/G) 탭 한 줄 - Revit V/G 대화상자에서 카테고리별로 재정의할 수 있는 항목을 그대로
    // 옮겼다. 색상은 int(0xRRGGBB)로, 선/채우기 패턴은 이름으로 저장한다 - ElementId는 문서마다 달라
    // 이식(내보내기/가져오기)이 안 되기 때문에 ViewTemplateId/Name과 같은 이유로 이름을 같이 둔다.
    // 모든 항목이 nullable인 이유: "이 속성은 재정의하지 않음"(null)과 "재정의해서 특정 값으로 설정함"을
    // 구분해야 하기 때문 - null이면 Toggle 시 그 속성을 아예 건드리지 않는다.
    public class CategoryOverrideConfig
    {
        public int CategoryId { get; set; }
        public string CategoryName { get; set; } = "";
        // 최상위 카테고리면 null. 같은 이름의 하위 카테고리가 서로 다른 상위 카테고리에 있을 수 있어
        // (예: 여러 카테고리가 공유하는 서브카테고리 이름) 가져오기 시 이름만으로는 매칭이 모호할 수
        // 있으므로 부모 이름까지 같이 저장해 매칭 정확도를 높인다.
        public string? ParentCategoryName { get; set; }

        // true = 표시, false = 숨김, null = 이 프리셋에서 표시 여부는 건드리지 않음(재정의 값만 적용).
        public bool? Visible { get; set; }
        public bool? Halftone { get; set; }
        // ViewDetailLevel enum 이름 문자열(Coarse/Medium/Fine), null = 재정의 안 함.
        public string? DetailLevel { get; set; }
        public int? Transparency { get; set; } // 0~100

        public int? ProjectionLineWeight { get; set; }
        public int? ProjectionLineColor { get; set; }
        public string? ProjectionLinePatternName { get; set; }

        public int? CutLineWeight { get; set; }
        public int? CutLineColor { get; set; }
        public string? CutLinePatternName { get; set; }

        public bool? SurfaceForegroundVisible { get; set; }
        public string? SurfaceForegroundPatternName { get; set; }
        public int? SurfaceForegroundColor { get; set; }
        public bool? SurfaceBackgroundVisible { get; set; }
        public string? SurfaceBackgroundPatternName { get; set; }
        public int? SurfaceBackgroundColor { get; set; }

        public bool? CutForegroundVisible { get; set; }
        public string? CutForegroundPatternName { get; set; }
        public int? CutForegroundColor { get; set; }
        public bool? CutBackgroundVisible { get; set; }
        public string? CutBackgroundPatternName { get; set; }
        public int? CutBackgroundColor { get; set; }

        public CategoryOverrideConfig Clone() => new CategoryOverrideConfig
        {
            CategoryId = CategoryId,
            CategoryName = CategoryName,
            ParentCategoryName = ParentCategoryName,
            Visible = Visible,
            Halftone = Halftone,
            DetailLevel = DetailLevel,
            Transparency = Transparency,
            ProjectionLineWeight = ProjectionLineWeight,
            ProjectionLineColor = ProjectionLineColor,
            ProjectionLinePatternName = ProjectionLinePatternName,
            CutLineWeight = CutLineWeight,
            CutLineColor = CutLineColor,
            CutLinePatternName = CutLinePatternName,
            SurfaceForegroundVisible = SurfaceForegroundVisible,
            SurfaceForegroundPatternName = SurfaceForegroundPatternName,
            SurfaceForegroundColor = SurfaceForegroundColor,
            SurfaceBackgroundVisible = SurfaceBackgroundVisible,
            SurfaceBackgroundPatternName = SurfaceBackgroundPatternName,
            SurfaceBackgroundColor = SurfaceBackgroundColor,
            CutForegroundVisible = CutForegroundVisible,
            CutForegroundPatternName = CutForegroundPatternName,
            CutForegroundColor = CutForegroundColor,
            CutBackgroundVisible = CutBackgroundVisible,
            CutBackgroundPatternName = CutBackgroundPatternName,
            CutBackgroundColor = CutBackgroundColor,
        };
    }

    // 프로젝트 파일 경로별로 저장되는 설정 (이 PC 안에서만 유지 - Q&A로 확정).
    public class QuickToggleSettings
    {
        // 목록의 순서가 곧 툴바에 표시되는 버튼 순서.
        public List<QuickToggleButtonConfig> Buttons { get; set; } = new List<QuickToggleButtonConfig>();
        public bool ToolbarVisible { get; set; } = true;

        // 디버깅/향후 마이그레이션 참고용으로 원본 경로도 같이 저장한다 (해시만으로는 사람이 못 알아봄).
        public string ProjectPath { get; set; } = "";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        internal static string RootDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WallSplitter", "quick-toggle");

        // 저장 안 된 새 문서는 PathName이 비어 있어 프로젝트별로 저장할 곳이 없다 - 이 경우 null을 반환하고
        // 호출자는 툴바를 비활성 상태로 표시해야 한다 (최초 저장 후부터 정상 동작).
        public static string? PathFor(Document doc)
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

        public static QuickToggleSettings Load(Document doc)
        {
            string? path = PathFor(doc);
            if (path != null)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        string json = File.ReadAllText(path, Encoding.UTF8);
                        QuickToggleSettings? loaded = JsonSerializer.Deserialize<QuickToggleSettings>(json, JsonOptions);
                        if (loaded != null)
                        {
                            loaded.Buttons ??= new List<QuickToggleButtonConfig>();
                            return loaded;
                        }
                    }
                }
                catch
                {
                    // 설정 파일이 손상된 경우 빈 설정으로 대체
                }
            }
            return new QuickToggleSettings { ProjectPath = doc?.PathName ?? "" };
        }

        public void Save(Document doc)
        {
            string? path = PathFor(doc);
            if (path == null)
                throw new InvalidOperationException("저장되지 않은 문서에는 커스텀 버튼 설정을 저장할 수 없습니다. 먼저 프로젝트 파일을 저장하세요.");

            ProjectPath = doc.PathName;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json, Encoding.UTF8);
        }

        // 같은 카테고리 내에서 "뷰템플릿버튼1", "뷰템플릿버튼2"처럼 다음 번호를 붙인 기본 이름을 만든다.
        public string NextDefaultName(QuickToggleCategory category)
        {
            string prefix = category switch
            {
                QuickToggleCategory.ViewTemplate => "뷰템플릿버튼",
                QuickToggleCategory.Filter => "필터버튼",
                QuickToggleCategory.Workset => "작업세트버튼",
                QuickToggleCategory.Preset => "프리셋버튼",
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
