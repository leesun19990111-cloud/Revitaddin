using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using WpfPath = System.Windows.Shapes.Path;
using WpfPoint = System.Windows.Point;

namespace WallSplitter
{
    public partial class PatternStudioWindow : Window
    {
        private static readonly SolidColorBrush[] GridBrushes =
        {
            MakeBrush("#5980A6"), MakeBrush("#A6595D"), MakeBrush("#3D8F5C"),
            MakeBrush("#A67B3D"), MakeBrush("#795A9D"), MakeBrush("#3B8E9A"),
        };

        private readonly List<PatternDefinition> _sources;
        private readonly HashSet<string> _existingPatternNames;
        private PatternDefinition? _source;
        private PatternDefinition? _edited;
        private PatternTransformSettings _settings = new PatternTransformSettings();
        private bool _suppressUi;
        private int _selectedGridIndex = -1;
        private string _newPatternName = "";

        internal PatternStudioSaveRequest? SaveRequest { get; private set; }

        internal PatternStudioWindow(IEnumerable<PatternDefinition> sources, IEnumerable<PatternDefinition>? existingPatterns = null)
        {
            InitializeComponent();
            _sources = sources.Select(source => source.Clone()).OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            _existingPatternNames = new HashSet<string>(
                _sources.Concat(existingPatterns ?? Enumerable.Empty<PatternDefinition>())
                    .Where(source => source.SourceElementId != null)
                    .Select(source => PatternNameKey(source.Target, source.Name)),
                StringComparer.OrdinalIgnoreCase);

            SourceComboBox.ItemsSource = _sources;
            if (_sources.Count > 0) SourceComboBox.SelectedIndex = 0;
            else LoadEmptyState();
        }

        private PatternGridEditState? SelectedEdit =>
            _selectedGridIndex >= 0 && _selectedGridIndex < _settings.GridEdits.Count
                ? _settings.GridEdits[_selectedGridIndex]
                : null;

