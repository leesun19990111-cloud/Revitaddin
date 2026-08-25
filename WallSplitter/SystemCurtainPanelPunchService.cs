using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Clipper2Lib;

namespace WallSplitter
{
    // 저장할 수 없는 Revit 시스템 커튼패널은 EditFamily로 복제할 수 없다.
    // 현재 패널의 단순 프리즘 형상만 고정형 로드 패밀리로 다시 만들고, 실제 교체 결과가
    // 원래 위치·두께·재료와 일치할 때만 선택 패널에 적용한다.
    internal static class SystemCurtainPanelPunchService
    {
        private const string MaterialParameterName = "SunnyTools_패널재료";
        private const double MaximumDepthRelativeError = 0.02;
        private const double MaximumVolumeRelativeError = 0.03;
        private const double MaximumOutlineRelativeError = 0.01;

        internal static PunchExecutionResult Execute(Document document, Panel panel, FamilySymbol originalSymbol,
            PatternPunchPlan plan, PatternPunchTarget target, Paths64 punchPaths, bool probe)
        {
            Document? familyDocument = null;
            string? familyPath = null;
            try
            {
                if (punchPaths.Count == 0)
                    throw new InvalidOperationException("시스템 커튼패널에 적용할 타공 경계가 없습니다.");
                if (target.MaterialElementId == ElementId.InvalidElementId ||
                    document.GetElement(target.MaterialElementId) is not Material)
                    throw new InvalidOperationException("선택 면의 패널 재료를 찾을 수 없습니다.");

                double thickness = GetSystemPanelThickness(panel, originalSymbol,
                    document.Application.ShortCurveTolerance);
                PrismInfo prism = ValidateOriginalPrism(document, panel, target, thickness);
                Paths64 remainingPaths = BuildRemainingPaths(document, target.FacePaths, punchPaths);

                familyDocument = OpenCurtainWallPanelFamilyDocument(document.Application, out string templatePath);
                ValidateCurtainPanelTemplate(familyDocument, templatePath);
                BuildReplacementFamily(document, familyDocument, panel, target, remainingPaths, prism);

                string folder = Path.Combine(Path.GetTempPath(), "SunnyTools", "PatternPanels");
                Directory.CreateDirectory(folder);
                string cleanName = MakeSafeFileName(originalSymbol.Name);
                if (cleanName.Length > 70) cleanName = cleanName.Substring(0, 70);
                string uniqueName = $"{cleanName}_Sunny시스템타공_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
                familyPath = Path.Combine(folder, uniqueName + ".rfa");
                familyDocument.SaveAs(familyPath, new SaveAsOptions
                {
                    OverwriteExistingFile = false,
                    MaximumBackups = 1,
                });
                familyDocument.Close(false);
                familyDocument = null;

                return LoadAssignAndValidate(document, panel, originalSymbol, plan, target, punchPaths, remainingPaths,
                    prism.Thickness, familyPath, probe);
            }
            catch (Exception ex)
            {
                Exception root = ex.GetBaseException();
                return Failure("시스템 커튼패널 대체 타공을 완료하지 못했습니다. " + root.Message);
            }
            finally
            {
                if (familyDocument != null)
                {
                    try { familyDocument.Close(false); } catch { }
                }
                DeleteTemporaryFamilyFiles(familyPath);
            }
        }

        private static double GetSystemPanelThickness(Panel panel, FamilySymbol originalSymbol, double shortTolerance)
        {
            Parameter? parameter = panel.get_Parameter(BuiltInParameter.CURTAIN_WALL_SYSPANEL_THICKNESS);
            if (parameter == null || parameter.StorageType != StorageType.Double || !parameter.HasValue)
                parameter = originalSymbol.get_Parameter(BuiltInParameter.CURTAIN_WALL_SYSPANEL_THICKNESS);
            if (parameter == null || parameter.StorageType != StorageType.Double || !parameter.HasValue)
                throw new InvalidOperationException("시스템 패널 유형에서 두께 값을 찾지 못했습니다.");

            double thickness = parameter.AsDouble();
            if (double.IsNaN(thickness) || double.IsInfinity(thickness) || thickness <= shortTolerance)
                throw new InvalidOperationException("시스템 패널 두께가 Revit 최소 길이보다 작거나 유효하지 않습니다.");
            return thickness;
        }

