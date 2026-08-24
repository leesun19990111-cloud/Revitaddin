using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Clipper2Lib;

namespace WallSplitter
{
    internal sealed class PatternFaceBasis
    {
        public XYZ Origin { get; set; } = XYZ.Zero;
        public XYZ XAxis { get; set; } = XYZ.BasisX;
        public XYZ YAxis { get; set; } = XYZ.BasisY;
        public XYZ Normal { get; set; } = XYZ.BasisZ;

        public PatternPoint ToLocal(XYZ point)
        {
            XYZ delta = point - Origin;
            return new PatternPoint(delta.DotProduct(XAxis), delta.DotProduct(YAxis));
        }

        public XYZ ToWorld(PatternPoint point) => Origin + XAxis * point.X + YAxis * point.Y;
    }

    internal sealed class PatternPunchTarget
    {
        public ElementId ElementId { get; set; } = ElementId.InvalidElementId;
        public ElementId MaterialElementId { get; set; } = ElementId.InvalidElementId;
        public string ElementUniqueId { get; set; } = "";
        public string Label { get; set; } = "";
        public string CategoryLabel { get; set; } = "";
        public List<ElementId> DisplayElementIds { get; set; } = new List<ElementId>();
        public PatternFaceBasis Basis { get; set; } = new PatternFaceBasis();
        public Paths64 FacePaths { get; set; } = new Paths64();
        public PatternBounds Bounds { get; set; }
        public List<PatternSegment> PatternSegments { get; set; } = new List<PatternSegment>();
        public List<PatternRegion> Regions { get; set; } = new List<PatternRegion>();
        public List<PatternRegion>? PrecomputedDisplayedRegions { get; set; }
        public string PrecomputedDisplayedWarning { get; set; } = "";
        public List<string> Warnings { get; set; } = new List<string>();

        public Paths64 BuildPunchPaths(IEnumerable<PatternRegion> selectedRegions, double minimumWidthFeet, double minimumHeightFeet)
        {
            var candidates = new Paths64();
            var available = new HashSet<PatternRegion>(Regions);
            var added = new HashSet<PatternRegion>();
            foreach (PatternRegion region in selectedRegions)
            {
                if (!available.Contains(region) || !added.Add(region)) continue;
                Paths64 clipped = PatternClipper.Intersect(
                    new Paths64 { PatternClipper.ToPath(region.Points) }, FacePaths);
                foreach (Path64 path in clipped)
                {
                    PatternBounds pathBounds = PatternBounds.FromPoints(PatternClipper.FromPath(path));
                    if (minimumWidthFeet > 0.0 && pathBounds.Width < minimumWidthFeet) continue;
                    if (minimumHeightFeet > 0.0 && pathBounds.Height < minimumHeightFeet) continue;
                    candidates.Add(path);
                }
            }
            if (candidates.Count == 0) return candidates;
            return PatternClipper.Union(candidates);
        }

        public int CountEligibleRegions(IEnumerable<PatternRegion> selectedRegions, double minimumWidthFeet, double minimumHeightFeet)
        {
            var available = new HashSet<PatternRegion>(Regions);
            var counted = new HashSet<PatternRegion>();
            int result = 0;
            foreach (PatternRegion region in selectedRegions)
            {
                if (!available.Contains(region) || !counted.Add(region)) continue;
                Paths64 clipped = PatternClipper.Intersect(
                    new Paths64 { PatternClipper.ToPath(region.Points) }, FacePaths);
                if (clipped.Any(path =>
                {
                    PatternBounds bounds = PatternBounds.FromPoints(PatternClipper.FromPath(path));
                    return (minimumWidthFeet <= 0.0 || bounds.Width >= minimumWidthFeet) &&
                           (minimumHeightFeet <= 0.0 || bounds.Height >= minimumHeightFeet);
                })) result++;
            }
            return result;
        }
    }