        private void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressUi || SourceComboBox.SelectedItem is not PatternDefinition source) return;
            LoadSource(source);
        }

        private void LoadSource(PatternDefinition source)
        {
            _source = source.Clone();
            _settings = new PatternTransformSettings
            {
                GridEdits = _source.Grids.Select((grid, index) => new PatternGridEditState { Index = index }).ToList(),
            };
            _selectedGridIndex = _settings.GridEdits.Count > 0 ? 0 : -1;
            _newPatternName = FindUniqueName(_source.Target, _source.Name + "_편집");

            _suppressUi = true;
            try
            {
                GridList.ItemsSource = _settings.GridEdits;
                GridList.SelectedIndex = _selectedGridIndex;
                PatternTypeText.Text = _source.Target == Autodesk.Revit.DB.FillPatternTarget.Model ? "모델 패턴" : "제도 패턴";
                SourceUnitText.Text = string.IsNullOrWhiteSpace(_source.SourceUnitLabel) ? "Revit 내부 단위" : _source.SourceUnitLabel;
                ReferenceAxisText.Text = _source.Grids.Count == 0
                    ? "기준축 없음"
                    : $"폭·높이 기준축: 선군 1 ({_source.Grids[0].AngleDegrees:0.##}°)";
                OverwriteCheckBox.IsChecked = false;
                OverwriteCheckBox.IsEnabled = _source.SourceElementId != null;
                SaveNameBox.IsEnabled = true;
                SaveNameBox.Text = _newPatternName;
                SetOverallControls();
                SetSelectedGridControls();
            }
            finally
            {
                _suppressUi = false;
            }
            UpdatePreview("새 패턴으로 저장합니다. 원본은 바뀌지 않습니다.");
        }

        private void LoadEmptyState()
        {
            _source = null;
            _edited = null;
            _settings = new PatternTransformSettings();
            GridList.ItemsSource = null;
            PatternTypeText.Text = "패턴 없음";
            SourceUnitText.Text = "PAT 불러오기 가능";
            ReferenceAxisText.Text = "";
            SaveNameBox.Text = "";
            SaveNameBox.IsEnabled = false;
            OverwriteCheckBox.IsChecked = false;
            OverwriteCheckBox.IsEnabled = false;
            SaveToRevitButton.IsEnabled = false;
            SetGridControlsEnabled(false);
            PreviewCanvas.Children.Clear();
            EmptyPreviewText.Visibility = Visibility.Visible;
            SetStatus("Revit 문서에 편집할 패턴이 없으면 PAT 파일을 먼저 불러오세요.", false);
        }

        private void GridList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressUi) return;
            _selectedGridIndex = GridList.SelectedIndex;
            SetSelectedGridControls();
            RenderPreview();
        }

        private void SetOverallControls()
        {
            SetBoxAndSlider(OverallAngleBox, OverallAngleSlider, _settings.RotationDegrees);
            SetBoxAndSlider(UniformScaleBox, UniformScaleSlider, _settings.UniformScalePercent);
            SetBoxAndSlider(WidthBox, WidthSlider, _settings.WidthPercent);
            SetBoxAndSlider(HeightBox, HeightSlider, _settings.HeightPercent);
        }

        private void SetSelectedGridControls()
        {
            PatternGridEditState? edit = SelectedEdit;
            bool enabled = edit != null;
            SetGridControlsEnabled(enabled);
            SelectedGridText.Text = edit?.DisplayName ?? "선군을 선택하세요.";
            if (edit == null) return;

            _suppressUi = true;
            try
            {
                SetBoxAndSlider(GroupAngleBox, GroupAngleSlider, edit.AngleDeltaDegrees);
                SetBoxAndSlider(GroupSizeBox, GroupSizeSlider, edit.SizePercent);
                SetBoxAndSlider(GroupSpacingBox, GroupSpacingSlider, edit.SpacingPercent);
            }
            finally
            {
                _suppressUi = false;
            }
        }

        private void SetGridControlsEnabled(bool enabled)
        {
            GroupAngleBox.IsEnabled = enabled;
            GroupAngleSlider.IsEnabled = enabled;
            GroupSizeBox.IsEnabled = enabled;
            GroupSizeSlider.IsEnabled = enabled;
            GroupSpacingBox.IsEnabled = enabled;
            GroupSpacingSlider.IsEnabled = enabled;
        }

        private static void SetBoxAndSlider(TextBox box, Slider slider, double value)
        {
            box.Text = FormatNumber(value);
            slider.Value = Clamp(value, slider.Minimum, slider.Maximum);
        }

        private void OverallAngleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            ApplySliderValue("OverallAngle", e.NewValue);
        private void UniformScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            ApplySliderValue("UniformScale", e.NewValue);
        private void WidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            ApplySliderValue("Width", e.NewValue);
        private void HeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            ApplySliderValue("Height", e.NewValue);
        private void GroupAngleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            ApplySliderValue("GroupAngle", e.NewValue);
        private void GroupSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            ApplySliderValue("GroupSize", e.NewValue);
        private void GroupSpacingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            ApplySliderValue("GroupSpacing", e.NewValue);

        private void ApplySliderValue(string tag, double value)
        {
            if (_suppressUi || _source == null) return;
            _suppressUi = true;
            try
            {
                switch (tag)
                {
                    case "OverallAngle": _settings.RotationDegrees = value; OverallAngleBox.Text = FormatNumber(value); break;
                    case "UniformScale": _settings.UniformScalePercent = value; UniformScaleBox.Text = FormatNumber(value); break;
                    case "Width":
                        _settings.WidthPercent = value;
                        WidthBox.Text = FormatNumber(value);
                        if (LockAxesCheckBox.IsChecked == true)
                        {
                            _settings.HeightPercent = value;
                            HeightBox.Text = FormatNumber(value);
                            HeightSlider.Value = Clamp(value, HeightSlider.Minimum, HeightSlider.Maximum);
                        }
                        break;
                    case "Height":
                        _settings.HeightPercent = value;
                        HeightBox.Text = FormatNumber(value);
                        if (LockAxesCheckBox.IsChecked == true)
                        {
                            _settings.WidthPercent = value;
                            WidthBox.Text = FormatNumber(value);
                            WidthSlider.Value = Clamp(value, WidthSlider.Minimum, WidthSlider.Maximum);
                        }
                        break;
                    case "GroupAngle" when SelectedEdit != null:
                        SelectedEdit.AngleDeltaDegrees = value; GroupAngleBox.Text = FormatNumber(value); break;
                    case "GroupSize" when SelectedEdit != null:
                        SelectedEdit.SizePercent = value; GroupSizeBox.Text = FormatNumber(value); break;
                    case "GroupSpacing" when SelectedEdit != null:
                        SelectedEdit.SpacingPercent = value; GroupSpacingBox.Text = FormatNumber(value); break;
                }
            }
            finally
            {
                _suppressUi = false;
            }
            UpdateAfterEdit();
        }

        private void NumericBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not TextBox box) return;
            CommitNumericBox(box, true);
            Keyboard.ClearFocus();
            e.Handled = true;
        }

        private void NumericBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox box) CommitNumericBox(box, false);
        }

        private bool CommitNumericBox(TextBox box, bool showMessage)
        {
            if (_suppressUi || _source == null) return true;
            if (!TryParseNumber(box.Text, out double value))
            {
                RestoreNumericBox(box);
                SetStatus("숫자 형식을 확인해 주세요.", true);
                if (showMessage) MessageBox.Show(this, "숫자 형식을 확인해 주세요.", "패턴 스튜디오", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            string tag = box.Tag as string ?? "";
            if (tag != "OverallAngle" && tag != "GroupAngle" && value <= 0.0)
            {
                RestoreNumericBox(box);
                SetStatus("크기와 간격 값은 0보다 커야 합니다.", true);
                if (showMessage) MessageBox.Show(this, "크기와 간격 값은 0보다 커야 합니다.", "패턴 스튜디오", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            _suppressUi = true;
            try
            {
                switch (tag)
                {
                    case "OverallAngle": _settings.RotationDegrees = value; OverallAngleSlider.Value = Clamp(value, OverallAngleSlider.Minimum, OverallAngleSlider.Maximum); break;
                    case "UniformScale": _settings.UniformScalePercent = value; UniformScaleSlider.Value = Clamp(value, UniformScaleSlider.Minimum, UniformScaleSlider.Maximum); break;
                    case "Width":
                        _settings.WidthPercent = value;
                        WidthSlider.Value = Clamp(value, WidthSlider.Minimum, WidthSlider.Maximum);
                        if (LockAxesCheckBox.IsChecked == true)
                        {
                            _settings.HeightPercent = value;
                            HeightBox.Text = FormatNumber(value);
                            HeightSlider.Value = Clamp(value, HeightSlider.Minimum, HeightSlider.Maximum);
                        }
                        break;
                    case "Height":
                        _settings.HeightPercent = value;
                        HeightSlider.Value = Clamp(value, HeightSlider.Minimum, HeightSlider.Maximum);
                        if (LockAxesCheckBox.IsChecked == true)
                        {
                            _settings.WidthPercent = value;
                            WidthBox.Text = FormatNumber(value);
                            WidthSlider.Value = Clamp(value, WidthSlider.Minimum, WidthSlider.Maximum);
                        }
                        break;
                    case "GroupAngle" when SelectedEdit != null: SelectedEdit.AngleDeltaDegrees = value; GroupAngleSlider.Value = Clamp(value, GroupAngleSlider.Minimum, GroupAngleSlider.Maximum); break;
                    case "GroupSize" when SelectedEdit != null: SelectedEdit.SizePercent = value; GroupSizeSlider.Value = Clamp(value, GroupSizeSlider.Minimum, GroupSizeSlider.Maximum); break;
                    case "GroupSpacing" when SelectedEdit != null: SelectedEdit.SpacingPercent = value; GroupSpacingSlider.Value = Clamp(value, GroupSpacingSlider.Minimum, GroupSpacingSlider.Maximum); break;
                }
                box.Text = FormatNumber(value);
            }
            finally
            {
                _suppressUi = false;
            }
            UpdateAfterEdit();
            return true;
        }

        private void RestoreNumericBox(TextBox box)
        {
            string tag = box.Tag as string ?? "";
            double value = tag switch
            {
                "OverallAngle" => _settings.RotationDegrees,
                "UniformScale" => _settings.UniformScalePercent,
                "Width" => _settings.WidthPercent,
                "Height" => _settings.HeightPercent,
                "GroupAngle" => SelectedEdit?.AngleDeltaDegrees ?? 0.0,
                "GroupSize" => SelectedEdit?.SizePercent ?? 100.0,
                "GroupSpacing" => SelectedEdit?.SpacingPercent ?? 100.0,
                _ => 0.0,
            };
            box.Text = FormatNumber(value);
        }

        private void UpdateAfterEdit()
        {
            GridList.Items.Refresh();
            UpdatePreview("편집값이 미리보기에 적용되었습니다.");
        }

        private void UpdatePreview(string successStatus)
        {
            if (_source == null) return;
            try
            {
                _edited = PatternTransformService.Transform(_source, _settings);
                List<string> errors = PatternTransformService.Validate(_edited);
                if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
                SaveToRevitButton.IsEnabled = true;
                SetStatus(successStatus, false);
            }
            catch (Exception ex)
            {
                _edited = null;
                SaveToRevitButton.IsEnabled = false;
                SetStatus(ex.Message, true);
            }
            RenderPreview();
        }

        private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderPreview();
        private void PreviewOption_Changed(object sender, RoutedEventArgs e) => RenderPreview();

        private void RenderPreview()
        {
            if (!IsInitialized || PreviewCanvas == null) return;
            PreviewCanvas.Children.Clear();
            if (_source == null || _edited == null || PreviewCanvas.ActualWidth < 20 || PreviewCanvas.ActualHeight < 20)
            {
                EmptyPreviewText.Visibility = Visibility.Visible;
                return;
            }
            EmptyPreviewText.Visibility = Visibility.Collapsed;

            double spacing = Median(_edited.Grids.Select(grid => Math.Abs(grid.Offset)).Where(value => value > 1e-9).ToList());
            if (spacing <= 1e-9) spacing = 1.0;
            double worldHeight = spacing * 12.0;
            double pixelsPerFoot = PreviewCanvas.ActualHeight / worldHeight;
            double worldWidth = PreviewCanvas.ActualWidth / pixelsPerFoot;
            double minX = -worldWidth / 2.0;
            double maxX = worldWidth / 2.0;
            double minY = -worldHeight / 2.0;
            double maxY = worldHeight / 2.0;

            DrawPreviewAxes();
            if (ShowOriginalCheckBox.IsChecked == true)
                DrawDefinition(_source, minX, maxX, minY, maxY, pixelsPerFoot, true);
            DrawDefinition(_edited, minX, maxX, minY, maxY, pixelsPerFoot, false);
        }

        private void DrawPreviewAxes()
        {
            double width = PreviewCanvas.ActualWidth;
            double height = PreviewCanvas.ActualHeight;
            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(new WpfPoint(width / 2.0, 0), false, false);
                context.LineTo(new WpfPoint(width / 2.0, height), true, false);
                context.BeginFigure(new WpfPoint(0, height / 2.0), false, false);
                context.LineTo(new WpfPoint(width, height / 2.0), true, false);
            }
            geometry.Freeze();
            PreviewCanvas.Children.Add(new WpfPath
            {
                Data = geometry,
                Stroke = MakeBrush("#18000000"),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });
        }

        private void DrawDefinition(PatternDefinition definition, double minX, double maxX, double minY, double maxY,
            double pixelsPerFoot, bool original)
        {
            for (int index = 0; index < definition.Grids.Count; index++)
            {
                PatternGridDefinition grid = definition.Grids[index];
                StreamGeometry geometry = BuildGridGeometry(grid, minX, maxX, minY, maxY, pixelsPerFoot);
                bool selected = !original && index == _selectedGridIndex;
                var path = new WpfPath
                {
                    Data = geometry,
                    Stroke = original ? MakeBrush("#6B7178") : GridBrushes[index % GridBrushes.Length],
                    StrokeThickness = original ? 1.0 : selected ? 2.4 : 1.35,
                    Opacity = original ? 0.28 : 0.92,
                    SnapsToDevicePixels = true,
                    Tag = original ? null : index,
                    Cursor = original ? Cursors.Arrow : Cursors.Hand,
                    IsHitTestVisible = !original,
                };
                if (!original) path.MouseLeftButtonDown += PreviewPath_MouseLeftButtonDown;
                PreviewCanvas.Children.Add(path);
            }
        }

        private StreamGeometry BuildGridGeometry(PatternGridDefinition grid, double minX, double maxX, double minY, double maxY,
            double pixelsPerFoot)
        {
            double radians = grid.AngleDegrees * Math.PI / 180.0;
            Vector direction = new Vector(Math.Cos(radians), Math.Sin(radians));
            Vector normal = new Vector(-direction.Y, direction.X);
            Vector origin = new Vector(grid.OriginX, grid.OriginY);
            double originProjection = Dot(origin, normal);

            var corners = new[]
            {
                new Vector(minX, minY), new Vector(minX, maxY),
                new Vector(maxX, minY), new Vector(maxX, maxY),
            };
            double minProjection = corners.Min(corner => Dot(corner, normal));
            double maxProjection = corners.Max(corner => Dot(corner, normal));
            double first = (minProjection - originProjection) / grid.Offset;
            double last = (maxProjection - originProjection) / grid.Offset;
            int start = SafeFloor(Math.Min(first, last)) - 2;
            int end = SafeCeiling(Math.Max(first, last)) + 2;
            const int maximumLines = 1200;
            if (end - start > maximumLines)
            {
                int center = SafeFloor((first + last) / 2.0);
                start = center - maximumLines / 2;
                end = center + maximumLines / 2;
            }

            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                for (int k = start; k <= end; k++)
                {
                    Vector anchor = origin + k * (direction * grid.Shift + normal * grid.Offset);
                    if (!TryClipLine(anchor, direction, minX, maxX, minY, maxY, out double t0, out double t1)) continue;
                    if (grid.Segments.Count == 0)
                        AddWorldSegment(context, anchor + direction * t0, anchor + direction * t1, pixelsPerFoot);
                    else
                        AddDashedLine(context, anchor, direction, t0, t1, grid.Segments, pixelsPerFoot);
                }
            }
            geometry.Freeze();
            return geometry;
        }

        private void AddDashedLine(StreamGeometryContext context, Vector anchor, Vector direction, double t0, double t1,
            IReadOnlyList<double> segments, double pixelsPerFoot)
        {
            double cycle = segments.Sum(segment => Math.Abs(segment));
            if (cycle < 1e-9)
            {
                // 0 세그먼트는 점이다. 반복 길이가 없는 점 전용 선군을 실선으로
                // 연장하지 않고, 이 선군의 기준점에 화면 크기의 짧은 점만 그린다.
                double singleDotLength = 1.7 / pixelsPerFoot;
                if (t0 <= 0.0 && t1 >= 0.0)
                    AddWorldSegment(context, anchor, anchor + direction * singleDotLength, pixelsPerFoot);
                return;
            }

            double dotLength = 1.7 / pixelsPerFoot;
            long firstCycle = SafeCycleIndex(Math.Floor(t0 / cycle));
            long lastCycle = SafeCycleIndex(Math.Ceiling(t1 / cycle));
            const long maximumCycles = 20000;
            if (lastCycle - firstCycle > maximumCycles)
                lastCycle = firstCycle + maximumCycles;

            int safety = 0;
            for (long cycleIndex = firstCycle; cycleIndex <= lastCycle && safety++ <= maximumCycles; cycleIndex++)
            {
                double cursor = cycleIndex * cycle;
                foreach (double segment in segments)
                {
                    double length = Math.Abs(segment);
                    if (Math.Abs(segment) < 1e-9)
                    {
                        if (cursor >= t0 && cursor <= t1)
                            AddWorldSegment(context, anchor + direction * cursor, anchor + direction * Math.Min(cursor + dotLength, t1), pixelsPerFoot);
                        continue;
                    }
                    double segmentEnd = cursor + length;
                    if (segment > 0.0 && segmentEnd >= t0 && cursor <= t1)
                    {
                        double visibleStart = Math.Max(cursor, t0);
                        double visibleEnd = Math.Min(segmentEnd, t1);
                        if (visibleEnd > visibleStart)
                            AddWorldSegment(context, anchor + direction * visibleStart, anchor + direction * visibleEnd, pixelsPerFoot);
                    }
                    cursor = segmentEnd;
                }
            }
        }

        private void AddWorldSegment(StreamGeometryContext context, Vector start, Vector end, double pixelsPerFoot)
        {
            WpfPoint a = WorldToCanvas(start, pixelsPerFoot);
            WpfPoint b = WorldToCanvas(end, pixelsPerFoot);
            context.BeginFigure(a, false, false);
            context.LineTo(b, true, false);
        }

        private WpfPoint WorldToCanvas(Vector point, double pixelsPerFoot)
        {
            return new WpfPoint(
                PreviewCanvas.ActualWidth / 2.0 + point.X * pixelsPerFoot,
                PreviewCanvas.ActualHeight / 2.0 - point.Y * pixelsPerFoot);
        }

        private static bool TryClipLine(Vector anchor, Vector direction, double minX, double maxX, double minY, double maxY,
            out double t0, out double t1)
        {
            t0 = double.NegativeInfinity;
            t1 = double.PositiveInfinity;
            if (!ClipAxis(anchor.X, direction.X, minX, maxX, ref t0, ref t1) ||
                !ClipAxis(anchor.Y, direction.Y, minY, maxY, ref t0, ref t1)) return false;
            return t1 >= t0 && !double.IsInfinity(t0) && !double.IsInfinity(t1);
        }

        private static bool ClipAxis(double origin, double direction, double minimum, double maximum, ref double t0, ref double t1)
        {
            if (Math.Abs(direction) < 1e-12) return origin >= minimum && origin <= maximum;
            double a = (minimum - origin) / direction;
            double b = (maximum - origin) / direction;
            if (a > b) (a, b) = (b, a);
            t0 = Math.Max(t0, a);
            t1 = Math.Min(t1, b);
            return t1 >= t0;
        }

        private void PreviewPath_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not WpfPath path || path.Tag is not int index) return;
            GridList.SelectedIndex = index;
            GridList.ScrollIntoView(GridList.SelectedItem);
            e.Handled = true;
        }

        private void ResetSelectedGridButton_Click(object sender, RoutedEventArgs e)
        {
            PatternGridEditState? edit = SelectedEdit;
            if (edit == null) return;
            edit.AngleDeltaDegrees = 0.0;
            edit.SizePercent = 100.0;
            edit.SpacingPercent = 100.0;
            SetSelectedGridControls();
            UpdateAfterEdit();
        }

        private void ResetAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_source == null) return;
            _settings = new PatternTransformSettings
            {
                GridEdits = _source.Grids.Select((grid, index) => new PatternGridEditState { Index = index }).ToList(),
            };
            _selectedGridIndex = _settings.GridEdits.Count > 0 ? Math.Max(0, Math.Min(_selectedGridIndex, _settings.GridEdits.Count - 1)) : -1;
            _suppressUi = true;
            try
            {
                GridList.ItemsSource = _settings.GridEdits;
                GridList.SelectedIndex = _selectedGridIndex;
                SetOverallControls();
                SetSelectedGridControls();
            }
            finally
            {
                _suppressUi = false;
            }
            UpdatePreview("모든 편집값을 초기화했습니다.");
        }

        private void OpenPatButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "PAT 패턴 파일 불러오기",
                Filter = "Revit 패턴 파일 (*.pat)|*.pat|모든 파일 (*.*)|*.*",
                Multiselect = false,
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                PatImportResult imported = PatFileService.Import(dialog.FileName);
                if (imported.Patterns.Count == 0)
                {
                    string detail = imported.Warnings.Count > 0 ? "\n\n" + string.Join("\n", imported.Warnings.Take(12)) : "";
                    MessageBox.Show(this, "불러올 수 있는 패턴이 없습니다." + detail, "패턴 스튜디오", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int previousCount = _sources.Count;
                _sources.AddRange(imported.Patterns);
                SourceComboBox.Items.Refresh();
                SourceComboBox.SelectedItem = _sources[previousCount];

                if (imported.Warnings.Count > 0)
                {
                    string detail = string.Join("\n", imported.Warnings.Take(12));
                    if (imported.Warnings.Count > 12) detail += $"\n... 외 {imported.Warnings.Count - 12}개";
                    MessageBox.Show(this, $"{imported.Patterns.Count}개 패턴을 불러왔습니다.\n\n확인할 내용:\n{detail}", "패턴 스튜디오", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    SetStatus($"PAT에서 {imported.Patterns.Count}개 패턴을 불러왔습니다.", false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "PAT 파일을 읽지 못했습니다.\n\n" + ex.Message, "패턴 스튜디오", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportPatButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetNamedEditedPattern(out PatternDefinition pattern)) return;
            var dialog = new SaveFileDialog
            {
                Title = "편집한 패턴을 PAT로 내보내기",
                Filter = "Revit 패턴 파일 (*.pat)|*.pat",
                DefaultExt = ".pat",
                AddExtension = true,
                FileName = MakeSafeFileName(pattern.Name) + ".pat",
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                PatFileService.Export(dialog.FileName, pattern);
                SetStatus("PAT 파일을 저장했습니다: " + System.IO.Path.GetFileName(dialog.FileName), false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "PAT 파일을 저장하지 못했습니다.\n\n" + ex.Message, "패턴 스튜디오", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OverwriteCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressUi || _source == null) return;
            bool overwrite = OverwriteCheckBox.IsChecked == true;
            if (overwrite)
            {
                _newPatternName = SaveNameBox.Text.Trim();
                SaveNameBox.Text = _source.Name;
                SaveNameBox.IsEnabled = false;
                SetStatus("주의: 저장하면 이 패턴의 모든 사용처에 변경이 반영됩니다.", true);
            }
            else
            {
                SaveNameBox.IsEnabled = true;
                SaveNameBox.Text = string.IsNullOrWhiteSpace(_newPatternName) ? FindUniqueName(_source.Target, _source.Name + "_편집") : _newPatternName;
                SetStatus("새 패턴으로 저장합니다. 원본은 바뀌지 않습니다.", false);
            }
        }

        private void SaveToRevitButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetNamedEditedPattern(out PatternDefinition pattern) || _source == null) return;
            bool overwrite = OverwriteCheckBox.IsChecked == true;
            if (overwrite)
            {
                MessageBoxResult answer = MessageBox.Show(this,
                    $"'{_source.Name}' 패턴을 덮어쓰면 이 패턴을 사용하는 모든 재료·영역의 표시가 함께 바뀝니다.\n\n계속할까요?",
                    "원본 패턴 덮어쓰기", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
                pattern.Name = _source.Name;
            }
            else if (_existingPatternNames.Contains(PatternNameKey(pattern.Target, pattern.Name)))
            {
                MessageBox.Show(this, "같은 유형에 이미 같은 이름의 패턴이 있습니다. 다른 저장 이름을 입력해 주세요.", "패턴 스튜디오", MessageBoxButton.OK, MessageBoxImage.Warning);
                SaveNameBox.Focus();
                SaveNameBox.SelectAll();
                return;
            }

            SaveRequest = new PatternStudioSaveRequest
            {
                Pattern = pattern,
                Name = pattern.Name,
                OverwriteSource = overwrite,
                SourceElementId = overwrite ? _source.SourceElementId : null,
            };
            DialogResult = true;
        }

        private bool TryGetNamedEditedPattern(out PatternDefinition pattern)
        {
            pattern = null!;
            if (_source == null) return false;

            foreach (TextBox box in AllNumericBoxes())
            {
                if (!CommitNumericBox(box, false)) return false;
            }

            string name = SaveNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(this, "저장할 패턴 이름을 입력해 주세요.", "패턴 스튜디오", MessageBoxButton.OK, MessageBoxImage.Warning);
                SaveNameBox.Focus();
                return false;
            }

            try
            {
                pattern = PatternTransformService.Transform(_source, _settings);
                pattern.Name = name;
                List<string> errors = PatternTransformService.Validate(pattern);
                if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "현재 편집값으로 패턴을 만들 수 없습니다.\n\n" + ex.Message, "패턴 스튜디오", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private IEnumerable<TextBox> AllNumericBoxes()
        {
            yield return OverallAngleBox;
            yield return UniformScaleBox;
            yield return WidthBox;
            yield return HeightBox;
            if (SelectedEdit != null)
            {
                yield return GroupAngleBox;
                yield return GroupSizeBox;
                yield return GroupSpacingBox;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private string FindUniqueName(Autodesk.Revit.DB.FillPatternTarget target, string desired)
        {
            if (!_existingPatternNames.Contains(PatternNameKey(target, desired))) return desired;
            int suffix = 2;
            while (_existingPatternNames.Contains(PatternNameKey(target, desired + " " + suffix))) suffix++;
            return desired + " " + suffix;
        }

        private static string PatternNameKey(Autodesk.Revit.DB.FillPatternTarget target, string name) => ((int)target).ToString(CultureInfo.InvariantCulture) + "|" + name.Trim();
        private static string MakeSafeFileName(string name) => string.Join("_", name.Split(System.IO.Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

        private void SetStatus(string text, bool error)
        {
            StatusText.Text = text;
            StatusText.Foreground = error ? Theme.DangerText : Theme.TextSecondary;
        }

        private static bool TryParseNumber(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string FormatNumber(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
        private static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(maximum, value));
        private static double Dot(Vector a, Vector b) => a.X * b.X + a.Y * b.Y;
        private static int SafeFloor(double value) => (int)Math.Max(int.MinValue + 10.0, Math.Min(int.MaxValue - 10.0, Math.Floor(value)));
        private static int SafeCeiling(double value) => (int)Math.Max(int.MinValue + 10.0, Math.Min(int.MaxValue - 10.0, Math.Ceiling(value)));
        private static long SafeCycleIndex(double value) => (long)Math.Max(long.MinValue / 2.0, Math.Min(long.MaxValue / 2.0, value));

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0.0;
            values.Sort();
            int middle = values.Count / 2;
            return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2.0;
        }

        private static SolidColorBrush MakeBrush(string value)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            return brush;
        }
    }
}