        private static PrismInfo ValidateOriginalPrism(Document document, Panel panel,
            PatternPunchTarget target, double thickness)
        {
            List<SolidSnapshot> solids = CollectSolidSnapshots(document, panel);
            if (solids.Count != 1)
                throw new InvalidOperationException(
                    $"현재 버전은 유효한 솔리드가 하나인 단순 시스템 패널만 지원합니다. 감지된 솔리드: {solids.Count:N0}개");

            if (CountMaterialIslands(target.FacePaths) != 1)
                throw new InvalidOperationException("선택 면이 하나의 연속된 패널 외곽으로 이루어져 있지 않습니다.");

            SolidSnapshot snapshot = solids[0];
            XYZ normal = target.Basis.Normal.Normalize();
            GetProjectionRange(snapshot.WorldEdgePoints, normal, out double minimum, out double maximum);
            double measuredDepth = maximum - minimum;
            double depthTolerance = Math.Max(document.Application.VertexTolerance * 20.0,
                thickness * MaximumDepthRelativeError);
            if (Math.Abs(measuredDepth - thickness) > depthTolerance)
            {
                throw new InvalidOperationException(
                    $"패널 형상 깊이({FormatMillimeters(measuredDepth)})와 시스템 유형 두께({FormatMillimeters(thickness)})가 일치하지 않습니다. " +
                    "비정형·다중 레이어 시스템 패널은 자동 대체하지 않습니다.");
            }

            double faceCoordinate = target.Basis.Origin.DotProduct(normal);
            double extremeError = Math.Min(Math.Abs(faceCoordinate - minimum), Math.Abs(faceCoordinate - maximum));
            if (extremeError > depthTolerance)
                throw new InvalidOperationException("선택 면이 시스템 패널 솔리드의 바깥쪽 평면이 아닙니다.");

            double faceArea = NetArea(target.FacePaths);
            if (faceArea <= document.Application.ShortCurveTolerance * document.Application.ShortCurveTolerance)
                throw new InvalidOperationException("선택한 시스템 패널 면의 면적이 너무 작습니다.");
            double expectedVolume = faceArea * thickness;
            if (RelativeError(snapshot.Volume, expectedVolume) > MaximumVolumeRelativeError)
            {
                throw new InvalidOperationException(
                    "선택 패널이 면 외곽을 일정한 두께로 밀어 만든 단순 프리즘이 아닙니다. " +
                    "원본 형상을 잃지 않도록 자동 대체를 중단했습니다.");
            }

            foreach (Face face in snapshot.Solid.Faces)
            {
                ElementId materialId = face.MaterialElementId;
                if (materialId != ElementId.InvalidElementId && materialId != target.MaterialElementId)
                    throw new InvalidOperationException("패널 솔리드에 선택 면과 다른 재료가 함께 사용되어 자동 대체할 수 없습니다.");
            }

            double centroidSide = (snapshot.WorldCentroid - target.Basis.Origin).DotProduct(normal);
            if (Math.Abs(centroidSide) <= Math.Max(document.Application.VertexTolerance * 10.0, thickness * 0.1))
                throw new InvalidOperationException("선택 면에서 패널 안쪽 방향을 안정적으로 판단하지 못했습니다.");

            return new PrismInfo
            {
                Thickness = thickness,
                Inward = centroidSide > 0.0 ? normal : -normal,
            };
        }

