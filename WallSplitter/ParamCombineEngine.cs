using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using RevitDocument = Autodesk.Revit.DB.Document;

namespace WallSplitter
{
    // 한 요소에 규칙을 적용한 결과. 화면에 "몇 개를 바꿨고 왜 못 바꿨는지"를 그대로 보여주기 위해
    // 실패 사유를 종류별로 구분한다.
    public enum CombineOutcome
    {
        Changed,
        AlreadySame,
        TargetMissing,
        TargetReadOnly,
        TargetNotText,
        SkippedEmptySource,
        Failed,
    }

    public class CombineRunResult
    {
        public int Changed;
        public int AlreadySame;
        public int TargetMissing;
        public int TargetReadOnly;
        public int TargetNotText;
        public int SkippedEmptySource;
        public int Failed;

        public int Total => Changed + AlreadySame + TargetMissing + TargetReadOnly + TargetNotText + SkippedEmptySource + Failed;

        public void Add(CombineOutcome outcome)
        {
            switch (outcome)
            {
                case CombineOutcome.Changed: Changed++; break;
                case CombineOutcome.AlreadySame: AlreadySame++; break;
                case CombineOutcome.TargetMissing: TargetMissing++; break;
                case CombineOutcome.TargetReadOnly: TargetReadOnly++; break;
                case CombineOutcome.TargetNotText: TargetNotText++; break;
                case CombineOutcome.SkippedEmptySource: SkippedEmptySource++; break;
                default: Failed++; break;
            }
        }

        public void Merge(CombineRunResult other)
        {
            Changed += other.Changed;
            AlreadySame += other.AlreadySame;
            TargetMissing += other.TargetMissing;
            TargetReadOnly += other.TargetReadOnly;
            TargetNotText += other.TargetNotText;
            SkippedEmptySource += other.SkippedEmptySource;
            Failed += other.Failed;
        }

        // projected=true면 아직 쓰지 않은 "이렇게 바뀔 예정"이라는 말투로 바꾼다(설정 창 미리보기용).
        public string Summary(bool projected = false)
        {
            List<string> lines = new List<string>
            {
                projected
                    ? "요소 " + Total + "개 중 " + Changed + "개가 바뀝니다."
                    : "요소 " + Total + "개 중 " + Changed + "개를 갱신했습니다.",
            };
            if (AlreadySame > 0) lines.Add("이미 같은 값인 " + AlreadySame + "개는 그대로 둡니다.");
            if (SkippedEmptySource > 0) lines.Add("소스 값이 비어 건너뛴 요소 " + SkippedEmptySource + "개.");
            if (TargetMissing > 0) lines.Add("대상 매개변수가 없는 요소 " + TargetMissing + "개.");
            if (TargetNotText > 0) lines.Add("대상 매개변수가 문자 형식이 아닌 요소 " + TargetNotText + "개.");
            if (TargetReadOnly > 0) lines.Add("대상 매개변수가 읽기 전용인 요소 " + TargetReadOnly + "개.");
            if (Failed > 0) lines.Add("쓰지 못한 요소 " + Failed + "개.");
            return string.Join(" ", lines);
        }
    }

    public class CombinePreviewRow
    {
        public string ElementLabel { get; set; } = "";
        public string CurrentValue { get; set; } = "";
        public string NewValue { get; set; } = "";
        public CombineOutcome Outcome { get; set; }
        public bool WillChange => Outcome == CombineOutcome.Changed;
    }

    // 규칙을 실제 Revit 요소에 적용하는 계산부. 트랜잭션은 절대 열지 않는다 - 호출자가 연 트랜잭션
    // (전체 갱신 명령) 또는 Revit이 이미 열어둔 트랜잭션(IUpdater.Execute) 안에서 그대로 쓰이기 때문이다.
    internal static class ParamCombineEngine
    {
        // ===== 값 읽기 =====

