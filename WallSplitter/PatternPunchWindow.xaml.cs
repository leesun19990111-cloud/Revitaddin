using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clipper2Lib;
using WpfPath = System.Windows.Shapes.Path;
using WpfPoint = System.Windows.Point;

namespace WallSplitter
{
    internal partial class PatternPunchWindow : Window
    {
        private readonly PatternPunchPlan _plan;
        private readonly Dictionary<PatternPunchTarget, HashSet<PatternRegion>> _selectedByTarget =
            new Dictionary<PatternPunchTarget, HashSet<PatternRegion>>();
        private bool _validated;
        private bool _isInitialized;

        public PatternPunchWindow(PatternPunchPlan plan)
        {
            _plan = plan ?? throw new ArgumentNullException(nameof(plan));
            InitializeComponent();
            PatternNameText.Text = plan.PatternName;
            PatternInfoText.Text = plan.Pattern.Target == Autodesk.Revit.DB.FillPatternTarget.Drafting
                ? $"제도 패턴 · {plan.PatternLayerLabel} · 현재 뷰 1:{plan.ViewScale} 기준"
                : $"모델 패턴 · {plan.PatternLayerLabel} · 실제 크기 1:1";
            TargetList.ItemsSource = plan.Targets;
            TargetList.SelectedIndex = 0;
            StatusText.Text = plan.Warnings.Count == 0
                ? "현재 면에서 실제로 뚫을 폐영역을 하나씩 직접 클릭하세요."
                : "타공 영역을 직접 선택하세요. · " + string.Join(" · ", plan.Warnings.Take(3));
            _isInitialized = true;
            Loaded += (_, _) => RenderAll();
        }

        public PatternPunchRequest? Request { get; private set; }

        private void RenderAll()
        {
            if (!_isInitialized) return;
            RenderSelectionPreview();
            RenderFullPreview();
            UpdateCounts();
        }

        private void RenderSelectionPreview()
        {
            if (SelectionCanvas == null || SelectionCanvas.ActualWidth < 10 || SelectionCanvas.ActualHeight < 10) return;
            SelectionCanvas.Children.Clear();
            if (TargetList.SelectedItem is not PatternPunchTarget target) return;
            CanvasTransform transform = CanvasTransform.Fit(target.Bounds, SelectionCanvas.ActualWidth, SelectionCanvas.ActualHeight, 22);

            foreach (Path64 boundary in target.FacePaths)
            {
                SelectionCanvas.Children.Add(new WpfPath
                {
                    Data = MakeGeometry(PatternClipper.FromPath(boundary), transform),
                    Fill = MakeBrush("#0C5980A6"),
                    Stroke = MakeBrush("#1D1F20"),
                    StrokeThickness = 1.6,
                    IsHitTestVisible = false,
                });
            }
            DrawSegments(SelectionCanvas, target.PatternSegments, transform, "#778087", 1.0, 0.72);

            HashSet<PatternRegion> selectedRegions = GetSelectedSet(target);
            foreach (PatternRegion region in target.Regions)
            {
                bool selected = selectedRegions.Contains(region);
                Paths64 visiblePaths = PatternClipper.Intersect(
                    new Paths64 { PatternClipper.ToPath(region.Points) }, target.FacePaths);
                foreach (Path64 visiblePath in visiblePaths)
                {
                    var path = new WpfPath
                    {
                        Data = MakeGeometry(PatternClipper.FromPath(visiblePath), transform),
                        Fill = MakeBrush(selected ? "#665980A6" : "#08000000"),
                        Stroke = MakeBrush(selected ? "#5980A6" : "#334C5358"),
                        StrokeThickness = selected ? 2.0 : 0.8,
                        Cursor = Cursors.Hand,
                        Tag = new RegionSelectionTag(target, region),
                        ToolTip = $"면적 {region.Area * 92903.04:0.##} mm² · 이 영역만 선택/해제",
                    };
                    path.MouseLeftButtonDown += RegionPath_MouseLeftButtonDown;
                    SelectionCanvas.Children.Add(path);
                }
            }
        }