        private static Paths64 BuildRemainingPaths(Document document, Paths64 facePaths, Paths64 punchPaths)
        {
            Paths64 face = ClonePaths(facePaths);
            Paths64 cutters = PatternClipper.Union(ClonePaths(punchPaths));
            Paths64 remaining = PatternClipper.Difference(face, cutters);
            if (remaining.Count == 0)
                throw new InvalidOperationException("타공 후 남는 패널 형상이 없습니다.");
            if (CountMaterialIslands(remaining) != 1)
                throw new InvalidOperationException("타공 후 패널 재료가 둘 이상의 섬으로 분리되어 안전하게 대체할 수 없습니다.");

            double originalArea = NetArea(face);
            double remainingArea = NetArea(remaining);
            double areaTolerance = Math.Max(document.Application.ShortCurveTolerance *
                                            document.Application.ShortCurveTolerance, originalArea * 1e-8);
            if (remainingArea <= areaTolerance)
                throw new InvalidOperationException("타공 후 남는 패널 면적이 너무 작습니다.");
            if (originalArea - remainingArea <= areaTolerance)
                throw new InvalidOperationException("선택한 타공 경계가 패널 면과 실제로 겹치지 않습니다.");

            ValidateProfileEdges(document, remaining);
            return remaining;
        }

