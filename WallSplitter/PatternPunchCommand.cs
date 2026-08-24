using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Clipper2Lib;

namespace WallSplitter
{
    [Transaction(TransactionMode.Manual)]
    public sealed class PatternPunchCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument? uiDocument = commandData.Application.ActiveUIDocument;
            if (uiDocument == null)
            {
                TaskDialog.Show("패턴 타공", "열려 있는 Revit 문서가 없습니다.");
                return Result.Cancelled;
            }

            Document document = uiDocument.Document;
            string stage = "면 선택";
            try
            {
                IList<Reference> references = uiDocument.Selection.PickObjects(
                    ObjectType.Face,
                    new PatternPunchFaceSelectionFilter(document),
                    "패턴이 보이는 벽·바닥·천장·커튼패널 면을 선택한 뒤 완료를 누르세요.");
                if (references.Count == 0) return Result.Cancelled;

                stage = "선택 면과 표시 패턴 분석";
                PatternPunchPlan plan = PatternPunchPlanBuilder.Build(document, document.ActiveView, references.ToList());
                stage = "패턴 타공 창 준비";
                var window = new PatternPunchWindow(plan);
                new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
                if (window.ShowDialog() != true || window.Request == null) return Result.Cancelled;

                stage = "타공 형상 검증과 적용";
                PatternPunchRequest request = window.Request;
                var successes = new List<string>();
                var failures = new List<string>();
                foreach (PatternPunchSelection selection in request.Selections)
                {
                    PatternPunchTarget target = selection.Target;
                    Element? element = document.GetElement(target.ElementId);
                    if (element == null)
                    {
                        failures.Add($"{target.Label}: 요소를 찾을 수 없습니다.");
                        continue;
                    }

                    Paths64 punchPaths = target.BuildPunchPaths(selection.Regions, request.MinimumWidthFeet, request.MinimumHeightFeet);
                    if (punchPaths.Count == 0)
                    {
                        failures.Add($"{target.Label}: 적용할 타공 영역이 없습니다.");
                        continue;
                    }

                    PunchExecutionResult probe = PatternPunchExecutor.Execute(document, element, plan, target, punchPaths, true);
                    if (!probe.Succeeded)
                    {
                        failures.Add($"{target.Label}: 사전 생성 실패 · {probe.Message}");
                        continue;
                    }

                    element = document.GetElement(target.ElementId);
                    if (element == null)
                    {
                        failures.Add($"{target.Label}: 사전 검증 후 요소를 다시 찾지 못했습니다.");
                        continue;
                    }
                    // 안전 복원 기록은 PatternPunchExecutor가 실제 타공 트랜잭션(그룹) 안에서 함께 커밋한다.
                    // 여기서 별도 트랜잭션으로 나중에 기록하면 되돌리기 한 번에 기록만 사라지는 문제가 있었다.
                    PunchExecutionResult applied = PatternPunchExecutor.Execute(document, element, plan, target, punchPaths, false);
                    if (applied.Succeeded)
                    {
                        successes.Add($"{target.Label}: {applied.CutCount:N0}개");
                    }
                    else
                    {
                        failures.Add($"{target.Label}: {applied.Message}");
                    }
                }

                string summary = $"완료 {successes.Count:N0}개 · 실패 {failures.Count:N0}개";
                string warningSummary = plan.Warnings.Count == 0
                    ? ""
                    : "\n\n확인 사항\n" + string.Join("\n", plan.Warnings.Distinct().Take(3));
                if (failures.Count > 0)
                {
                    TaskDialog.Show("패턴 타공 결과", summary + "\n\n" + string.Join("\n", failures.Take(12)) + warningSummary);
                }
                else
                {
                    TaskDialog.Show("패턴 타공 결과", summary + $"\n\n타공 대상: {request.Selections.Count:N0}개\n패턴: {plan.PatternName}" + warningSummary);
                }
                return successes.Count > 0 ? Result.Succeeded : Result.Cancelled;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (InvalidOperationException ex)
            {
                TaskDialog.Show("패턴 타공", "패턴 타공을 시작하지 못했습니다.\n\n" + ex.Message);
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Exception root = ex.GetBaseException();
                string detail = $"진행 단계: {stage}\n오류 종류: {root.GetType().Name}\n\n{root.Message}";
                message = detail;
                TaskDialog.Show("패턴 타공", "패턴 타공을 시작하지 못했습니다.\n\n" + detail);
                return Result.Cancelled;
            }
        }
    }

    internal sealed class PatternPunchFaceSelectionFilter : ISelectionFilter
    {
        private readonly Document _document;

        public PatternPunchFaceSelectionFilter(Document document)
        {
            _document = document;
        }

        public bool AllowElement(Element element) =>
            element is Wall || element is Floor || element is Ceiling || element is Panel;

        public bool AllowReference(Reference reference, XYZ position)
        {
            Element? element = _document.GetElement(reference.ElementId);
            return element != null && element.GetGeometryObjectFromReference(reference) is PlanarFace;
        }
    }

    internal sealed class PunchExecutionResult
    {
        public bool Succeeded { get; set; }
        public string Message { get; set; } = "";
        public int CutCount { get; set; }
        public string BeforeProfileJson { get; set; } = "";
        public string AfterProfileHash { get; set; } = "";
        public List<long> CreatedElementIds { get; set; } = new List<long>();
        public long OriginalPanelTypeId { get; set; } = -1;
    }

    internal static class PatternPunchExecutor
    {
        public static PunchExecutionResult Execute(Document document, Element element, PatternPunchPlan plan,
            PatternPunchTarget target, Paths64 punchPaths, bool probe)
        {
            if (element is Panel panel)
                return CurtainPanelPunchService.Execute(document, panel, plan, target, punchPaths, probe);

            ElementId sketchId = GetSketchId(element);
            if (element is Wall wall && sketchId == ElementId.InvalidElementId)
                return ExecuteWithNewWallSketch(document, wall, plan, target, punchPaths, probe);
            if (sketchId == ElementId.InvalidElementId)
                return new PunchExecutionResult { Message = "편집할 수 있는 프로파일 스케치를 찾지 못했습니다." };
            return ExecuteSketchDifference(document, element, sketchId, plan, target, punchPaths, probe, null);
        }

        private static PunchExecutionResult ExecuteWithNewWallSketch(Document document, Wall wall, PatternPunchPlan plan,
            PatternPunchTarget target, Paths64 punchPaths, bool probe)
        {
            if (!wall.CanHaveProfileSketch())
                return new PunchExecutionResult { Message = "이 벽 유형은 프로파일 스케치를 만들 수 없습니다." };

            using var group = new TransactionGroup(document, probe ? "패턴 타공 사전 검증" : "Sunny 패턴 타공");
            try
            {
                group.Start();
                ElementId sketchId;
                using (var transaction = new Transaction(document, "벽 프로파일 준비"))
                {
                    transaction.Start();
                    Sketch sketch = wall.CreateProfileSketch();
                    sketchId = sketch.Id;
                    transaction.Commit();
                }
                PunchExecutionResult result = ExecuteSketchDifference(document, wall, sketchId, plan, target, punchPaths, false, group);
                if (!result.Succeeded)
                {
                    group.RollBack();
                    return result;
                }
                if (probe)
                {
                    group.RollBack();
                }
                else
                {
                    AppendRecordWithinGroup(document, wall, plan, target, punchPaths, result);
                    group.Assimilate();
                }
                return result;
            }
            catch (Exception ex)
            {
                try { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); } catch { }
                return new PunchExecutionResult { Message = ex.Message };
            }
        }

        // existingGroup이 있으면 새 벽 스케치 생성과 한 그룹으로 묶기 위해 여기서는 별도 그룹을 열지 않는다.
        // existingGroup이 있을 때는 최종 Assimilate/RollBack 판단과 안전 복원 기록도 호출자(ExecuteWithNewWallSketch)가 맡는다.
        private static PunchExecutionResult ExecuteSketchDifference(Document document, Element host, ElementId sketchId,
            PatternPunchPlan plan, PatternPunchTarget target, Paths64 punchPaths, bool probe, TransactionGroup? existingGroup)
        {
            TransactionGroup? localGroup = null;
            if (existingGroup == null)
            {
                localGroup = new TransactionGroup(document, probe ? "패턴 타공 사전 검증" : "Sunny 패턴 타공");
                localGroup.Start();
            }

            var scope = new SketchEditScope(document, probe ? "패턴 타공 사전 검증" : "Sunny 패턴 타공");
            try
            {
                Sketch sketch = document.GetElement(sketchId) as Sketch
                    ?? throw new InvalidOperationException("호스트 스케치를 찾을 수 없습니다.");
                Plane plane = sketch.SketchPlane.GetPlane();
                double parallel = Math.Abs(plane.Normal.Normalize().DotProduct(target.Basis.Normal));
                if (parallel < 0.999)
                {
                    if (localGroup != null) localGroup.RollBack();
                    return ExecuteNativeOpenings(document, host, plan, target, punchPaths, probe);
                }

                PatternFaceBasis sketchBasis = new PatternFaceBasis
                {
                    Origin = plane.Origin,
                    XAxis = plane.XVec.Normalize(),
                    YAxis = plane.YVec.Normalize(),
                    Normal = plane.Normal.Normalize(),
                };
                Paths64 originalProfile = BuildSketchPaths(sketch, sketchBasis);
                if (originalProfile.Count == 0) throw new InvalidOperationException("기존 호스트 프로파일을 읽지 못했습니다.");
                Paths64 sketchPunch = MapPaths(punchPaths, target.Basis, sketchBasis);
                Paths64 remaining = PatternClipper.Difference(originalProfile, sketchPunch);
                if (remaining.Count == 0) throw new InvalidOperationException("타공이 호스트 전체를 제거합니다.");
                if (CountOuterIslands(remaining) > 1)
                    throw new InvalidOperationException("타공 후 남는 재료가 여러 조각으로 분리됩니다.");

                string beforeJson = PatternPunchRecordStore.SerializePaths(originalProfile);
                string afterHash = PatternPunchRecordStore.HashPaths(remaining);
                scope.Start(sketchId);
                using (var transaction = new Transaction(document, "패턴 타공 프로파일 작성"))
                {
                    transaction.Start();
                    foreach (ElementId id in sketch.GetAllElements()) document.Delete(id);
                    foreach (Path64 path in remaining)
                        CreatePathCurves(document, sketch.SketchPlane, sketchBasis, path);
                    transaction.Commit();
                }
                scope.Commit(new PatternPunchFailuresPreprocessor());

                var result = new PunchExecutionResult
                {
                    Succeeded = true,
                    CutCount = punchPaths.Count,
                    BeforeProfileJson = beforeJson,
                    AfterProfileHash = afterHash,
                };
                if (localGroup != null)
                {
                    if (probe)
                    {
                        localGroup.RollBack();
                    }
                    else
                    {
                        AppendRecordWithinGroup(document, host, plan, target, punchPaths, result);
                        localGroup.Assimilate();
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                try { if (scope.IsActive) scope.Cancel(); } catch { }
                try { if (localGroup?.GetStatus() == TransactionStatus.Started) localGroup.RollBack(); } catch { }
                return new PunchExecutionResult { Message = ex.Message };
            }
            finally
            {
                scope.Dispose();
                localGroup?.Dispose();
            }
        }

        private static PunchExecutionResult ExecuteNativeOpenings(Document document, Element host, PatternPunchPlan plan,
            PatternPunchTarget target, Paths64 punchPaths, bool probe)
        {
            using var transaction = new Transaction(document, probe ? "패턴 타공 사전 검증" : "Sunny 패턴 타공");
            try
            {
                transaction.Start();
                var created = new List<long>();
                foreach (Path64 path in punchPaths)
                {
                    CurveArray profile = BuildCurveArray(path, target.Basis, document.Application.ShortCurveTolerance);
                    Opening opening = document.Create.NewOpening(host, profile, true);
                    created.Add(GetElementIdValue(opening.Id));
                }
                var result = new PunchExecutionResult { Succeeded = true, CutCount = punchPaths.Count, CreatedElementIds = created };
                if (probe)
                {
                    transaction.RollBack();
                }
                else
                {
                    PatternPunchRecordStore.AppendEntity(host, plan, target, punchPaths, result);
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("개구부 트랜잭션을 완료하지 못했습니다.");
                }
                return result;
            }
            catch (Exception ex)
            {
                try { if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack(); } catch { }
                return new PunchExecutionResult { Message = ex.Message };
            }
        }

        // TransactionGroup이 아직 Assimilate되지 않은 상태에서만 호출한다. 별도의 새 Transaction으로 기록을 감싸
        // 같은 그룹 안에 안전하게 포함시키되, 기록 저장 실패가 이미 완료된 타공 자체를 취소시키지는 않는다.
        private static void AppendRecordWithinGroup(Document document, Element host, PatternPunchPlan plan,
            PatternPunchTarget target, Paths64 punchPaths, PunchExecutionResult result)
        {
            try
            {
                using var transaction = new Transaction(document, "패턴 타공 복원 기록");
                transaction.Start();
                PatternPunchRecordStore.AppendEntity(host, plan, target, punchPaths, result);
                transaction.Commit();
            }
            catch
            {
                // 기록 저장 실패가 이미 완료된 실제 타공 결과를 되돌리지는 않는다.
            }
        }

        internal static ElementId GetSketchId(Element element)
        {
            if (element is Wall wall) return wall.SketchId;
            if (element is Floor floor) return floor.SketchId;
            if (element is Ceiling ceiling) return ceiling.SketchId;
            return ElementId.InvalidElementId;
        }

        internal static Paths64 BuildSketchPaths(Sketch sketch, PatternFaceBasis basis)
        {
            var paths = new Paths64();
            foreach (CurveArray loop in sketch.Profile)
            {
                var points = new List<PatternPoint>();
                foreach (Curve curve in loop)
                {
                    IList<XYZ> tessellated = curve.Tessellate();
                    for (int i = 0; i < tessellated.Count - 1; i++) points.Add(basis.ToLocal(tessellated[i]));
                }
                if (points.Count >= 3) paths.Add(PatternClipper.ToPath(points));
            }
            return PatternClipper.Union(paths);
        }

        private static Paths64 MapPaths(Paths64 source, PatternFaceBasis sourceBasis, PatternFaceBasis targetBasis)
        {
            var result = new Paths64();
            foreach (Path64 path in source)
            {
                var mapped = new List<PatternPoint>();
                foreach (PatternPoint point in PatternClipper.FromPath(path))
                    mapped.Add(targetBasis.ToLocal(sourceBasis.ToWorld(point)));
                if (mapped.Count >= 3) result.Add(PatternClipper.ToPath(mapped));
            }
            return PatternClipper.Union(result);
        }

        internal static void CreatePathCurves(Document document, SketchPlane sketchPlane, PatternFaceBasis basis, Path64 path)
        {
            List<PatternPoint> points = CleanPath(PatternClipper.FromPath(path), document.Application.ShortCurveTolerance);
            if (points.Count < 3) throw new InvalidOperationException("너무 짧은 선 때문에 유효한 폐곡선을 만들 수 없습니다.");
            for (int i = 0; i < points.Count; i++)
            {
                XYZ a = basis.ToWorld(points[i]);
                XYZ b = basis.ToWorld(points[(i + 1) % points.Count]);
                if (a.DistanceTo(b) <= document.Application.ShortCurveTolerance)
                    throw new InvalidOperationException("Revit 최소 길이보다 짧은 프로파일 선이 있습니다.");
                document.Create.NewModelCurve(Line.CreateBound(a, b), sketchPlane);
            }
        }

        private static CurveArray BuildCurveArray(Path64 path, PatternFaceBasis basis, double shortTolerance)
        {
            List<PatternPoint> points = CleanPath(PatternClipper.FromPath(path), shortTolerance);
            if (points.Count < 3) throw new InvalidOperationException("유효한 타공 폐곡선이 아닙니다.");
            var curves = new CurveArray();
            for (int i = 0; i < points.Count; i++)
            {
                XYZ a = basis.ToWorld(points[i]);
                XYZ b = basis.ToWorld(points[(i + 1) % points.Count]);
                if (a.DistanceTo(b) <= shortTolerance) throw new InvalidOperationException("Revit 최소 길이보다 짧은 타공 선이 있습니다.");
                curves.Append(Line.CreateBound(a, b));
            }
            return curves;
        }

        private static List<PatternPoint> CleanPath(List<PatternPoint> points, double shortTolerance)
        {
            double tolerance = Math.Max(shortTolerance * 1.02, 1e-7);
            var cleaned = new List<PatternPoint>();
            foreach (PatternPoint point in points)
                if (cleaned.Count == 0 || (point - cleaned[cleaned.Count - 1]).Length > tolerance) cleaned.Add(point);
            if (cleaned.Count > 1 && (cleaned[0] - cleaned[cleaned.Count - 1]).Length <= tolerance) cleaned.RemoveAt(cleaned.Count - 1);

            bool changed = true;
            while (changed && cleaned.Count > 3)
            {
                changed = false;
                for (int i = 0; i < cleaned.Count; i++)
                {
                    PatternPoint a = cleaned[(i - 1 + cleaned.Count) % cleaned.Count];
                    PatternPoint b = cleaned[i];
                    PatternPoint c = cleaned[(i + 1) % cleaned.Count];
                    PatternPoint ab = b - a;
                    PatternPoint bc = c - b;
                    double cross = Math.Abs(ab.X * bc.Y - ab.Y * bc.X);
                    if (cross <= 1e-8 * Math.Max(1.0, ab.Length + bc.Length) || ab.Length <= tolerance || bc.Length <= tolerance)
                    {
                        cleaned.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }
            return cleaned;
        }

        private static int CountOuterIslands(Paths64 paths)
        {
            if (paths.Count == 0) return 0;
            Path64 largest = paths.OrderByDescending(path => Math.Abs(Clipper.Area(path))).First();
            int sign = Math.Sign(Clipper.Area(largest));
            return paths.Count(path => Math.Sign(Clipper.Area(path)) == sign && Math.Abs(Clipper.Area(path)) > 10.0);
        }

        internal static long GetElementIdValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }
    }

    internal sealed class PatternPunchFailuresPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning) failuresAccessor.DeleteWarning(failure);
                else if (failure.HasResolutions()) failuresAccessor.ResolveFailure(failure);
            }
            return FailureProcessingResult.ProceedWithCommit;
        }
    }
}