        private void RenderFullPreview()
        {
            if (FullCanvas == null || FullCanvas.ActualWidth < 10 || FullCanvas.ActualHeight < 10) return;
            FullCanvas.Children.Clear();
            if (TargetList.SelectedItem is not PatternPunchTarget target) return;
            CanvasTransform transform = CanvasTransform.Fit(target.Bounds, FullCanvas.ActualWidth, FullCanvas.ActualHeight, 24);

            foreach (Path64 boundary in target.FacePaths)
            {
                FullCanvas.Children.Add(new WpfPath
                {
                    Data = MakeGeometry(PatternClipper.FromPath(boundary), transform),
                    Fill = MakeBrush("#0C5980A6"),
                    Stroke = MakeBrush("#1D1F20"),
                    StrokeThickness = 1.6,
                    IsHitTestVisible = false,
                });
            }
            DrawSegments(FullCanvas, target.PatternSegments, transform, "#7D858B", 0.8, 0.42);

            List<PatternRegion> selectedRegions = OrderedSelection(target);
            if (selectedRegions.Count == 0) return;
            if (!TryReadMinimum(out double minWidth, out double minHeight)) return;
            Paths64 punchPaths = target.BuildPunchPaths(selectedRegions, minWidth, minHeight);
            foreach (Path64 punchPath in punchPaths)
            {
                bool boundary = TouchesFaceBoundary(punchPath, target.FacePaths);
                FullCanvas.Children.Add(new WpfPath
                {
                    Data = MakeGeometry(PatternClipper.FromPath(punchPath), transform),
                    Fill = MakeBrush(boundary ? "#66C27A3A" : "#665980A6"),
                    Stroke = MakeBrush(boundary ? "#A66F2E" : "#3E668E"),
                    StrokeThickness = 1.2,
                    IsHitTestVisible = false,
                });
            }
        }