        private static void BuildReplacementFamily(Document projectDocument, Document familyDocument, Panel panel,
            PatternPunchTarget target, Paths64 remainingPaths, PrismInfo prism)
        {
            Transform toFamily = panel.GetTransform().Inverse;
            XYZ backOriginModel = target.Basis.Origin + prism.Inward * prism.Thickness;
            XYZ extrusionNormalFamily = toFamily.OfVector(-prism.Inward).Normalize();
            XYZ backOriginFamily = toFamily.OfPoint(backOriginModel);

            using var transaction = new Transaction(familyDocument, "시스템 커튼패널 대체 형상 생성");
            try
            {
                transaction.Start();
                FamilyManager manager = familyDocument.FamilyManager;
                if (manager.CurrentType == null) manager.NewType("타공 패널");

                Plane plane = Plane.CreateByNormalAndOrigin(extrusionNormalFamily, backOriginFamily);
                SketchPlane sketchPlane = SketchPlane.Create(familyDocument, plane);
                CurveArrArray profile = BuildSolidProfile(projectDocument, remainingPaths, target.Basis,
                    toFamily, prism.Inward, prism.Thickness);
                Extrusion extrusion = familyDocument.FamilyCreate.NewExtrusion(true, profile, sketchPlane,
                    prism.Thickness);

                FamilyParameter materialParameter = manager.AddParameter(MaterialParameterName,
                    GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
                Parameter? extrusionMaterial = extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (extrusionMaterial == null)
                    throw new InvalidOperationException("새 패널 솔리드의 재료 매개변수를 만들지 못했습니다.");
                manager.AssociateElementParameterToFamilyParameter(extrusionMaterial, materialParameter);
                transaction.Commit();
            }
            catch
            {
                try { if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack(); } catch { }
                throw;
            }
        }

        private static CurveArrArray BuildSolidProfile(Document projectDocument, Paths64 paths,
            PatternFaceBasis basis, Transform toFamily, XYZ inward, double thickness)
        {
            var profile = new CurveArrArray();
            double shortTolerance = projectDocument.Application.ShortCurveTolerance;
            foreach (Path64 path in paths)
            {
                List<PatternPoint> points = PatternClipper.FromPath(path);
                if (points.Count >= 2 && (points[points.Count - 1] - points[0]).Length <= shortTolerance)
                    points.RemoveAt(points.Count - 1);
                if (points.Count < 3)
                    throw new InvalidOperationException("남는 패널 프로파일에 세 점 미만의 경계가 있습니다.");

                var familyPoints = new List<XYZ>(points.Count);
                foreach (PatternPoint point in points)
                {
                    XYZ backModelPoint = basis.ToWorld(point) + inward * thickness;
                    familyPoints.Add(toFamily.OfPoint(backModelPoint));
                }

                var loop = new CurveArray();
                for (int i = 0; i < familyPoints.Count; i++)
                {
                    XYZ first = familyPoints[i];
                    XYZ second = familyPoints[(i + 1) % familyPoints.Count];
                    if (first.DistanceTo(second) <= shortTolerance)
                        throw new InvalidOperationException("Revit 최소 길이보다 짧은 대체 패널 경계선이 있습니다.");
                    loop.Append(Line.CreateBound(first, second));
                }
                profile.Append(loop);
            }
            return profile;
        }

        private static PunchExecutionResult LoadAssignAndValidate(Document document, Panel panel,
            FamilySymbol originalSymbol, PatternPunchPlan plan, PatternPunchTarget target, Paths64 punchPaths,
            Paths64 expectedRemaining, double thickness, string familyPath, bool probe)
        {
            using var group = new TransactionGroup(document,
                probe ? "시스템 커튼패널 타공 사전 검증" : "Sunny 시스템 커튼패널 타공");
            try
            {
                if (group.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("시스템 패널 대체 작업 그룹을 시작하지 못했습니다.");

                FamilySymbol replacement;
                using (var transaction = new Transaction(document, "타공 시스템 패널 대체 유형 배치"))
                {
                    try
                    {
                        transaction.Start();
                        if (!document.LoadFamily(familyPath, new SunnyFamilyLoadOptions(), out Family? loadedFamily) ||
                            loadedFamily == null)
                            throw new InvalidOperationException("생성한 시스템 패널 대체 패밀리를 프로젝트에 불러오지 못했습니다.");

                        replacement = loadedFamily.GetFamilySymbolIds()
                            .Select(id => document.GetElement(id) as FamilySymbol)
                            .FirstOrDefault(symbol => symbol != null)
                            ?? throw new InvalidOperationException("대체 패밀리에서 사용할 패널 유형을 찾지 못했습니다.");
                        if (replacement.Category == null ||
                            PatternPunchExecutor.GetElementIdValue(replacement.Category.Id) !=
                            (long)BuiltInCategory.OST_CurtainWallPanels)
                            throw new InvalidOperationException("생성한 패밀리가 커튼월 패널 유형이 아닙니다.");

                        if (!replacement.IsActive) replacement.Activate();
                        Parameter? material = replacement.LookupParameter(MaterialParameterName);
                        if (material == null || material.StorageType != StorageType.ElementId || material.IsReadOnly)
                            throw new InvalidOperationException("대체 패널 유형의 재료 매개변수를 설정할 수 없습니다.");
                        material.Set(target.MaterialElementId);

                        try
                        {
                            panel.Symbol = replacement;
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                "커튼패널 유형을 대체 패밀리로 바꾸지 못했습니다. 패널 또는 커튼 그리드의 잠금을 확인해 주세요. " +
                                ex.Message, ex);
                        }

                        document.Regenerate();
                        ValidateReplacement(document, panel, replacement, target, expectedRemaining, thickness);
                        transaction.Commit();
                    }
                    catch
                    {
                        try { if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack(); } catch { }
                        throw;
                    }
                }

                var result = new PunchExecutionResult
                {
                    Succeeded = true,
                    CutCount = punchPaths.Count,
                    OriginalPanelTypeId = PatternPunchExecutor.GetElementIdValue(originalSymbol.Id),
                };

                if (probe)
                {
                    group.RollBack();
                }
                else
                {
                    // 안전 복원 기록을 group.Assimilate() 이전, 같은 그룹 안에서 커밋해 되돌리기 한 번으로
                    // 기록과 실제 패널 교체가 항상 함께 취소/유지되도록 한다.
                    using (var recordTransaction = new Transaction(document, "패턴 타공 복원 기록"))
                    {
                        recordTransaction.Start();
                        try { PatternPunchRecordStore.AppendEntity(panel, plan, target, punchPaths, result); }
                        catch { /* 기록 저장 실패가 이미 완료된 실제 타공 결과를 되돌리지는 않는다. */ }
                        recordTransaction.Commit();
                    }
                    group.Assimilate();
                }

                return result;
            }
            catch
            {
                try { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); } catch { }
                throw;
            }
        }