    internal sealed class PatternPunchPlan
    {
        public PatternDefinition Pattern { get; set; } = null!;
        public ElementId PatternElementId { get; set; } = ElementId.InvalidElementId;
        public string PatternName { get; set; } = "";
        public string PatternLayerLabel { get; set; } = "표면 전경";
        public int ViewScale { get; set; } = 1;
        public double LengthScale { get; set; } = 1.0;
        public List<PatternPunchTarget> Targets { get; set; } = new List<PatternPunchTarget>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    internal sealed class PatternPunchSelection
    {
        public PatternPunchTarget Target { get; set; } = null!;
        public List<PatternRegion> Regions { get; set; } = new List<PatternRegion>();
    }

    internal sealed class PatternPunchRequest
    {
        public List<PatternPunchSelection> Selections { get; set; } = new List<PatternPunchSelection>();
        public double MinimumWidthFeet { get; set; }
        public double MinimumHeightFeet { get; set; }
    }

    internal static class PatternPunchPlanBuilder
    {
        public static PatternPunchPlan Build(Document document, View view, IReadOnlyList<Reference> references)
        {
            if (references.Count == 0) throw new InvalidOperationException("선택한 패턴 면이 없습니다.");
            if (view.IsTemplate || !IsSupported2DView(view))
                throw new InvalidOperationException("패턴 타공은 대상 면의 패턴이 보이는 평면·천장평면·입면·단면 2D 뷰에서 실행해 주세요.");

            PatternDefinition? definition = null;
            ElementId? patternId = null;
            string patternName = "";
            string layerLabel = "";
            var plan = new PatternPunchPlan { ViewScale = Math.Max(1, view.Scale) };

            foreach (Reference reference in references)
            {
                Element element = document.GetElement(reference.ElementId)
                    ?? throw new InvalidOperationException("선택한 요소를 찾을 수 없습니다.");
                string elementLabel = ElementLabel(element);
                if (element.GetGeometryObjectFromReference(reference) is not PlanarFace referencedFace)
                    throw new InvalidOperationException($"{elementLabel}: 평평한 면이 아닙니다.");

                ResolvedPlanarFace resolvedFace = ResolvePlanarFace(document, view, element, reference, referencedFace);
                ElementId materialId = referencedFace.MaterialElementId;
                if (materialId == ElementId.InvalidElementId || document.GetElement(materialId) is not Material material)
                    throw new InvalidOperationException($"{elementLabel}: 선택한 면의 재료를 찾을 수 없습니다.");

                (ElementId selectedPatternId, string selectedLayer) = SelectSurfacePattern(document, material);
                if (patternId == null)
                {
                    patternId = selectedPatternId;
                    layerLabel = selectedLayer;
                    FillPatternElement patternElement = document.GetElement(selectedPatternId) as FillPatternElement
                        ?? throw new InvalidOperationException("표면 패턴 요소를 찾을 수 없습니다.");
                    definition = FromRevitPattern(patternElement);
                    patternName = patternElement.Name;
                    plan.LengthScale = definition.Target == FillPatternTarget.Drafting ? Math.Max(1, view.Scale) : 1.0;
                }
                else if (selectedPatternId != patternId)
                {
                    throw new InvalidOperationException($"{elementLabel}: 첫 번째 면과 다른 패턴을 사용하고 있습니다. 같은 패턴의 면만 한 번에 선택해 주세요.");
                }

                PatternFaceBasis basis = CreateBasis(resolvedFace.Face, resolvedFace.Transform, view, definition!.Target, elementLabel);
                Paths64 facePaths = BuildFacePaths(resolvedFace.Face, resolvedFace.Transform, basis);
                if (facePaths.Count == 0) throw new InvalidOperationException($"{elementLabel}: 면 경계를 읽지 못했습니다.");
                PatternBounds bounds = PatternBounds.FromPoints(facePaths.SelectMany(PatternClipper.FromPath));
                double expansion = EstimatePatternSpan(definition, plan.LengthScale) * 1.5;
                PatternBounds generationBounds = bounds.Expand(expansion);
                PatternLineGenerationResult generated = PatternLineGenerator.Generate(definition, generationBounds, plan.LengthScale);
                List<PatternRegion> regions = PatternRegionDetector.Detect(generated.Segments, generationBounds, out string regionWarning);

                var target = new PatternPunchTarget
                {
                    ElementId = element.Id,
                    MaterialElementId = materialId,
                    ElementUniqueId = element.UniqueId,
                    Label = elementLabel,
                    CategoryLabel = element.Category?.Name ?? element.GetType().Name,
                    DisplayElementIds = CollectDisplayElementIds(document, element, referencedFace),
                    Basis = basis,
                    FacePaths = facePaths,
                    Bounds = bounds,
                    PatternSegments = generated.Segments,
                    Regions = regions.Where(region => PatternClipper.Contains(facePaths, region.Centroid) ||
                                                      PatternClipper.Intersect(new Paths64 { PatternClipper.ToPath(region.Points) }, facePaths).Count > 0).ToList(),
                };
                if (element is Panel panel && panel.Symbol?.Family is Family panelFamily && !panelFamily.IsUserCreated)
                    target.Warnings.Add("시스템 커튼패널은 선택 패널 전용 고정형 로드 패밀리로 대체됩니다. 이후 커튼그리드 셀 크기를 바꾸면 자동으로 늘어나지 않습니다.");
                target.Warnings.AddRange(generated.Warnings);
                if (!string.IsNullOrWhiteSpace(regionWarning)) target.Warnings.Add(regionWarning);
                plan.Targets.Add(target);
            }

            if (plan.Targets.GroupBy(target => target.ElementId).Any(group => group.Count() > 1))
                throw new InvalidOperationException("현재 버전에서는 같은 요소의 여러 면을 한 번에 타공할 수 없습니다. 요소마다 한 면씩 나누어 실행해 주세요.");

            ApplyDisplayedPatternGeometry(document, view, plan.Targets);

            foreach (string warning in plan.Targets.SelectMany(target => target.Warnings).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct())
                plan.Warnings.Add(warning);

            plan.Pattern = definition!;
            plan.PatternElementId = patternId!;
            plan.PatternName = patternName;
            plan.PatternLayerLabel = layerLabel;
            return plan;
        }

