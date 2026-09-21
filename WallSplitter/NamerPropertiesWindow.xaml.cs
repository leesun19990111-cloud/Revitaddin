using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using WpfGrid = System.Windows.Controls.Grid;

namespace WallSplitter
{
    // NAMER의 '특성' 버튼이 **Revit 기본 창을 열 수 없을 때만** 뜨는 대체 창.
    //
    // 2026-09-21 처음 만들 때는 이게 유일한 수단이었다 - 그때 NAMER는 모달이라 PostCommand로 Revit
    // 기본 창을 여는 것이 구조적으로 불가능했기 때문이다. 같은 날 사용자가 *"오히려 새로 만든게 더
    // 어렵고 보기가 힘들어. 네이머를 띄워두어도 다른작업이 병행가능한 창으로 변경해서라도 특성버튼을
    // 누르면 레빗 자체의 유형편집이나 특성창을 띄울 수 있도록"* 이라고 해서 NAMER를 모드리스로 바꿨고,
    // 이제 기본 동작은 Revit 기본 창이다(NamerNativeProperties 참고). 이 창이 남아 있는 이유는 단
    // 하나, **유형/패밀리를 쓰는 부재가 모델에 하나도 없으면 Revit 유형 특성 창을 열 방법이 정말로
    // 없기 때문**이다(그 명령은 "선택된 부재"를 기준으로 동작하는데 유형 자체는 선택할 수 없다).
    // 그 경우에도 아무것도 못 보는 것보다는 낫기에 남겨 두었다 - 기본 경로로 되돌리지 말 것.
    //
    // 이 창은 **ExternalEvent 안에서 띄운다**(NamerWindow.ShowFallbackProperties). 그 안은 유효한 API
    // 컨텍스트라 아래 Transaction이 그대로 동작한다 - 모드리스 창에서 직접 띄우면 트랜잭션을 못 연다.
    //
    // 반영 시점이 둘로 나뉘는 점에 주의:
    //   - **이름**은 모델에 바로 쓰지 않고 NAMER의 작업 중 이름(_workingNames)으로 돌려준다. NAMER의
    //     2단계 적용 구조(창 안에서 계속 쌓고 '최종 적용'에서 한 번에 반영)를 깨지 않기 위해서다.
    //   - **그 밖의 특성**은 '확인'을 누르는 즉시 자체 Transaction으로 모델에 쓴다. Revit 특성
    //     팔레트와 같은 동작이고, 값 종류가 제각각이라 NAMER처럼 미리보기로 쌓아 둘 수가 없다.
    public partial class NamerPropertiesWindow : Window
    {
        private sealed class ParamRow
        {
            public Parameter Param = null!;
            public string Label = "";
            public string OriginalText = "";
            public bool OriginalChecked;
            public TextBox? Box;
            public CheckBox? Check;
            public FrameworkElement Container = null!;
        }

        private sealed class ParamGroup
        {
            public TextBlock Header = null!;
            public readonly List<ParamRow> Rows = new();
        }

        private readonly Document _doc;
        private readonly Element _element;
        private readonly NamerWindow.NamerCategory _category;
        private readonly string _modelName;
        private readonly List<ParamGroup> _groups = new();
        private readonly List<ParamRow> _rows = new();
        private string _originalMaterialClass = "";
        private readonly string? _fallbackReason;

        // '확인'으로 확정된 새 이름. NamerWindow가 이걸 받아 작업 중 이름에만 반영한다(모델에는 안 씀).
        public string? NewName { get; private set; }

        // internal: NamerCategory가 internal이라 생성자도 같은 수준이어야 한다(CS0051).
        // fallbackReason: Revit 기본 창을 못 열어서 이 창으로 넘어온 이유(있으면 머리말에 그대로 보여준다).
        internal NamerPropertiesWindow(Document doc, Element element, NamerWindow.NamerCategory category,
            string workingName, string? fallbackReason = null)
        {
            InitializeComponent();
            _doc = doc;
            _element = element;
            _category = category;
            _modelName = element.Name ?? "";
            _fallbackReason = fallbackReason;

            NameBox.Text = workingName;
            BuildHeader(workingName);
            BuildMaterialExtras();
            BuildParameters();
            UpdateParamCount();
        }

        // ===================== 머리말 =====================