        private static void ValidateReplacement(Document document, Panel panel, FamilySymbol replacement,
            PatternPunchTarget target, Paths64 expectedRemaining, double thickness)
        {
            if (panel.Symbol == null || panel.Symbol.Id != replacement.Id)
                throw new InvalidOperationException("패널에 대체 유형이 실제로 지정되지 않았습니다.");
            Parameter? materialParameter = replacement.LookupParameter(MaterialParameterName);
            if (materialParameter == null || materialParameter.AsElementId() != target.MaterialElementId)
                throw new InvalidOperationException("대체 패널 유형에 원래 표면 재료가 지정되지 않았습니다.");

            List<SolidSnapshot> solids = CollectSolidSnapshots(document, panel);
            if (solids.Count != 1)
                throw new InvalidOperationException("대체 후 패널 형상이 하나의 유효한 솔리드로 만들어지지 않았습니다.");
            SolidSnapshot snapshot = solids[0];
            XYZ normal = target.Basis.Normal.Normalize();
            GetProjectionRange(snapshot.WorldEdgePoints, normal, out double minimum, out double maximum);
            double depthTolerance = Math.Max(document.Application.VertexTolerance * 20.0,
                thickness * MaximumDepthRelativeError);
            if (Math.Abs((maximum - minimum) - thickness) > depthTolerance)
                throw new InvalidOperationException("대체 후 패널 두께가 원래 시스템 패널 두께와 다릅니다.");

            double expectedArea = NetArea(expectedRemaining);
            double expectedVolume = expectedArea * thickness;
            if (RelativeError(snapshot.Volume, expectedVolume) > MaximumVolumeRelativeError)
                throw new InvalidOperationException("대체 후 패널 솔리드 부피가 예상 타공 형상과 일치하지 않습니다.");

            Paths64 frontPaths = new Paths64();
            bool foundTargetMaterial = false;
            double planeTolerance = Math.Max(document.Application.VertexTolerance * 20.0, 1e-5);
            foreach (Face face in snapshot.Solid.Faces)
            {
                if (face is not PlanarFace planarFace) continue;
                XYZ faceNormal = snapshot.Transform.OfVector(planarFace.FaceNormal).Normalize();
                if (Math.Abs(faceNormal.DotProduct(normal)) < Math.Cos(Math.PI / 1800.0)) continue;
                XYZ faceOrigin = snapshot.Transform.OfPoint(planarFace.Origin);
                if (Math.Abs((faceOrigin - target.Basis.Origin).DotProduct(normal)) > planeTolerance) continue;

                Paths64 facePaths = BuildFacePaths(planarFace, snapshot.Transform, target.Basis);
                foreach (Path64 path in facePaths) frontPaths.Add(path);
                if (planarFace.MaterialElementId == target.MaterialElementId) foundTargetMaterial = true;
                else if (planarFace.MaterialElementId != ElementId.InvalidElementId)
                    throw new InvalidOperationException("대체 패널 전면에 원래와 다른 재료가 지정되었습니다.");
            }

            if (frontPaths.Count == 0)
                throw new InvalidOperationException("대체 패널에서 원래 선택 면 위치의 전면 형상을 찾지 못했습니다.");
            frontPaths = PatternClipper.Union(frontPaths);
            double outlineError = NetArea(PatternClipper.Difference(ClonePaths(expectedRemaining), ClonePaths(frontPaths))) +
                                  NetArea(PatternClipper.Difference(ClonePaths(frontPaths), ClonePaths(expectedRemaining)));
            double allowedOutlineError = Math.Max(expectedArea * MaximumOutlineRelativeError,
                document.Application.ShortCurveTolerance * document.Application.ShortCurveTolerance * 4.0);
            if (outlineError > allowedOutlineError)
                throw new InvalidOperationException("대체 후 패널 외곽 또는 타공 경계가 예상 형상과 일치하지 않습니다.");
            if (!foundTargetMaterial)
                throw new InvalidOperationException("대체 패널 전면에서 원래 표면 재료를 확인하지 못했습니다.");
        }