        private static void ApplyDisplayedPatternGeometry(Document document, View view, IReadOnlyList<PatternPunchTarget> targets)
        {
            Dictionary<ElementId, List<PatternSegment>> displayed;
            Dictionary<ElementId, string> diagnostics;
            try
            {
                displayed = PatternDisplayedLineCollector.Collect(document, view, targets, out diagnostics);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("현재 뷰에 실제 표시된 패턴 선을 읽지 못했습니다. 대상 면 전체가 패턴과 함께 보이는 2D 뷰에서 다시 실행해 주세요.\n\n" + ex.Message, ex);
            }

            foreach (PatternPunchTarget target in targets)
            {
                if (!displayed.TryGetValue(target.ElementId, out List<PatternSegment>? segments) || segments.Count == 0)
                {
                    string diagnostic = diagnostics.TryGetValue(target.ElementId, out string? value)
                        ? "\n수집 진단: " + value
                        : "";
                    throw new InvalidOperationException($"{target.Label}: 현재 뷰에서 표시 패턴 선을 찾지 못했습니다. 대상 면이 현재 뷰에서 실제로 보이는지 확인해 주세요.{diagnostic}");
                }
                List<PatternRegion> regions;
                string warning;
                if (target.PrecomputedDisplayedRegions != null)
                {
                    regions = target.PrecomputedDisplayedRegions;
                    warning = target.PrecomputedDisplayedWarning;
                    target.PrecomputedDisplayedRegions = null;
                    target.PrecomputedDisplayedWarning = "";
                }
                else
                {
                    regions = PatternRegionDetector.Detect(segments, target.Bounds, out warning);
                }
                regions = regions.Where(region => PatternClipper.Contains(target.FacePaths, region.Centroid) ||
                                                  PatternClipper.Intersect(new Paths64 { PatternClipper.ToPath(region.Points) }, target.FacePaths).Count > 0).ToList();
                if (regions.Count == 0)
                    throw new InvalidOperationException($"{target.Label}: 화면에 표시된 패턴에서 닫힌 영역을 찾지 못했습니다. 대상 면 전체가 현재 뷰의 자르기 영역 안에 보이도록 한 뒤 다시 시도해 주세요." +
                                                        (string.IsNullOrWhiteSpace(warning) ? "" : "\n" + warning));
                target.PatternSegments = segments;
                target.Regions = regions;
                if (!string.IsNullOrWhiteSpace(warning)) target.Warnings.Add(warning);
            }
        }