        // 내장/공유/프로젝트 매개변수를 이름으로 찾는다. 인스턴스에 없으면 유형에서도 찾아본다
        // (용도/지상지하처럼 유형 매개변수로 만들어 둔 프로젝트도 있기 때문 - 읽기만 하므로 안전하다).
        internal static Parameter? FindReadableParameter(Element element, string name)
        {
            if (element == null || string.IsNullOrWhiteSpace(name)) return null;

            try
            {
                Parameter? p = element.LookupParameter(name);
                if (p != null) return p;
            }
            catch
            {
                // LookupParameter는 일부 요소에서 예외를 던질 수 있다 - 아래 유형 조회로 넘어간다.
            }

            try
            {
                ElementId typeId = element.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId &&
                    element.Document.GetElement(typeId) is Element type)
                {
                    return type.LookupParameter(name);
                }
            }
            catch
            {
                // 유형이 없는 요소(룸 등)는 여기로 온다 - 매개변수가 없는 것으로 취급한다.
            }

            return null;
        }

        // 결과를 쓸 대상은 반드시 그 요소 자신의 인스턴스 매개변수여야 한다 - 유형 매개변수에 쓰면
        // 같은 유형을 쓰는 다른 요소 전부가 같이 바뀌어버린다.
        internal static Parameter? FindWritableParameter(Element element, string name)
        {
            if (element == null || string.IsNullOrWhiteSpace(name)) return null;
            try { return element.LookupParameter(name); }
            catch { return null; }
        }

        // 매개변수 값을 "합치기 좋은 문자열"로 바꾼다. 숫자는 단위 문자열(AsValueString)이 아니라 숫자
        // 자체를 쓴다 - 자리수 채움의 대상이기 때문이다(층 1 -> "1" -> "01").
        internal static string ReadValue(Element element, string parameterName)
        {
            Parameter? p = FindReadableParameter(element, parameterName);
            if (p == null || !p.HasValue) return "";

            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        return p.AsString() ?? "";

                    case StorageType.Integer:
                        return p.AsInteger().ToString(CultureInfo.InvariantCulture);

                    case StorageType.Double:
                    {
                        // 길이/면적처럼 단위가 붙는 값은 AsValueString이 사용자가 보는 그대로라 더 낫지만,
                        // 거기에는 단위 기호와 천 단위 구분자가 섞여 있어 실번호에는 못 쓴다.
                        // 소수점 이하가 없으면 정수로, 있으면 불필요한 0을 뗀 형태로 쓴다.
                        double v = p.AsDouble();
                        return Math.Abs(v - Math.Round(v)) < 1e-9
                            ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture)
                            : v.ToString("0.####", CultureInfo.InvariantCulture);
                    }