        private static Paths64 BuildFacePaths(PlanarFace face, Transform transform, PatternFaceBasis basis)
        {
            var paths = new Paths64();
            foreach (CurveLoop loop in face.GetEdgesAsCurveLoops())
            {
                var points = new List<PatternPoint>();
                foreach (Curve curve in loop)
                {
                    IList<XYZ> tessellated = curve.Tessellate();
                    for (int i = 0; i < tessellated.Count - 1; i++)
                        points.Add(basis.ToLocal(transform.OfPoint(tessellated[i])));
                }
                if (points.Count >= 3) paths.Add(PatternClipper.ToPath(points));
            }
            return paths.Count == 0 ? paths : PatternClipper.Union(paths);
        }

        private static Document OpenCurtainWallPanelFamilyDocument(
            Autodesk.Revit.ApplicationServices.Application application, out string templatePath)
        {
            List<string> candidates = FindTemplateCandidates(application).ToList();
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    "현재 Revit 버전의 '미터법 커튼월 패널' 패밀리 템플릿을 찾지 못했습니다. " +
                    "Autodesk Revit 콘텐츠의 패밀리 템플릿을 설치해 주세요.");
            }

            var failures = new List<string>();
            foreach (string candidate in candidates)
            {
                Document? familyDocument = null;
                try
                {
                    familyDocument = application.NewFamilyDocument(candidate);
                    if (IsCurtainPanelFamilyDocument(familyDocument))
                    {
                        templatePath = candidate;
                        Document result = familyDocument;
                        familyDocument = null;
                        return result;
                    }
                    failures.Add(Path.GetFileName(candidate) + ": 커튼월 패널 범주가 아님");
                }
                catch (Exception ex)
                {
                    failures.Add(Path.GetFileName(candidate) + ": " + ex.GetBaseException().Message);
                }
                finally
                {
                    if (familyDocument != null)
                    {
                        try { familyDocument.Close(false); } catch { }
                    }
                }
            }