        private void RegionPath_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not WpfPath path || path.Tag is not RegionSelectionTag tag) return;
            HashSet<PatternRegion> selectedRegions = GetSelectedSet(tag.Target);
            if (!selectedRegions.Add(tag.Region)) selectedRegions.Remove(tag.Region);
            _validated = false;
            RunButton.IsEnabled = false;
            ValidationText.Text = "형상 사전 검증이 필요합니다.";
            int selectedCount = _selectedByTarget.Values.Sum(regions => regions.Count);
            int selectedTargetCount = _selectedByTarget.Count(pair => pair.Value.Count > 0);
            SelectionStateText.Text = $"직접 선택 {selectedCount:N0}개 · 면 {selectedTargetCount:N0}개";
            RenderAll();
            e.Handled = true;
        }

        private void ValidateButton_Click(object sender, RoutedEventArgs e)
        {
            List<PatternPunchSelection> selections = BuildSelections();
            if (selections.Count == 0)
            {
                ValidationText.Text = "먼저 타공할 폐영역을 하나 이상 직접 클릭하세요.";
                return;
            }
            if (!TryReadMinimum(out double minWidth, out double minHeight))
            {
                ValidationText.Text = "최소 폭·높이에 올바른 숫자를 입력하세요.";
                return;
            }

            int total = 0;
            var errors = new List<string>();
            foreach (PatternPunchSelection selection in selections)
            {
                PatternPunchTarget target = selection.Target;
                Paths64 punch = target.BuildPunchPaths(selection.Regions, minWidth, minHeight);
                total += punch.Count;
                if (punch.Count == 0)
                {
                    errors.Add($"{target.Label}: 직접 선택한 영역이 최소 크기 조건을 통과하지 못했습니다.");
                    continue;
                }
                Paths64 remaining = PatternClipper.Difference(target.FacePaths, punch);
                if (remaining.Count == 0)
                {
                    errors.Add($"{target.Label}: 선택 결과가 패널 또는 호스트 전체를 제거합니다.");
                    continue;
                }
                if (CountMaterialIslands(remaining) > 1)
                    errors.Add($"{target.Label}: 남는 재료가 여러 조각으로 완전히 분리됩니다.");
            }

            if (errors.Count > 0)
            {
                _validated = false;
                RunButton.IsEnabled = false;
                ValidationText.Text = "검증 실패\n" + string.Join("\n", errors.Take(8));
                return;
            }

            _validated = true;
            RunButton.IsEnabled = true;
            ValidationText.Text = $"형상 사전 검증을 통과했습니다. · 타공 경계 {total:N0}개";
            StatusText.Text = "타공 실행을 누르면 대상별로 실제 Revit 스케치 생성 검사를 한 뒤 적용합니다.";
        }

        private void UpdateCounts()
        {
            List<PatternPunchSelection> selections = BuildSelections();
            if (selections.Count == 0)
            {
                CountText.Text = "타공할 폐영역을 직접 선택하면 계산됩니다.";
                ComplexityText.Text = $"선택 면 {_plan.Targets.Count:N0}개 · 감지된 폐영역 {_plan.Targets.Sum(target => target.Regions.Count):N0}개";
                return;
            }
            if (!TryReadMinimum(out double minWidth, out double minHeight))
            {
                CountText.Text = $"직접 선택 영역 {selections.Sum(selection => selection.Regions.Count):N0}개\n최소 폭·높이 값을 확인하세요.";
                return;
            }

            int clippedCount = 0;
            int eligibleRegionCount = 0;
            foreach (PatternPunchSelection selection in selections)
            {
                clippedCount += selection.Target.BuildPunchPaths(selection.Regions, minWidth, minHeight).Count;
                eligibleRegionCount += selection.Target.CountEligibleRegions(selection.Regions, minWidth, minHeight);
            }
            int selectedRegionCount = selections.Sum(selection => selection.Regions.Count);
            int excludedRegionCount = selectedRegionCount - eligibleRegionCount;
            CountText.Text = $"직접 선택 영역 {selectedRegionCount:N0}개\n최종 타공 경계 {clippedCount:N0}개\n선택된 면 {selections.Count:N0}/{_plan.Targets.Count:N0}개" +
                             (excludedRegionCount > 0 ? $"\n최소 크기로 제외 {excludedRegionCount:N0}개" : "");
            ComplexityText.Text = clippedCount > 2000
                ? "대량 타공입니다. 실행 시간이 길고 Revit 파일 용량이 늘어날 수 있습니다."
                : clippedCount > 500 ? "타공 수가 많아 요소별로 나누어 검증합니다." : "일반적인 형상 복잡도입니다.";
        }

        private bool TryReadMinimum(out double widthFeet, out double heightFeet)
        {
            widthFeet = 0.0;
            heightFeet = 0.0;
            if (MinimumSizeCheckBox?.IsChecked != true) return true;
            if (!TryParseNumber(MinimumWidthBox.Text, out double widthMm) || widthMm < 0.0 ||
                !TryParseNumber(MinimumHeightBox.Text, out double heightMm) || heightMm < 0.0) return false;
            widthFeet = widthMm / 304.8;
            heightFeet = heightMm / 304.8;
            return true;
        }

        private static bool TryParseNumber(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            bool enabled = MinimumSizeCheckBox.IsChecked == true;
            MinimumWidthBox.IsEnabled = enabled;
            MinimumHeightBox.IsEnabled = enabled;
            _validated = false;
            if (RunButton != null) RunButton.IsEnabled = false;
            if (ValidationText != null) ValidationText.Text = "설정이 바뀌어 다시 검증해야 합니다.";
            RenderFullPreview();
            UpdateCounts();
        }

        private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderAll();
        private void PreviewTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderAll();
        private void TargetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitialized) RenderAll();
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_validated || !TryReadMinimum(out double minWidth, out double minHeight)) return;
            List<PatternPunchSelection> selections = BuildSelections();
            if (selections.Count == 0) return;
            Request = new PatternPunchRequest
            {
                Selections = selections,
                MinimumWidthFeet = minWidth,
                MinimumHeightFeet = minHeight,
            };
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private HashSet<PatternRegion> GetSelectedSet(PatternPunchTarget target)
        {
            if (!_selectedByTarget.TryGetValue(target, out HashSet<PatternRegion>? selected))
            {
                selected = new HashSet<PatternRegion>();
                _selectedByTarget.Add(target, selected);
            }
            return selected;
        }

        private List<PatternRegion> OrderedSelection(PatternPunchTarget target)
        {
            if (!_selectedByTarget.TryGetValue(target, out HashSet<PatternRegion>? selected) || selected.Count == 0)
                return new List<PatternRegion>();
            return target.Regions.Where(selected.Contains).ToList();
        }

        private List<PatternPunchSelection> BuildSelections()
        {
            var result = new List<PatternPunchSelection>();
            foreach (PatternPunchTarget target in _plan.Targets)
            {
                List<PatternRegion> regions = OrderedSelection(target);
                if (regions.Count == 0) continue;
                result.Add(new PatternPunchSelection { Target = target, Regions = regions });
            }
            return result;
        }

        private static void DrawSegments(Canvas canvas, IEnumerable<PatternSegment> segments, CanvasTransform transform,
            string color, double thickness, double opacity)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                foreach (PatternSegment segment in segments)
                {
                    context.BeginFigure(transform.Map(segment.Start), false, false);
                    context.LineTo(transform.Map(segment.End), true, false);
                }
            }
            geometry.Freeze();
            canvas.Children.Add(new WpfPath
            {
                Data = geometry,
                Stroke = MakeBrush(color),
                StrokeThickness = thickness,
                Opacity = opacity,
                IsHitTestVisible = false,
            });
        }

        private static Geometry MakeGeometry(IReadOnlyList<PatternPoint> points, CanvasTransform transform)
        {
            var geometry = new StreamGeometry();
            if (points.Count >= 3)
            {
                using StreamGeometryContext context = geometry.Open();
                context.BeginFigure(transform.Map(points[0]), true, true);
                for (int i = 1; i < points.Count; i++) context.LineTo(transform.Map(points[i]), true, false);
            }
            geometry.Freeze();
            return geometry;
        }

        private static int CountMaterialIslands(Paths64 paths)
        {
            if (paths.Count == 0) return 0;
            Path64 largest = paths.OrderByDescending(path => Math.Abs(Clipper.Area(path))).First();
            int outerSign = Math.Sign(Clipper.Area(largest));
            return paths.Count(path => Math.Sign(Clipper.Area(path)) == outerSign && Math.Abs(Clipper.Area(path)) > 10.0);
        }

        private static bool TouchesFaceBoundary(Path64 path, Paths64 facePaths)
        {
            const long tolerance = 3;
            foreach (Point64 point in path)
            {
                foreach (Path64 facePath in facePaths)
                {
                    for (int i = 0; i < facePath.Count; i++)
                    {
                        Point64 a = facePath[i];
                        Point64 b = facePath[(i + 1) % facePath.Count];
                        if (DistanceToSegment(point, a, b) <= tolerance) return true;
                    }
                }
            }
            return false;
        }

        private static double DistanceToSegment(Point64 point, Point64 a, Point64 b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double denominator = dx * dx + dy * dy;
            if (denominator <= 0.0) return Math.Sqrt((point.X - a.X) * (double)(point.X - a.X) + (point.Y - a.Y) * (double)(point.Y - a.Y));
            double t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / denominator;
            t = Math.Max(0.0, Math.Min(1.0, t));
            double x = a.X + t * dx;
            double y = a.Y + t * dy;
            return Math.Sqrt((point.X - x) * (point.X - x) + (point.Y - y) * (point.Y - y));
        }

        private static SolidColorBrush MakeBrush(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        private sealed class RegionSelectionTag
        {
            public RegionSelectionTag(PatternPunchTarget target, PatternRegion region)
            {
                Target = target;
                Region = region;
            }

            public PatternPunchTarget Target { get; }
            public PatternRegion Region { get; }
        }

        private readonly struct CanvasTransform
        {
            private CanvasTransform(PatternBounds bounds, double scale, double offsetX, double offsetY)
            {
                Bounds = bounds;
                Scale = scale;
                OffsetX = offsetX;
                OffsetY = offsetY;
            }

            private PatternBounds Bounds { get; }
            private double Scale { get; }
            private double OffsetX { get; }
            private double OffsetY { get; }

            public WpfPoint Map(PatternPoint point) => new WpfPoint(
                OffsetX + (point.X - Bounds.MinX) * Scale,
                OffsetY + (Bounds.MaxY - point.Y) * Scale);

            public static CanvasTransform Fit(PatternBounds bounds, double width, double height, double margin)
            {
                double availableWidth = Math.Max(10.0, width - margin * 2.0);
                double availableHeight = Math.Max(10.0, height - margin * 2.0);
                double scale = Math.Min(availableWidth / Math.Max(bounds.Width, 1e-9), availableHeight / Math.Max(bounds.Height, 1e-9));
                double contentWidth = bounds.Width * scale;
                double contentHeight = bounds.Height * scale;
                return new CanvasTransform(bounds, scale, (width - contentWidth) * 0.5, (height - contentHeight) * 0.5);
            }
        }
    }
}
