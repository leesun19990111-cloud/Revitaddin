using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // "기능 버튼"(QuickToggleCategory.CommandLauncher, 2026-08-03 추가 - "커스텀 버튼 설정에 재료지정/
    // 네이머/동기화 등 다른 기능도 버튼으로 추가하고 싶다"는 요청)이 검색해서 고를 수 있는 명령 목록.
    // Sunny Tools 자체 명령은 설정 파일에 IExternalCommand 클래스의 FullName으로 저장한다 - 문서와 무관한
    // 안정적인 식별자라서(실제 Revit 명령 id로 바꾸는 방법은 아래 RibbonCommandIds 참고). Revit 기본
    // 명령은 PostableCommand enum 전체를 대상으로 하는데, 수백 개라 검색어 없이는 나열하지 않는다
    // (QuickToggleSettingsWindow.RenderCommandList 참고).
    internal static class SunnyToolsCommands
    {
        private const string CommandLabelsResourceName = "WallSplitter.Resources.RevitCommandLabels.2027.tsv";

        // ===== Sunny Tools 자체 명령의 Revit 명령 id (클래스 FullName → id) =====
        //
        // **CONFIRMED LIVE BUG (2026-09-03) — 기능 버튼이 항상 "실행하지 못했습니다"로 끝나던 원인.**
        // 2026-08-03 최초 구현은 `RevitCommandId.LookupCommandId(클래스 FullName)`으로 조회했는데, 그건
        // `.addin` 매니페스트에 `<AddIn Type="Command">`로 등록된 외부 명령에만 통한다. 이 프로젝트의
        // 매니페스트에는 `<AddIn Type="Application">` 하나뿐이고 명령은 전부 `App.OnStartup`의
        // `PushButtonData`로 리본에만 등록되므로, Revit의 명령 레지스트리에 "클래스 이름"이라는 키가 아예
        // 없다 → `LookupCommandId`가 늘 null → `RunCommand`가 false → 실패 팝업만 떴다.
        //
        // Revit이 실제로 쓰는 id는 **리본 경로**다. 저널(`%LOCALAPPDATA%\Autodesk\Revit\...\Journals`)에서
        // 실측한 실제 문자열:
        //   Jrn.RibbonEvent "Execute external command:
        //     CustomCtrl_%CustomCtrl_%Sunny Tools%커스텀 버튼%WallSplitter_QuickToggleSettings
        //     :WallSplitter.QuickToggleSettingsCommand"
        // 즉 `CustomCtrl_%CustomCtrl_%<탭 이름>%<패널 이름>%<PushButtonData의 internal name>` 이다
        // (`LookupCommandId` API 문서도 "저널에 있는 문자열을 쓰라"고만 말한다). `CanPostCommand` 문서의
        // "Only members of PostableCommand **or external commands** can be posted"도 이 경로가 맞다는 근거다.
        //
        // **이 문자열을 여기에 손으로 적어두지 말 것** - 패널/버튼 이름을 바꾸는 순간 조용히 깨진다.
        // `App.OnStartup`이 리본을 다 만든 뒤 실제로 만들어진 `RibbonPanel`/`PushButton`에서 읽어
        // `RegisterRibbonCommandId`로 채운다(App.RegisterRibbonCommandIds).
        private static readonly Dictionary<string, string> RibbonCommandIds =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // 같은 명령이 여러 패널에 중복 등록될 수 있다(예: SettingsCommand는 벽체 분리/바닥 분리 패널에
        // 각각 있다) - 어느 버튼을 눌러도 같은 명령이므로 처음 등록된 것만 쓴다.
        public static void RegisterRibbonCommandId(string className, string commandId)
        {
            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(commandId)) return;
            if (!RibbonCommandIds.ContainsKey(className)) RibbonCommandIds[className] = commandId;
        }

        public static string? RibbonCommandIdFor(string className) =>
            RibbonCommandIds.TryGetValue(className, out string? id) ? id : null;

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
            ("경고Pick", typeof(WarningPickCommand).FullName!),
            ("커스텀 버튼 설정", typeof(QuickToggleSettingsCommand).FullName!),
            ("커스텀 버튼 표시/숨김 전환", typeof(QuickToggleVisibilityToggleCommand).FullName!),
        };

        private sealed class NativeCommandInfo
        {
            public string EnumName { get; set; } = "";
            public string InternalName { get; set; } = "";
            public string EnglishLabel { get; set; } = "";
            public string KoreanLabel { get; set; } = "";

            public string DisplayLabel(LanguageType language) =>
                language == LanguageType.Korean && !string.IsNullOrWhiteSpace(KoreanLabel)
                    ? KoreanLabel
                    : EnglishLabel;

            public bool Matches(string filter) =>
                Contains(EnglishLabel, filter) ||
                Contains(KoreanLabel, filter) ||
                Contains(EnumName, filter) ||
                Contains(InternalName, filter);

            private static bool Contains(string value, string filter) =>
                !string.IsNullOrEmpty(value) && value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Autodesk가 배포한 Revit 2027 영문/한국어 기본 명령표를 CommandId 기준으로 합친 리소스다.
        // RevitCommandId의 이름 자체는 비지역화 문자열이므로, 실행용 PostableCommand와 사람이 읽는
        // 현지화 이름을 이 CommandId로 연결한다. 리소스를 못 읽거나 매칭되지 않는 명령은 enum 이름을
        // 읽기 좋게 다듬은 영문 이름으로 안전하게 되돌아간다.
        private static readonly Lazy<Dictionary<string, (string English, string Korean)>> LocalizedLabels =
            new Lazy<Dictionary<string, (string English, string Korean)>>(LoadLocalizedLabels);

        private static readonly Lazy<List<NativeCommandInfo>> NativeCommands =
            new Lazy<List<NativeCommandInfo>>(BuildNativeCommands);

        private static string FriendlyLabel(string enumName) =>
            Regex.Replace(enumName, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");

        private static Dictionary<string, (string English, string Korean)> LoadLocalizedLabels()
        {
            Dictionary<string, (string English, string Korean)> labels =
                new Dictionary<string, (string English, string Korean)>(StringComparer.OrdinalIgnoreCase);

            try
            {
                Assembly assembly = typeof(SunnyToolsCommands).Assembly;
                using Stream? stream = assembly.GetManifestResourceStream(CommandLabelsResourceName);
                if (stream == null) return labels;

                using StreamReader reader = new StreamReader(stream, Encoding.UTF8, true);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] columns = line.Split(new[] { '\t' }, 3, StringSplitOptions.None);
                    if (columns.Length != 3 || string.IsNullOrWhiteSpace(columns[0])) continue;
                    labels[columns[0]] = (columns[1], columns[2]);
                }
            }
            catch
            {
                // 지역화 리소스 오류 때문에 기능 버튼 설정 전체가 열리지 않는 일은 없어야 한다.
            }

            return labels;
        }

        private static List<NativeCommandInfo> BuildNativeCommands()
        {
            List<NativeCommandInfo> commands = new List<NativeCommandInfo>();

            foreach (string enumName in Enum.GetNames(typeof(PostableCommand)))
            {
                string internalName = "";
                try
                {
                    PostableCommand command = (PostableCommand)Enum.Parse(typeof(PostableCommand), enumName);
                    RevitCommandId? commandId = RevitCommandId.LookupPostableCommandId(command);
                    if (commandId != null)
                        internalName = commandId.Name ?? "";
                }
                catch
                {
                    // 특정 Revit 버전에서 명령 ID 조회가 실패해도 나머지 명령 목록은 계속 제공한다.
                }

                string english = FriendlyLabel(enumName);
                string korean = "";
                if (!string.IsNullOrWhiteSpace(internalName) &&
                    LocalizedLabels.Value.TryGetValue(internalName, out (string English, string Korean) localized))
                {
                    if (!string.IsNullOrWhiteSpace(localized.English)) english = localized.English;
                    korean = localized.Korean;
                }

                commands.Add(new NativeCommandInfo
                {
                    EnumName = enumName,
                    InternalName = internalName,
                    EnglishLabel = english,
                    KoreanLabel = korean,
                });
            }

            return commands;
        }

        // 검색어가 없으면 빈 목록을 반환한다 - 필터 없이 전체(수백 개)를 그리면 설정 창이 느려지고 원하는
        // 항목을 찾기도 오히려 어렵다. 검색은 표시 언어와 무관하게 영문·한국어·enum·내부 ID를 모두
        // 대상으로 하며, 결과 이름만 현재 Revit 언어에 맞춘다.
        public static List<(string Label, string Name)> SearchNativeCommands(
            string filter, int maxResults, LanguageType displayLanguage)
        {
            if (string.IsNullOrWhiteSpace(filter)) return new List<(string, string)>();
            string normalizedFilter = filter.Trim();

            return NativeCommands.Value
                .Where(c => c.Matches(normalizedFilter))
                .Select(c => (Label: c.DisplayLabel(displayLanguage), Name: c.EnumName))
                .OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
                .Take(maxResults)
                .ToList();
        }

        // CommandLabel은 예전 설정 파일과 매칭 실패 때를 위한 fallback으로 계속 보존한다. 평소에는 저장된
        // 언어에 묶이지 않고 현재 Revit 언어를 기준으로 다시 표시해, 같은 설정을 다른 언어판에서 열어도
        // 설정 창과 툴팁이 그 언어를 따른다.
        public static string DisplayLabelFor(
            QuickToggleCommandKind? kind, string? id, LanguageType displayLanguage, string? fallback)
        {
            if (kind == QuickToggleCommandKind.SunnyTool && !string.IsNullOrWhiteSpace(id))
            {
                (string Label, string ClassName) match = All.FirstOrDefault(c => c.ClassName == id);
                if (!string.IsNullOrWhiteSpace(match.Label)) return match.Label;
            }

            if (kind == QuickToggleCommandKind.NativeRevit && !string.IsNullOrWhiteSpace(id))
            {
                NativeCommandInfo? match = NativeCommands.Value.FirstOrDefault(c => c.EnumName == id);
                if (match != null) return match.DisplayLabel(displayLanguage);
            }

            return fallback ?? "";
        }
    }
}
