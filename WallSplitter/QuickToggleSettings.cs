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

        // 사용자가 버튼마다 아이콘 모양/on 상태 색을 직접 고를 수 있게 해달라는 요청(2026-07-27)으로 추가.
        // 둘 다 null이면 예전 그대로 카테고리 기본 아이콘(QuickToggleIcons.DefaultFor)과 공용 on 색
        // (Theme.ToggleOn)을 쓴다 - 기존에 저장된 설정 파일도 그대로 호환된다.
        public QuickToggleIconShape? IconShape { get; set; }
        public string? OnColorHex { get; set; }
    }

    // 프로젝트 파일 경로별로 저장되는 설정 (이 PC 안에서만 유지 - Q&A로 확정).
    public class QuickToggleSettings
    {
        // 목록의 순서가 곧 툴바에 표시되는 버튼 순서.
        public List<QuickToggleButtonConfig> Buttons { get; set; } = new List<QuickToggleButtonConfig>();
        public bool ToolbarVisible { get; set; } = true;

        // 툴바의 위치 - Revit 메인 창 좌상단으로부터의 오프셋(px)으로 저장한다. 메인 창이 움직이면 이
        // 오프셋만큼 계속 따라다니되(원래 요청사항), 오프셋 값 자체는 더 이상 고정 상수가 아니라 사용자가
        // 툴바를 마우스로 직접 드래그하면 그 즉시 갱신·저장된다(QuickToggleToolbar.RootBorder_MouseLeftButtonDown
        // 참고) - "리본 버튼을 가리는 고정 위치라 불편하다"는 실측 피드백(2026-07-27)으로 순수 고정 배치를
        // 포기하고 드래그 가능한 패널로 바꿨다. 기본값은 예전 고정 오프셋과 비슷하게 잡아둔 초기값일 뿐이다.
        public int ToolbarOffsetXDip { get; set; } = 6;
        public int ToolbarOffsetYDip { get; set; } = 130;

        // 디버깅/향후 마이그레이션 참고용으로 원본 경로도 같이 저장한다 (해시만으로는 사람이 못 알아봄).
        public string ProjectPath { get; set; } = "";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string RootDir => Path.Combine(
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
                throw new InvalidOperationException("저장되지 않은 문서에는 빠른 토글 설정을 저장할 수 없습니다. 먼저 프로젝트 파일을 저장하세요.");

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
                _ => "버튼",
            };
            int count = 0;
            foreach (QuickToggleButtonConfig b in Buttons)
                if (b.Category == category) count++;
            return prefix + (count + 1);
        }
    }
}