        private static PatternDefinition FromRevitPattern(FillPatternElement element)
        {
            FillPattern pattern = element.GetFillPattern();
            if (pattern.IsSolidFill) throw new InvalidOperationException("솔리드 채우기에는 선택할 폐영역이 없습니다.");
            IList<FillGrid> grids = pattern.GetFillGrids();
            var result = new PatternDefinition
            {
                Name = element.Name,
                Target = pattern.Target,
                HostOrientation = pattern.HostOrientation,
                SourceElementId = element.Id,
                SourceLabel = "선택한 면의 재료",
                SourceUnitLabel = "Revit 패턴",
            };
            foreach (FillGrid grid in grids)
            {
                IList<double> segments = grid.GetSegments();
                result.Grids.Add(new PatternGridDefinition
                {
                    AngleDegrees = grid.Angle * 180.0 / Math.PI,
                    OriginX = grid.Origin.U,
                    OriginY = grid.Origin.V,
                    Shift = grid.Shift,
                    Offset = grid.Offset,
                    Segments = PatternSegmentCodec.FromRevit(segments),
                });
            }
            return result;
        }

        private static (ElementId PatternId, string LayerLabel) SelectSurfacePattern(Document document, Material material)
        {
            if (IsUsablePattern(document, material.SurfaceForegroundPatternId))
                return (material.SurfaceForegroundPatternId, "표면 전경");
            if (IsUsablePattern(document, material.SurfaceBackgroundPatternId))
                return (material.SurfaceBackgroundPatternId, "표면 배경");
            throw new InvalidOperationException($"재료 '{material.Name}'에 타공에 사용할 비솔리드 표면 패턴이 없습니다.");
        }

        private static bool IsUsablePattern(Document document, ElementId id)
        {
            if (id == ElementId.InvalidElementId || document.GetElement(id) is not FillPatternElement element) return false;
            try { return !element.GetFillPattern().IsSolidFill; }
            catch { return false; }
        }

        private static PatternFaceBasis CreateBasis(PlanarFace face, Transform geometryTransform, View view,
            FillPatternTarget target, string elementLabel)
        {
            XYZ origin = geometryTransform.OfPoint(face.Origin);
            XYZ normal = geometryTransform.OfVector(face.FaceNormal).Normalize();
            XYZ faceX = geometryTransform.OfVector(face.XVector).Normalize();
            XYZ faceY = geometryTransform.OfVector(face.YVector).Normalize();
            XYZ xAxis;
            XYZ yAxis;
            if (target == FillPatternTarget.Drafting)
            {
                XYZ viewDirection = view.ViewDirection.Normalize();
                double alignment = Math.Min(1.0, Math.Abs(normal.DotProduct(viewDirection)));
                double frontDeviation = Math.Acos(alignment) * 180.0 / Math.PI;
                const double maximumFrontDeviation = 2.0;
                if (frontDeviation > maximumFrontDeviation)
                {
                    throw new InvalidOperationException(
                        $"{elementLabel}: 선택 면이 현재 뷰의 정면에서 {frontDeviation:0.0}° 벗어나 있습니다. (0°가 정면)\n" +
                        $"현재 뷰: {view.Name} · {view.ViewType}\n\n" +
                        "끝면·상하면·리빌면이 아니라 화면에서 패턴이 보이는 넓은 재료면을 다시 선택해 주세요.");
                }

                xAxis = ProjectToPlane(view.RightDirection, normal);
                yAxis = ProjectToPlane(view.UpDirection, normal);
                if (xAxis.GetLength() < 1e-9 || yAxis.GetLength() < 1e-9)
                    throw new InvalidOperationException($"{elementLabel}: 현재 뷰의 화면 축으로 선택 면 좌표를 만들 수 없습니다. 다른 정면 2D 뷰에서 다시 실행해 주세요.");
                xAxis = xAxis.Normalize();
                yAxis = (yAxis - xAxis * yAxis.DotProduct(xAxis)).Normalize();
                if (yAxis.DotProduct(view.UpDirection) < 0.0) yAxis = -yAxis;
            }
            else
            {
                xAxis = faceX;
                yAxis = faceY;
                if (xAxis.CrossProduct(yAxis).DotProduct(normal) < 0.0) yAxis = -yAxis;
            }
            return new PatternFaceBasis { Origin = origin, XAxis = xAxis, YAxis = yAxis, Normal = normal };
        }