            throw new InvalidOperationException(
                "찾은 패밀리 템플릿 중 정확한 커튼월 패널 템플릿을 열지 못했습니다. " +
                string.Join(" / ", failures.Take(4)));
        }

        private static IEnumerable<string> FindTemplateCandidates(
            Autodesk.Revit.ApplicationServices.Application application)
        {
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(application.FamilyTemplatePath))
            {
                foreach (string value in application.FamilyTemplatePath.Split(new[] { ';' },
                             StringSplitOptions.RemoveEmptyEntries))
                    AddDirectory(roots, value.Trim());
            }

            string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            AddDirectory(roots, Path.Combine(commonData, "Autodesk", "RVT " + application.VersionNumber,
                "Family Templates"));

            var files = new List<string>();
            foreach (string root in roots)
            {
                try
                {
                    files.AddRange(Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories)
                        .Where(IsCurtainWallPanelTemplateName));
                }
                catch
                {
                    // 접근할 수 없는 콘텐츠 하위 폴더는 다른 기본 경로 후보를 계속 확인한다.
                }
            }

            return files.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => TemplateNameRank(Path.GetFileName(path)))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsCurtainWallPanelTemplateName(string path)
        {
            string name = Path.GetFileName(path);
            if (string.Equals(name, "미터법 커튼월 패널.rft", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Metric Curtain Wall Panel.rft", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Curtain Wall Panel.rft", StringComparison.OrdinalIgnoreCase))
                return true;

            string lower = name.ToLowerInvariant();
            bool englishCandidate = lower.Contains("curtain") && lower.Contains("panel") &&
                                    !lower.Contains("pattern") && !lower.Contains("door") &&
                                    !lower.Contains("window");
            bool koreanCandidate = name.Contains("커튼월") && name.Contains("패널") &&
                                   !name.Contains("패턴") && !name.Contains("문 -") &&
                                   !name.Contains("창 -");
            return englishCandidate || koreanCandidate;
        }

        private static int TemplateNameRank(string name)
        {
            if (string.Equals(name, "미터법 커튼월 패널.rft", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(name, "Metric Curtain Wall Panel.rft", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(name, "Curtain Wall Panel.rft", StringComparison.OrdinalIgnoreCase)) return 2;
            return 10;
        }

        private static void ValidateCurtainPanelTemplate(Document familyDocument, string templatePath)
        {
            if (!IsCurtainPanelFamilyDocument(familyDocument))
                throw new InvalidOperationException(
                    $"'{Path.GetFileName(templatePath)}'은(는) 커튼월 패널 패밀리 템플릿이 아닙니다.");
        }

        private static bool IsCurtainPanelFamilyDocument(Document familyDocument)
        {
            if (!familyDocument.IsFamilyDocument) return false;
            Category? category = familyDocument.OwnerFamily?.FamilyCategory;
            return category != null && PatternPunchExecutor.GetElementIdValue(category.Id) ==
                (long)BuiltInCategory.OST_CurtainWallPanels;
        }

        private static List<SolidSnapshot> CollectSolidSnapshots(Document document, Element element)
        {
            var options = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine,
            };
            GeometryElement? geometry = element.get_Geometry(options);
            if (geometry == null) return new List<SolidSnapshot>();

            var result = new List<SolidSnapshot>();
            double minimumVolume = Math.Pow(Math.Max(document.Application.ShortCurveTolerance, 1e-6), 3.0);
            CollectSolidSnapshots(geometry, Transform.Identity, 0, minimumVolume, result);
            return result;
        }

        private static void CollectSolidSnapshots(GeometryElement geometry, Transform accumulated, int depth,
            double minimumVolume, List<SolidSnapshot> result)
        {
            if (depth > 16) return;
            foreach (GeometryObject geometryObject in geometry)
            {
                if (geometryObject is Solid solid)
                {
                    double volume;
                    try { volume = solid.Volume; }
                    catch { continue; }
                    if (solid.Faces.Size == 0 || volume <= minimumVolume) continue;

                    var edgePoints = new List<XYZ>();
                    foreach (Edge edge in solid.Edges)
                    {
                        IList<XYZ> tessellated;
                        try { tessellated = edge.Tessellate(); }
                        catch { continue; }
                        foreach (XYZ point in tessellated) edgePoints.Add(accumulated.OfPoint(point));
                    }
                    if (edgePoints.Count == 0) continue;

                    XYZ centroid;
                    try { centroid = accumulated.OfPoint(solid.ComputeCentroid()); }
                    catch { continue; }
                    result.Add(new SolidSnapshot
                    {
                        Solid = solid,
                        Transform = accumulated,
                        Volume = volume,
                        WorldCentroid = centroid,
                        WorldEdgePoints = edgePoints,
                    });
                }
                else if (geometryObject is GeometryInstance instance)
                {
                    GeometryElement nested;
                    try { nested = instance.GetSymbolGeometry(); }
                    catch { continue; }
                    CollectSolidSnapshots(nested, accumulated.Multiply(instance.Transform), depth + 1,
                        minimumVolume, result);
                }
            }
        }

        private static void GetProjectionRange(IEnumerable<XYZ> points, XYZ direction,
            out double minimum, out double maximum)
        {
            minimum = double.PositiveInfinity;
            maximum = double.NegativeInfinity;
            foreach (XYZ point in points)
            {
                double value = point.DotProduct(direction);
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
            if (double.IsInfinity(minimum) || double.IsInfinity(maximum))
                throw new InvalidOperationException("패널 솔리드의 깊이 범위를 계산하지 못했습니다.");
        }

        private static void ValidateProfileEdges(Document document, Paths64 paths)
        {
            foreach (Path64 path in paths)
            {
                List<PatternPoint> points = PatternClipper.FromPath(path);
                if (points.Count < 3)
                    throw new InvalidOperationException("남는 패널 프로파일에 세 점 미만의 경계가 있습니다.");
                for (int i = 0; i < points.Count; i++)
                {
                    if ((points[(i + 1) % points.Count] - points[i]).Length <=
                        document.Application.ShortCurveTolerance)
                        throw new InvalidOperationException("Revit 최소 길이보다 짧은 남는 패널 경계선이 있습니다.");
                }
            }
        }

        private static int CountMaterialIslands(Paths64 paths)
        {
            int islands = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                if (paths[i].Count < 3) continue;
                if (ContainmentDepth(paths, i) % 2 == 0) islands++;
            }
            return islands;
        }

        private static double NetArea(Paths64 paths)
        {
            double area = 0.0;
            for (int i = 0; i < paths.Count; i++)
            {
                if (paths[i].Count < 3) continue;
                double absoluteArea = Math.Abs(SignedArea(PatternClipper.FromPath(paths[i])));
                area += ContainmentDepth(paths, i) % 2 == 0 ? absoluteArea : -absoluteArea;
            }
            return Math.Max(0.0, area);
        }

        private static int ContainmentDepth(Paths64 paths, int pathIndex)
        {
            int depth = 0;
            Point64 probe = paths[pathIndex][0];
            for (int other = 0; other < paths.Count; other++)
            {
                if (other == pathIndex || paths[other].Count < 3) continue;
                PointInPolygonResult result = Clipper.PointInPolygon(probe, paths[other]);
                if (result == PointInPolygonResult.IsInside) depth++;
            }
            return depth;
        }

        private static double SignedArea(IReadOnlyList<PatternPoint> points)
        {
            double twiceArea = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                PatternPoint first = points[i];
                PatternPoint second = points[(i + 1) % points.Count];
                twiceArea += first.X * second.Y - second.X * first.Y;
            }
            return twiceArea * 0.5;
        }

        private static double RelativeError(double actual, double expected) =>
            Math.Abs(actual - expected) / Math.Max(Math.Max(Math.Abs(actual), Math.Abs(expected)), 1e-12);

        private static Paths64 ClonePaths(Paths64 paths) =>
            new Paths64(paths.Select(path => new Path64(path)));

        private static void AddDirectory(ICollection<string> roots, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { return; }
            if (!roots.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) roots.Add(fullPath);
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? "CurtainPanel" : name;
        }

        private static string FormatMillimeters(double feet) => (feet * 304.8).ToString("0.###") + " mm";

        private static void DeleteTemporaryFamilyFiles(string? familyPath)
        {
            if (string.IsNullOrWhiteSpace(familyPath)) return;
            try
            {
                string? folder = Path.GetDirectoryName(familyPath);
                string stem = Path.GetFileNameWithoutExtension(familyPath);
                if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(stem) || !Directory.Exists(folder))
                    return;
                foreach (string file in Directory.EnumerateFiles(folder, stem + "*.rfa", SearchOption.TopDirectoryOnly))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch
            {
                // 프로젝트에는 이미 패밀리가 로드되어 있으므로 임시 파일 정리 실패는 타공 실패로 취급하지 않는다.
            }
        }

        private static PunchExecutionResult Failure(string message) => new PunchExecutionResult { Message = message };

        private sealed class PrismInfo
        {
            internal double Thickness { get; set; }
            internal XYZ Inward { get; set; } = XYZ.BasisZ;
        }

        private sealed class SolidSnapshot
        {
            internal Solid Solid { get; set; } = null!;
            internal Transform Transform { get; set; } = Transform.Identity;
            internal double Volume { get; set; }
            internal XYZ WorldCentroid { get; set; } = XYZ.Zero;
            internal List<XYZ> WorldEdgePoints { get; set; } = new List<XYZ>();
        }

        private sealed class SunnyFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
                out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }
    }
}