                    case StorageType.ElementId:
                    {
                        ElementId id = p.AsElementId();
                        if (id == null || id == ElementId.InvalidElementId) return "";
                        // 레벨처럼 ElementId로 저장되는 매개변수는 그 요소의 이름이 사용자가 보는 값이다.
                        Element? referenced = element.Document.GetElement(id);
                        return referenced?.Name ?? "";
                    }
                }
            }
            catch
            {
                // 값을 못 읽으면 빈 값으로 취급한다 - 한 매개변수 때문에 규칙 전체가 멈추면 안 된다.
            }

            return "";
        }

        // ===== 자리수 맞춤 =====

        internal static string ApplyWidth(string raw, CombineSourceField field)
        {
            string value = raw ?? "";
            if (field.Width <= 0) return value;

            if (value.Length > field.Width)
            {
                if (!field.Truncate) return value;
                // 앞을 채우는(숫자) 설정이면 뒤쪽 자리(=작은 자리)를 남기고, 뒤를 채우는(코드) 설정이면 앞쪽을 남긴다.
                return field.Pad == PadSide.Left
                    ? value.Substring(value.Length - field.Width)
                    : value.Substring(0, field.Width);
            }

            if (string.IsNullOrEmpty(field.PadChar)) return value;
            char pad = field.PadChar[0];
            return field.Pad == PadSide.Left
                ? value.PadLeft(field.Width, pad)
                : value.PadRight(field.Width, pad);
        }

        // ===== 결합 =====

        internal static string Build(Element element, CombineRule rule, out bool anySourceEmpty)
        {
            anySourceEmpty = false;
            StringBuilder sb = new StringBuilder();

            for (int i = 0; i < rule.Sources.Count; i++)
            {
                CombineSourceField field = rule.Sources[i];
                string raw = ReadValue(element, field.ParameterName);
                if (string.IsNullOrEmpty(raw)) anySourceEmpty = true;

                sb.Append(ApplyWidth(raw, field));
                if (i < rule.Sources.Count - 1) sb.Append(field.SeparatorAfter ?? "");
            }

            return sb.ToString();
        }

        // ===== 쓰기 =====

        // 호출자가 이미 트랜잭션을 열어둔 상태여야 한다. 값이 같으면 아무것도 쓰지 않는다 -
        // IUpdater가 자기가 만든 변경으로 다시 불려도 두 번째 패스에서 곧바로 멈추게 하는 안전장치이기도 하다.
        internal static CombineOutcome Apply(Element element, CombineRule rule)
        {
            if (element == null || rule.Sources.Count == 0) return CombineOutcome.Failed;

            Parameter? target = FindWritableParameter(element, rule.TargetParameterName);
            if (target == null) return CombineOutcome.TargetMissing;
            if (target.StorageType != StorageType.String) return CombineOutcome.TargetNotText;
            if (target.IsReadOnly) return CombineOutcome.TargetReadOnly;

            string value = Build(element, rule, out bool anyEmpty);
            if (anyEmpty && rule.SkipWhenAnySourceEmpty) return CombineOutcome.SkippedEmptySource;

            string current = target.AsString() ?? "";
            if (string.Equals(current, value, StringComparison.Ordinal)) return CombineOutcome.AlreadySame;

            try
            {
                return target.Set(value) ? CombineOutcome.Changed : CombineOutcome.Failed;
            }
            catch
            {
                // 작업공유 모델에서 다른 사용자가 소유한 요소 등 - 나머지 요소는 계속 처리한다.
                return CombineOutcome.Failed;
            }
        }

        // ===== 문서 전체 =====

        internal static IList<Element> Collect(RevitDocument doc, CombineRule rule)
        {
            try
            {
                return new FilteredElementCollector(doc)
                    .WherePasses(new ElementCategoryFilter(new ElementId(rule.CategoryId)))
                    .WhereElementIsNotElementType()
                    .ToElements();
            }
            catch
            {
                return new List<Element>();
            }
        }

        // 트랜잭션은 호출자가 연다(RunAll은 여러 규칙을 한 트랜잭션에 묶어 Ctrl+Z 한 번으로 되돌리게 하기 위함).
        internal static CombineRunResult RunAll(RevitDocument doc, ParamCombineSettings settings)
        {
            CombineRunResult total = new CombineRunResult();
            foreach (CombineRule rule in settings.ActiveRules)
                total.Merge(Run(doc, rule));
            return total;
        }

        internal static CombineRunResult Run(RevitDocument doc, CombineRule rule)
        {
            CombineRunResult result = new CombineRunResult();
            foreach (Element element in Collect(doc, rule))
                result.Add(Apply(element, rule));
            return result;
        }

        // ===== 미리보기 (아무것도 쓰지 않는다) =====

        internal static List<CombinePreviewRow> Preview(RevitDocument doc, CombineRule rule, int maxRows, out CombineRunResult projected)
        {
            projected = new CombineRunResult();
            List<CombinePreviewRow> rows = new List<CombinePreviewRow>();

            foreach (Element element in Collect(doc, rule))
            {
                CombineOutcome outcome;
                string current = "";
                string next = "";

                Parameter? target = FindWritableParameter(element, rule.TargetParameterName);
                if (target == null) outcome = CombineOutcome.TargetMissing;
                else if (target.StorageType != StorageType.String) outcome = CombineOutcome.TargetNotText;
                else if (target.IsReadOnly) outcome = CombineOutcome.TargetReadOnly;
                else
                {
                    current = target.AsString() ?? "";
                    next = Build(element, rule, out bool anyEmpty);
                    if (anyEmpty && rule.SkipWhenAnySourceEmpty) outcome = CombineOutcome.SkippedEmptySource;
                    else if (string.Equals(current, next, StringComparison.Ordinal)) outcome = CombineOutcome.AlreadySame;
                    else outcome = CombineOutcome.Changed;
                }

                projected.Add(outcome);

                if (rows.Count < maxRows)
                {
                    rows.Add(new CombinePreviewRow
                    {
                        ElementLabel = Describe(element),
                        CurrentValue = current,
                        NewValue = next,
                        Outcome = outcome,
                    });
                }
            }

            return rows;
        }

        private static string Describe(Element element)
        {
            try
            {
                string name = element.Name ?? "";
                return string.IsNullOrWhiteSpace(name)
                    ? "ID " + element.Id.ToInt()
                    : name + " (ID " + element.Id.ToInt() + ")";
            }
            catch
            {
                return "요소";
            }
        }

        // ===== 설정 창이 쓰는 목록들 =====

        // 매개변수를 붙일 수 있는 카테고리만 모은다(이름순). 카테고리 id는 문서가 달라도 같은 내장 값이다.
        internal static List<KeyValuePair<int, string>> BindableCategories(RevitDocument doc)
        {
            List<KeyValuePair<int, string>> result = new List<KeyValuePair<int, string>>();
            try
            {
                foreach (Category category in doc.Settings.Categories)
                {
                    if (category == null || !category.AllowsBoundParameters) continue;
                    int id = category.Id.ToInt();
                    // 내장 카테고리(음수 id)만 - 사용자 정의 하위 카테고리는 문서마다 id가 달라 설정에 저장할 수 없다.
                    if (id >= 0) continue;
                    result.Add(new KeyValuePair<int, string>(id, category.Name));
                }
            }
            catch
            {
                // 카테고리 조회 실패 시 빈 목록 - 설정 창은 직접 입력한 이름으로도 동작한다.
            }

            return result.OrderBy(c => c.Value, StringComparer.CurrentCulture).ToList();
        }

        // 그 카테고리에서 고를 수 있는 매개변수 이름들. 실제 요소 하나를 표본으로 훑는 방법(내장 매개변수까지
        // 전부 나온다)과 프로젝트에 바인딩된 공유/프로젝트 매개변수를 훑는 방법(요소가 아직 하나도 없어도
        // 나온다)을 합친다.
        internal static List<string> ParameterNames(RevitDocument doc, int categoryId)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                Element? sample = new FilteredElementCollector(doc)
                    .WherePasses(new ElementCategoryFilter(new ElementId(categoryId)))
                    .WhereElementIsNotElementType()
                    .FirstElement();

                if (sample != null)
                {
                    foreach (Parameter p in sample.Parameters)
                        if (!string.IsNullOrWhiteSpace(p?.Definition?.Name)) names.Add(p.Definition.Name);

                    ElementId typeId = sample.GetTypeId();
                    if (typeId != null && typeId != ElementId.InvalidElementId &&
                        doc.GetElement(typeId) is Element type)
                    {
                        foreach (Parameter p in type.Parameters)
                            if (!string.IsNullOrWhiteSpace(p?.Definition?.Name)) names.Add(p.Definition.Name);
                    }
                }
            }
            catch
            {
                // 표본 요소가 없거나 조회에 실패하면 아래 바인딩 목록만 쓴다.
            }

            try
            {
                DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
                while (it.MoveNext())
                {
                    if (!(it.Key is Definition definition)) continue;
                    if (!(it.Current is ElementBinding binding)) continue;
                    foreach (Category category in binding.Categories)
                    {
                        if (category != null && category.Id.ToInt() == categoryId)
                        {
                            names.Add(definition.Name);
                            break;
                        }
                    }
                }
            }
            catch
            {
                // 바인딩 조회 실패는 무시 - 위 표본 목록만으로도 대부분 충분하다.
            }

            return names.OrderBy(n => n, StringComparer.CurrentCulture).ToList();
        }
    }
}