        internal static string CategoryLabel(NamerWindow.NamerCategory category) => category switch
        {
            NamerWindow.NamerCategory.View => "뷰",
            NamerWindow.NamerCategory.Sheet => "시트",
            NamerWindow.NamerCategory.Family => "패밀리",
            NamerWindow.NamerCategory.Type => "유형",
            NamerWindow.NamerCategory.Legend => "범례",
            NamerWindow.NamerCategory.Schedule => "일람표",
            NamerWindow.NamerCategory.Material => "재료",
            NamerWindow.NamerCategory.ViewTemplate => "뷰 템플릿",
            _ => "항목",
        };

        private void BuildHeader(string workingName)
        {
            Title = "특성 - " + workingName;
            HeaderText.Text = CategoryLabel(_category) + " · " + workingName;

            var parts = new List<string> { "ID " + _element.Id.ToInt() };
            try
            {
                Category? cat = _element.Category;
                if (cat != null && !string.IsNullOrWhiteSpace(cat.Name)) parts.Add("카테고리: " + cat.Name);
            }
            catch { /* 카테고리가 없는 요소(뷰 템플릿 등)는 그냥 생략한다 */ }

            switch (_element)
            {
                case ViewSheet sheet:
                    parts.Add("시트 번호: " + sheet.SheetNumber);
                    break;
                case View view:
                    parts.Add("뷰 종류: " + view.ViewType);
                    break;
                case ElementType type:
                    if (!string.IsNullOrWhiteSpace(type.FamilyName)) parts.Add("패밀리: " + type.FamilyName);
                    break;
                case Material material:
                    if (!string.IsNullOrWhiteSpace(material.MaterialCategory)) parts.Add("재료 카테고리: " + material.MaterialCategory);
                    break;
            }

            // 패밀리는 "이 패밀리에 어떤 유형들이 들어 있나"가 가장 궁금한 정보인데 파라미터로는 전혀
            // 드러나지 않으므로(Family 요소 자체는 파라미터가 거의 없다) 머리말에 직접 적어 준다.
            if (_element is Family family)
            {
                List<string> names = FamilyTypeNames(family);
                parts.Add("유형 " + names.Count + "개");
                if (names.Count > 0)
                {
                    string list = string.Join(", ", names.Take(12));
                    if (names.Count > 12) list += " ... 외 " + (names.Count - 12) + "개";
                    parts.Add(list);
                }
            }

            if (workingName != _modelName)
                parts.Add("모델에 저장된 이름: \"" + _modelName + "\" (NAMER에서 아직 최종 적용 전)");

            // Revit 기본 창으로 못 간 이유가 있으면 맨 앞에 붙인다 - 사용자는 기본 창을 기대하고 눌렀으므로
            // "왜 이 창이 떴는지"를 설명하지 않으면 그냥 고장으로 보인다.
            if (!string.IsNullOrWhiteSpace(_fallbackReason))
                parts.Insert(0, _fallbackReason!.Replace("\n", " "));

            SubHeaderText.Text = string.Join("   ·   ", parts);
        }

        private List<string> FamilyTypeNames(Family family)
        {
            var names = new List<string>();
            try
            {
                foreach (ElementId id in family.GetFamilySymbolIds())
                {
                    Element? symbol = _doc.GetElement(id);
                    if (symbol != null) names.Add(symbol.Name ?? "");
                }
            }
            catch { /* 유형 목록을 못 얻는 경우는 그냥 비워 둔다 */ }
            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            return names;
        }

        // ===================== 재료 전용 칸 =====================

        // 재료의 '클래스'는 파라미터가 아니라 Material.MaterialClass 속성이라 아래 파라미터 목록에는
        // 아예 나타나지 않는다 - 재료 지정 도구(MaterialAssignWindow)와 같은 이유로 여기서 따로 다룬다.
        private void BuildMaterialExtras()
        {
            if (_element is not Material material) return;
            _originalMaterialClass = material.MaterialClass ?? "";
            MaterialClassBox.Text = _originalMaterialClass;
            MaterialPanel.Visibility = System.Windows.Visibility.Visible;
        }

        // ===================== 파라미터 목록 =====================