        private static bool IsSupported2DView(View view)
        {
            return view.ViewType == ViewType.FloorPlan ||
                   view.ViewType == ViewType.CeilingPlan ||
                   view.ViewType == ViewType.EngineeringPlan ||
                   view.ViewType == ViewType.AreaPlan ||
                   view.ViewType == ViewType.Elevation ||
                   view.ViewType == ViewType.Section ||
                   view.ViewType == ViewType.Detail;
        }

        private static Paths64 BuildFacePaths(PlanarFace face, Transform geometryTransform, PatternFaceBasis basis)
        {
            var paths = new Paths64();
            foreach (CurveLoop loop in face.GetEdgesAsCurveLoops())
            {
                var points = new List<PatternPoint>();
                foreach (Curve curve in loop)
                {
                    IList<XYZ> tessellated = curve.Tessellate();
                    for (int i = 0; i < tessellated.Count - 1; i++)
                        points.Add(basis.ToLocal(geometryTransform.OfPoint(tessellated[i])));
                }
                if (points.Count >= 3) paths.Add(PatternClipper.ToPath(points));
            }
            return PatternClipper.Union(paths);
        }

        private static ResolvedPlanarFace ResolvePlanarFace(Document document, View view, Element element,
            Reference reference, PlanarFace referencedFace)
        {
            string stableReference = "";
            try { stableReference = reference.ConvertToStableRepresentation(document); }
            catch { }

            if (!string.IsNullOrWhiteSpace(stableReference))
            {
                try
                {
                    var options = new Options
                    {
                        ComputeReferences = true,
                        IncludeNonVisibleObjects = true,
                        View = view,
                    };
                    GeometryElement geometry = element.get_Geometry(options);
                    if (geometry != null && TryFindReferencedPlanarFace(document, geometry, stableReference,
                            Transform.Identity, 0, out PlanarFace? matchedFace, out Transform? matchedTransform))
                    {
                        return new ResolvedPlanarFace(matchedFace!, matchedTransform!);
                    }
                }
                catch
                {
                    // 일부 패밀리는 뷰별 형상을 다시 순회할 수 없다. 아래 배치 변환 fallback을 사용한다.
                }
            }

            Transform fallback = Transform.Identity;
            if (element is Instance instance)
            {
                Transform instanceTransform = instance.GetTotalTransform();
                // FamilyInstance의 참조 Face는 대개 심벌 좌표다. 면의 무한 평면 거리만 비교하면
                // 면 안쪽 방향으로 이동한 인스턴스를 구분하지 못하므로 선택 UV의 화면 위치를 비교한다.
                fallback = instanceTransform;
                if (TryGetReferenceScreenError(reference, referencedFace, Transform.Identity, view, out double identityError) &&
                    TryGetReferenceScreenError(reference, referencedFace, instanceTransform, view, out double transformedError))
                {
                    double decisionTolerance = Math.Max(document.Application.VertexTolerance * 2.0, 1e-6);
                    if (identityError + decisionTolerance < transformedError) fallback = Transform.Identity;
                }
            }
            return new ResolvedPlanarFace(referencedFace, fallback);
        }

        private static bool TryFindReferencedPlanarFace(Document document, GeometryElement geometry,
            string stableReference, Transform accumulated, int depth,
            out PlanarFace? matchedFace, out Transform? matchedTransform)
        {
            if (depth > 16)
            {
                matchedFace = null;
                matchedTransform = null;
                return false;
            }

            foreach (GeometryObject geometryObject in geometry)
            {
                if (geometryObject is Solid solid)
                {
                    foreach (Face candidate in solid.Faces)
                    {
                        if (candidate is PlanarFace planarFace && ReferenceMatches(document, candidate.Reference, stableReference))
                        {
                            matchedFace = planarFace;
                            matchedTransform = accumulated;
                            return true;
                        }
                    }
                }
                else if (geometryObject is GeometryInstance geometryInstance)
                {
                    GeometryElement symbolGeometry;
                    try { symbolGeometry = geometryInstance.GetSymbolGeometry(); }
                    catch { continue; }
                    Transform nested = accumulated.Multiply(geometryInstance.Transform);
                    if (TryFindReferencedPlanarFace(document, symbolGeometry, stableReference, nested, depth + 1,
                            out matchedFace, out matchedTransform))
                        return true;
                }
            }

            matchedFace = null;
            matchedTransform = null;
            return false;
        }

