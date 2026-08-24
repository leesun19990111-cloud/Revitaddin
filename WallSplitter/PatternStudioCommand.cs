using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    [Transaction(TransactionMode.Manual)]
    public class PatternStudioCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument? uiDocument = commandData.Application.ActiveUIDocument;
            if (uiDocument == null)
            {
                TaskDialog.Show("패턴 스튜디오", "열려 있는 Revit 문서가 없습니다.");
                return Result.Cancelled;
            }

            Document document = uiDocument.Document;
            PatternStudioSaveRequest request;
            try
            {
                List<PatternDefinition> sources = CollectPatterns(document);
                var window = new PatternStudioWindow(sources, CollectPatternNames(document));
                new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
                if (window.ShowDialog() != true || window.SaveRequest == null) return Result.Cancelled;
                request = window.SaveRequest;
            }
            catch (Exception ex)
            {
                message = string.Empty;
                TaskDialog.Show("패턴 스튜디오", "패턴 편집 창을 열지 못했습니다.\n\n" + ex.GetBaseException().Message);
                return Result.Cancelled;
            }

            try
            {
                FillPattern revitPattern = BuildFillPattern(request.Pattern, request.Name);
                TransactionStatus status;
                using (var transaction = new Transaction(document, request.OverwriteSource
                           ? "패턴 스튜디오 원본 패턴 수정"
                           : "패턴 스튜디오 새 패턴 저장"))
                {
                    transaction.Start();
                    try
                    {
                        if (request.OverwriteSource)
                        {
                            if (request.SourceElementId == null || document.GetElement(request.SourceElementId) is not FillPatternElement sourceElement)
                                throw new InvalidOperationException("덮어쓸 원본 패턴을 찾을 수 없습니다.");
                            sourceElement.SetFillPattern(revitPattern);
                        }
                        else
                        {
                            FillPatternElement? collision = FindPatternByName(document, request.Pattern.Target, request.Name);
                            if (collision != null)
                                throw new InvalidOperationException($"'{request.Name}' 이름의 {TargetLabel(request.Pattern.Target)} 패턴이 이미 있습니다.");
                            _ = FillPatternElement.Create(document, revitPattern);
                        }
                        status = transaction.Commit();
                    }
                    catch
                    {
                        if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                        throw;
                    }
                }

                if (status != TransactionStatus.Committed)
                {
                    TaskDialog.Show("패턴 스튜디오", $"패턴이 Revit에 저장되지 않았습니다 (트랜잭션: {status}).");
                    message = string.Empty;
                    return Result.Cancelled;
                }

                string action = request.OverwriteSource ? "원본 패턴을 수정했습니다." : "새 패턴을 만들었습니다.";
                TaskDialog.Show("패턴 스튜디오", $"{action}\n\n이름: {request.Name}\n유형: {TargetLabel(request.Pattern.Target)}\n선군: {request.Pattern.Grids.Count}개");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("패턴 스튜디오", "패턴을 저장하지 못했습니다.\n\n" + ex.Message);
                message = string.Empty;
                return Result.Cancelled;
            }
        }

        internal static List<PatternDefinition> CollectPatterns(Document document)
        {
            var result = new List<PatternDefinition>();
            IEnumerable<FillPatternElement> elements = new FilteredElementCollector(document)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>();

            foreach (FillPatternElement element in elements)
            {
                FillPattern pattern;
                try { pattern = element.GetFillPattern(); }
                catch { continue; }
                if (pattern.IsSolidFill) continue;

                IList<FillGrid> grids;
                try { grids = pattern.GetFillGrids(); }
                catch { continue; }
                if (grids.Count == 0) continue;

                var definition = new PatternDefinition
                {
                    Name = element.Name ?? pattern.Name ?? "이름 없는 패턴",
                    Description = "",
                    Target = pattern.Target,
                    HostOrientation = pattern.HostOrientation,
                    SourceElementId = element.Id,
                    SourceLabel = "현재 Revit 문서",
                    SourceUnitLabel = "Revit 패턴",
                };

                bool allSegmentsRead = true;
                foreach (FillGrid grid in grids)
                {
                    IList<double> segments;
                    try { segments = grid.GetSegments(); }
                    catch
                    {
                        // 세그먼트를 읽지 못한 점·대시 선군을 빈 배열(실선)로 바꾸면
                        // 원본 패턴과 전혀 다른 미리보기 및 저장 결과가 생긴다.
                        allSegmentsRead = false;
                        break;
                    }
                    definition.Grids.Add(new PatternGridDefinition
                    {
                        // Revit FillGrid.Angle은 실제 API에서 라디안으로 읽힌다.
                        // 내부 공통 모델과 PAT는 도 단위를 사용하므로 경계에서 변환한다.
                        AngleDegrees = RadiansToDegrees(grid.Angle),
                        OriginX = grid.Origin.U,
                        OriginY = grid.Origin.V,
                        Shift = grid.Shift,
                        Offset = grid.Offset,
                        Segments = PatternSegmentCodec.FromRevit(segments),
                    });
                }
                if (allSegmentsRead && definition.Grids.Count == grids.Count)
                    result.Add(definition);
            }
            return result;
        }

        internal static List<PatternDefinition> CollectPatternNames(Document document)
        {
            var result = new List<PatternDefinition>();
            IEnumerable<FillPatternElement> elements = new FilteredElementCollector(document)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>();

            foreach (FillPatternElement element in elements)
            {
                try
                {
                    FillPattern pattern = element.GetFillPattern();
                    result.Add(new PatternDefinition
                    {
                        Name = element.Name,
                        Target = pattern.Target,
                        SourceElementId = element.Id,
                    });
                }
                catch
                {
                    // API에서 읽을 수 없는 패턴은 저장 이름 판정에서도 제외한다.
                }
            }

            return result;
        }

        internal static FillPattern BuildFillPattern(PatternDefinition definition, string name)
        {
            var grids = new List<FillGrid>(definition.Grids.Count);
            foreach (PatternGridDefinition source in definition.Grids)
            {
                var grid = new FillGrid(DegreesToRadians(source.AngleDegrees), source.Offset)
                {
                    Origin = new UV(source.OriginX, source.OriginY),
                    Shift = source.Shift,
                };
                if (source.Segments.Count > 0)
                    grid.SetSegments(PatternSegmentCodec.ToRevit(source.Segments));
                grids.Add(grid);
            }

            var pattern = new FillPattern(name, definition.Target, definition.HostOrientation);
            pattern.SetFillGrids(grids);
            // PAT의 길이 0 세그먼트(점)는 Revit에서 보이도록 내부적으로 짧은 선으로 확장해야 한다.
            pattern.ExpandDots();
            return pattern;
        }

        internal static FillPatternElement? FindPatternByName(Document document, FillPatternTarget target, string name)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(element =>
                {
                    try
                    {
                        FillPattern pattern = element.GetFillPattern();
                        return pattern.Target == target && string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                });
        }

        internal static string TargetLabel(FillPatternTarget target) => target == FillPatternTarget.Model ? "모델" : "제도";
        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
        private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;
    }
}