        private void BuildParameters()
        {
            IList<Parameter> parameters;
            try
            {
                // GetOrderedParameters는 Revit 특성 팔레트에 보이는 것과 같은 순서로 돌려준다.
                parameters = _element.GetOrderedParameters();
            }
            catch
            {
                parameters = _element.Parameters.Cast<Parameter>().ToList();
            }

            var groupLabels = new List<string>();
            var byGroup = new Dictionary<string, List<Parameter>>();
            foreach (Parameter p in parameters)
            {
                if (p == null || p.Definition == null) continue;
                string groupLabel = GroupLabelOf(p.Definition);
                if (!byGroup.TryGetValue(groupLabel, out List<Parameter>? bucket))
                {
                    bucket = new List<Parameter>();
                    byGroup[groupLabel] = bucket;
                    groupLabels.Add(groupLabel);
                }
                bucket.Add(p);
            }

            foreach (string label in groupLabels)
            {
                var group = new ParamGroup
                {
                    Header = new TextBlock
                    {
                        Text = label,
                        FontWeight = FontWeights.Bold,
                        Foreground = Theme.TextSecondary,
                        Margin = new Thickness(0, _groups.Count == 0 ? 0 : 10, 0, 4),
                    },
                };
                ParamsPanel.Children.Add(group.Header);

                foreach (Parameter p in byGroup[label])
                {
                    ParamRow row = CreateParamRow(p);
                    group.Rows.Add(row);
                    _rows.Add(row);
                    ParamsPanel.Children.Add(row.Container);
                }

                _groups.Add(group);
            }

            if (_rows.Count == 0)
                ParamsPanel.Children.Add(new TextBlock
                {
                    Text = "이 요소에는 표시할 특성이 없습니다.",
                    Foreground = Theme.TextSecondary,
                });

            // 설명이 길면 정작 봐야 할 목록이 그만큼 좁아진다 - 커스텀 버튼 설정 창에서 이미 한 번
            // 불편하다고 지적받은 부분이라(2026-09-17), 여기서는 처음부터 한 줄로 줄여 둔다.
            int editable = _rows.Count(r => r.Box != null || r.Check != null);
            NoteText.Text =
                "편집 가능 " + editable + "개 · 읽기 전용 " + (_rows.Count - editable) + "개(회색 - Revit이 잠근 값). " +
                "'확인'을 누르면 특성은 모델에 즉시 저장됩니다 (되돌리기 Ctrl+Z 한 번).";
        }

        // 파라미터 그룹 이름(치수/아이덴티티 데이터/그래픽 ...). 2024부터 BuiltInParameterGroup이
        // ForgeTypeId 기반으로 바뀌었으므로, 5개 연도에 모두 있는 GetGroupTypeId + LabelUtils만 쓴다.
        private static string GroupLabelOf(Definition definition)
        {
            try
            {
                ForgeTypeId group = definition.GetGroupTypeId();
                if (group == null || string.IsNullOrEmpty(group.TypeId)) return "기타";
                string label = LabelUtils.GetLabelForGroup(group);
                return string.IsNullOrWhiteSpace(label) ? "기타" : label;
            }
            catch
            {
                return "기타";
            }
        }

        private ParamRow CreateParamRow(Parameter p)
        {
            var row = new ParamRow { Param = p, Label = p.Definition.Name ?? "" };

            var grid = new WpfGrid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = row.Label,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = row.Label,
            };
            WpfGrid.SetColumn(label, 0);
            grid.Children.Add(label);

            FrameworkElement editor = CreateEditor(p, row);
            WpfGrid.SetColumn(editor, 1);
            grid.Children.Add(editor);

            row.Container = grid;
            return row;
        }