        private static bool ReferenceMatches(Document document, Reference? candidate, string stableReference)
        {
            if (candidate == null) return false;
            try { return string.Equals(candidate.ConvertToStableRepresentation(document), stableReference, StringComparison.Ordinal); }
            catch { return false; }
        }

        private static bool TryGetReferenceScreenError(Reference reference, PlanarFace face, Transform transform,
            View view, out double error)
        {
            try
            {
                UV uv = reference.UVPoint;
                XYZ globalPoint = reference.GlobalPoint;
                if (uv == null || globalPoint == null)
                {
                    error = double.PositiveInfinity;
                    return false;
                }
                XYZ evaluated = transform.OfPoint(face.Evaluate(uv));
                XYZ delta = evaluated - globalPoint;
                double right = delta.DotProduct(view.RightDirection.Normalize());
                double up = delta.DotProduct(view.UpDirection.Normalize());
                error = Math.Sqrt(right * right + up * up);
                return !double.IsNaN(error) && !double.IsInfinity(error);
            }
            catch
            {
                error = double.PositiveInfinity;
                return false;
            }
        }

        private static List<ElementId> CollectDisplayElementIds(Document document, Element element, PlanarFace face)
        {
            var aliases = new HashSet<ElementId> { element.Id };
            try
            {
                foreach (ElementId id in element.GetGeneratingElementIds(face))
                    if (id != ElementId.InvalidElementId) aliases.Add(id);
            }
            catch { }

            if (element.GetTypeId() != ElementId.InvalidElementId) aliases.Add(element.GetTypeId());
            var pending = new Queue<FamilyInstance>();
            var traversed = new HashSet<ElementId>();
            if (element is FamilyInstance rootInstance) pending.Enqueue(rootInstance);
            while (pending.Count > 0)
            {
                FamilyInstance instance = pending.Dequeue();
                if (!traversed.Add(instance.Id)) continue;
                if (instance.Symbol != null) aliases.Add(instance.Symbol.Id);
                ICollection<ElementId> childIds;
                try { childIds = instance.GetSubComponentIds(); }
                catch { continue; }
                foreach (ElementId childId in childIds)
                {
                    aliases.Add(childId);
                    if (document.GetElement(childId) is FamilyInstance child) pending.Enqueue(child);
                }
            }
            return aliases.ToList();
        }

        private sealed class ResolvedPlanarFace
        {
            internal ResolvedPlanarFace(PlanarFace face, Transform transform)
            {
                Face = face;
                Transform = transform;
            }

            internal PlanarFace Face { get; }
            internal Transform Transform { get; }
        }

        private static double EstimatePatternSpan(PatternDefinition definition, double lengthScale)
        {
            var values = new List<double>();
            foreach (PatternGridDefinition grid in definition.Grids)
            {
                if (Math.Abs(grid.Offset) > 1e-9) values.Add(Math.Abs(grid.Offset * lengthScale));
                double cycle = grid.Segments.Sum(value => Math.Abs(value)) * lengthScale;
                if (cycle > 1e-9) values.Add(cycle);
            }
            if (values.Count == 0) return 1.0;
            values.Sort();
            return Math.Max(values[values.Count / 2], 1e-4);
        }

        private static XYZ ProjectToPlane(XYZ vector, XYZ normal) => vector - normal * vector.DotProduct(normal);
        private static string ElementLabel(Element element) =>
            $"{element.Category?.Name ?? element.GetType().Name} · {element.Name} · ID {PatternPunchExecutor.GetElementIdValue(element.Id)}";
    }
}
