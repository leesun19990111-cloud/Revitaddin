using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // "기능 버튼"(QuickToggleCategory.CommandLauncher, 2026-08-03 추가 - "커스텀 버튼 설정에 재료지정/
    // 네이머/동기화 등 다른 기능도 버튼으로 추가하고 싶다"는 요청)이 검색해서 고를 수 있는 명령 목록.
    // Sunny Tools 자체 명령은 App.cs가 리본 버튼으로 등록할 때 쓴 것과 똑같은 IExternalCommand 클래스의
    // FullName을 쓴다(RevitCommandId.LookupCommandId가 이 문자열로 조회한다 - App.cs의 PushButtonData
    // 생성자에 넘기는 className 인자와 같은 값). Revit 기본 명령은 PostableCommand enum 전체를 대상으로
    // 하는데, 수백 개라 검색어 없이는 나열하지 않는다(QuickToggleSettingsWindow.RenderCommandList 참고 -
    // 애초에 "찾아서 추가하고 싶다"는 요청이었다).
    internal static class SunnyToolsCommands
    {
        public static readonly IReadOnlyList<(string Label, string ClassName)> All = new List<(string, string)>
        {
            ("벽체 분리", typeof(SplitWallCommand).FullName!),
            ("바닥 분리", typeof(SplitFloorCommand).FullName!),
            ("벽체/바닥 분리 설정", typeof(SettingsCommand).FullName!),
            ("단일/복수 전환 (벽체/바닥 분리)", typeof(ToggleTypeAssignmentPersistenceCommand).FullName!),
            ("NAMER", typeof(NamerCommand).FullName!),
            ("재료 지정", typeof(MaterialAssignCommand).FullName!),
            ("모델간 변경 반영", typeof(ModelSyncCommand).FullName!),
            ("패턴 스튜디오", typeof(PatternStudioCommand).FullName!),
            ("모델선 패턴 캡처", typeof(ModelLinePatternCaptureCommand).FullName!),
            ("패턴 타공", typeof(PatternPunchCommand).FullName!),
            ("패턴 타공 복원", typeof(PatternPunchRestoreCommand).FullName!),
            ("커스텀 버튼 설정", typeof(QuickToggleSettingsCommand).FullName!),
            ("커스텀 버튼 표시/숨김 전환", typeof(QuickToggleVisibilityToggleCommand).FullName!),
        };

        // PostableCommand enum 이름(예: "SyncWithCentral")을 검색 가능한 라벨로 바꾼다 - 수백 개 전부에
        // 한글 이름을 붙이는 건 이번 요청 범위를 넘어서고, Revit 자체 매크로/커스터마이즈 UI도 이 영문
        // 이름을 그대로 노출한다. 대문자 경계에 공백만 삽입("SyncWithCentral" -> "Sync With Central").
        private static string FriendlyLabel(string enumName) =>
            Regex.Replace(enumName, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");

        private static readonly Lazy<List<(string Label, string Name)>> NativeCommands = new(() =>
            Enum.GetNames(typeof(PostableCommand))
                .Select(name => (Label: FriendlyLabel(name), Name: name))
                .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                .ToList());

        // 검색어가 없으면 빈 목록을 반환한다 - 필터 없이 전체(수백 개)를 그리면 설정 창이 느려지고 원하는
        // 항목을 찾기도 오히려 어렵다.
        public static List<(string Label, string Name)> SearchNativeCommands(string filter, int maxResults)
        {
            if (string.IsNullOrWhiteSpace(filter)) return new List<(string, string)>();
            return NativeCommands.Value
                .Where(c => c.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(maxResults)
                .ToList();
        }
    }
}