        private FrameworkElement CreateEditor(Parameter p, ParamRow row)
        {
            // 값이 요소 이름과 똑같은 문자열 파라미터는 사실상 "이 요소의 이름" 칸이다(뷰 이름/유형 이름/
            // 시트 이름 ...). 위쪽 '이름' 칸과 같은 것을 두 군데서 따로 고치면 서로 덮어써 버리므로 잠근다.
            bool nameMirror = p.StorageType == StorageType.String
                && !string.IsNullOrEmpty(_modelName)
                && (SafeAsString(p) ?? "") == _modelName;

            bool locked = nameMirror
                || p.IsReadOnly
                || p.StorageType == StorageType.None
                || p.StorageType == StorageType.ElementId;

            if (locked)
            {
                string text = DisplayValue(p);
                if (nameMirror) text += "   (위 '이름' 칸에서 변경)";
                else if (p.StorageType == StorageType.ElementId) text += "   (다른 요소를 가리키는 값)";
                return new TextBlock
                {
                    Text = text,
                    Foreground = Theme.TextSecondary,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            if (p.StorageType == StorageType.Integer && IsYesNo(p))
            {
                row.OriginalChecked = SafeAsInteger(p) != 0;
                var check = new CheckBox
                {
                    IsChecked = row.OriginalChecked,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Check = check;
                return check;
            }

            var box = new TextBox
            {
                Text = EditableText(p),
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(3, 2, 3, 2),
            };
            row.Box = box;
            row.OriginalText = box.Text;
            return box;
        }

        private static bool IsYesNo(Parameter p)
        {
            try
            {
                ForgeTypeId type = p.Definition.GetDataType();
                return type != null && type.TypeId == SpecTypeId.Boolean.YesNo.TypeId;
            }
            catch
            {
                return false;
            }
        }

        private static string? SafeAsString(Parameter p)
        {
            try { return p.AsString(); } catch { return null; }
        }

        private static string SafeAsValueString(Parameter p)
        {
            try { return p.AsValueString() ?? ""; } catch { return ""; }
        }

        private static int SafeAsInteger(Parameter p)
        {
            try { return p.AsInteger(); } catch { return 0; }
        }

        // 편집칸에 넣을 텍스트. 실수/정수는 단위가 붙은 표시 문자열(AsValueString)을 쓰고 저장할 때도
        // 같은 형식을 그대로 해석하는 SetValueString을 쓴다 - 내부 단위(피트) 원시값을 보여 주면
        // 사용자가 "3000"을 입력했을 때 3000피트가 되는 사고가 난다.
        private static string EditableText(Parameter p)
        {
            switch (p.StorageType)
            {
                case StorageType.String:
                    return SafeAsString(p) ?? "";
                case StorageType.Double:
                    return SafeAsValueString(p);
                case StorageType.Integer:
                {
                    string value = SafeAsValueString(p);
                    return value.Length > 0 ? value : SafeAsInteger(p).ToString();
                }
                default:
                    return "";
            }
        }

        private string DisplayValue(Parameter p)
        {
            switch (p.StorageType)
            {
                case StorageType.String:
                    return SafeAsString(p) ?? "";
                case StorageType.Integer:
                {
                    string value = SafeAsValueString(p);
                    return value.Length > 0 ? value : SafeAsInteger(p).ToString();
                }
                case StorageType.Double:
                {
                    string value = SafeAsValueString(p);
                    if (value.Length > 0) return value;
                    try { return p.AsDouble().ToString("0.####"); } catch { return ""; }
                }
                case StorageType.ElementId:
                {
                    try
                    {
                        ElementId id = p.AsElementId();
                        if (id == ElementId.InvalidElementId) return "<없음>";
                        Element? target = _doc.GetElement(id);
                        return target != null ? (target.Name ?? "") : "ID " + id.ToInt();
                    }
                    catch { return ""; }
                }
                default:
                    return "";
            }
        }

        // ===================== 검색 =====================

        private void ParamFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // ComboBox/RadioButton의 초기 이벤트와 같은 이유 - InitializeComponent 도중에도 한 번
            // 발생하므로, 아직 목록을 만들기 전이면 아무것도 하지 않는다.
            if (ParamsPanel == null) return;
            string filter = ParamFilterBox.Text ?? "";

            foreach (ParamGroup group in _groups)
            {
                int visible = 0;
                foreach (ParamRow row in group.Rows)
                {
                    bool show = filter.Length == 0
                        || row.Label.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0;
                    row.Container.Visibility = show
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed;
                    if (show) visible++;
                }
                group.Header.Visibility = visible > 0
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            }

            UpdateParamCount();
        }

        private void UpdateParamCount()
        {
            int visible = _rows.Count(r => r.Container.Visibility == System.Windows.Visibility.Visible);
            ParamCountText.Text = visible == _rows.Count
                ? _rows.Count + "개"
                : visible + " / " + _rows.Count + "개";
        }

        // ===================== 확인/취소 =====================

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            string newName = (NameBox.Text ?? "").Trim();
            if (newName.Length == 0)
            {
                MessageBox.Show("이름은 비워 둘 수 없습니다.", "특성", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!ApplyChangedProperties()) return;

            NewName = newName;
            DialogResult = true;
            Close();
        }

        // 실제로 값이 바뀐 것만 모아 트랜잭션 하나로 쓴다. 바뀐 게 하나도 없으면 트랜잭션 자체를 열지
        // 않는다 - 빈 트랜잭션이라도 커밋하면 되돌리기 목록에 아무 일도 안 한 항목이 쌓이고,
        // 워크쉐어링 모델에서는 불필요한 요소 체크아웃까지 일어난다.
        private bool ApplyChangedProperties()
        {
            List<ParamRow> changed = _rows.Where(HasChanged).ToList();
            bool materialClassChanged = MaterialPanel.Visibility == System.Windows.Visibility.Visible
                && (MaterialClassBox.Text ?? "") != _originalMaterialClass;
            if (changed.Count == 0 && !materialClassChanged) return true;

            var failed = new List<string>();
            TransactionStatus status;

            // NamerCommand의 이름 변경 트랜잭션과 마찬가지로 커스텀 IFailuresPreprocessor를 붙이지
            // 않는다 - 붙이는 것만으로 커밋이 조용히 롤백된 라이브 버그 이력이 있다
            // (docs/namer/CLAUDE.md 참고). Revit 기본 처리에 맡긴다.
            using (Transaction tx = new Transaction(_doc, "NAMER 특성 편집"))
            {
                tx.Start();

                foreach (ParamRow row in changed)
                {
                    try
                    {
                        if (!ApplyRow(row)) failed.Add(row.Label + ": 값을 인식하지 못했습니다");
                    }
                    catch (System.Exception ex)
                    {
                        // 하나가 거부해도 예외를 흘려보내면 트랜잭션 전체가 취소돼 "아무것도 안 바뀐다"가
                        // 된다 - 모아 두었다가 마지막에 한 번에 알린다(룸 경계 ON/OFF와 같은 방침).
                        failed.Add(row.Label + ": " + ex.Message);
                    }
                }

                if (materialClassChanged && _element is Material material)
                {
                    try { material.MaterialClass = MaterialClassBox.Text ?? ""; }
                    catch (System.Exception ex) { failed.Add("재료 클래스: " + ex.Message); }
                }

                // Commit()은 실패해도 예외 없이 RolledBack을 돌려주고 변경을 전부 되돌린다 - 반환값을
                // 안 보면 "성공했다"고 잘못 알리게 된다(NamerCommand의 같은 주석 참고).
                status = tx.Commit();
            }

            if (status != TransactionStatus.Committed)
            {
                MessageBox.Show("특성이 모델에 반영되지 않았습니다 (트랜잭션 롤백: " + status + ").",
                    "특성", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (failed.Count > 0)
            {
                string detail = string.Join("\n", failed.Take(20));
                if (failed.Count > 20) detail += "\n... 외 " + (failed.Count - 20) + "개";
                MessageBox.Show(failed.Count + "개 특성을 바꾸지 못했습니다:\n" + detail,
                    "특성", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return true;
        }

        private static bool HasChanged(ParamRow row)
        {
            if (row.Check != null) return (row.Check.IsChecked == true) != row.OriginalChecked;
            if (row.Box != null) return (row.Box.Text ?? "") != row.OriginalText;
            return false;
        }

        private static bool ApplyRow(ParamRow row)
        {
            if (row.Check != null)
                return row.Param.Set(row.Check.IsChecked == true ? 1 : 0);

            if (row.Box == null) return true;
            string text = row.Box.Text ?? "";

            switch (row.Param.StorageType)
            {
                case StorageType.String:
                    return row.Param.Set(text);
                case StorageType.Double:
                    // 단위가 붙은 표시 문자열을 그대로 해석시킨다(EditableText 주석 참고).
                    return row.Param.SetValueString(text);
                case StorageType.Integer:
                    // 열거형 정수 파라미터는 라벨 문자열로, 평범한 정수는 숫자로 들어오므로 둘 다 받는다.
                    if (row.Param.SetValueString(text)) return true;
                    return int.TryParse(text, out int number) && row.Param.Set(number);
                default:
                    return false;
            }
        }
    }
}
