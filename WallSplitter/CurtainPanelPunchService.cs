using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Clipper2Lib;

namespace WallSplitter
{
    internal static class CurtainPanelPunchService
    {
        internal static PunchExecutionResult Execute(Document document, Panel panel, PatternPunchPlan plan,
            PatternPunchTarget target, Paths64 punchPaths, bool probe)
        {
            FamilySymbol? originalSymbol = panel.Symbol;
            Family? sourceFamily = originalSymbol?.Family;
            if (originalSymbol == null || sourceFamily == null)
                return Failure("패밀리 기반 커튼월 패널이 아닙니다.");
            if (punchPaths.Count == 0) return Failure("유효한 타공 경계가 없습니다.");
            if (sourceFamily.IsInPlace)
                return Failure("인플레이스 커튼패널은 안전하게 복제할 수 없습니다. 로드형 또는 시스템 커튼패널을 사용해 주세요.");

            // 패널은 스케치 편집 호스트가 아니므로 원본 패밀리를 건드리지 않고, 선택 패널 전용 복제본을 만든다.
            try
            {
                ValidatePaths(document, target, punchPaths);
                if (!sourceFamily.IsUserCreated)
                    return SystemCurtainPanelPunchService.Execute(document, panel, originalSymbol, plan, target, punchPaths, probe);
                if (!sourceFamily.IsEditable)
                    return Failure("이 로드형 커튼패널 패밀리를 현재 편집할 수 없습니다. 같은 패밀리가 패밀리 편집기에서 열려 있다면 닫은 뒤 다시 시도해 주세요.");
                if (probe)
                    return ProbeExtrusionGeometry(document, panel, originalSymbol, target, punchPaths);
                return CreatePunchedFamilyAndAssign(document, panel, originalSymbol, plan, target, punchPaths);
            }
            catch (Exception ex)
            {
                return Failure(ex.Message);
            }
        }

        // 실제 프로젝트 문서는 건드리지 않고, 패밀리 편집기에서 동일한 돌출(Extrusion) 생성을 시도한 뒤
        // 저장하지 않고 닫아 버린다. 자기교차 등 형상 오류를 저장/로드 이전에 값싸게 미리 잡아낸다.
        private static PunchExecutionResult ProbeExtrusionGeometry(Document projectDocument, Panel panel,
            FamilySymbol originalSymbol, PatternPunchTarget target, Paths64 punchPaths)
        {
            Family sourceFamily = originalSymbol.Family;
            Document? familyDocument = null;
            try
            {
                familyDocument = projectDocument.EditFamily(sourceFamily);
                Transform toFamily = panel.GetTransform().Inverse;
                double backOffset = GetSafeBackOffset(panel, target.Basis.Normal);
                XYZ modelNormal = target.Basis.Normal.Normalize();
                XYZ familyNormal = toFamily.OfVector(modelNormal).Normalize();

                using (var transaction = new Transaction(familyDocument, "패턴 타공 사전 검증"))
                {
                    transaction.Start();
                    foreach (Path64 path in punchPaths)
                    {
                        CurveArrArray profile = BuildFamilyProfile(path, target.Basis, toFamily,
                            modelNormal, backOffset, projectDocument.Application.ShortCurveTolerance,
                            out XYZ familyPlaneOrigin);
                        Plane plane = Plane.CreateByNormalAndOrigin(familyNormal, familyPlaneOrigin);
                        SketchPlane sketchPlane = SketchPlane.Create(familyDocument, plane);
                        familyDocument.FamilyCreate.NewExtrusion(false, profile, sketchPlane, backOffset * 2.0);
                    }
                    transaction.RollBack();
                }
                return new PunchExecutionResult { Succeeded = true, CutCount = punchPaths.Count };
            }
            catch (Exception ex)
            {
                return Failure("사전 검증에 실패했습니다. " + ex.GetBaseException().Message);
            }
            finally
            {
                if (familyDocument != null)
                {
                    try { familyDocument.Close(false); } catch { }
                }
            }
        }

        private static PunchExecutionResult CreatePunchedFamilyAndAssign(Document projectDocument, Panel panel,
            FamilySymbol originalSymbol, PatternPunchPlan plan, PatternPunchTarget target, Paths64 punchPaths)
        {
            Family sourceFamily = originalSymbol.Family;
            Document? familyDocument = null;
            string? familyPath = null;
            try
            {
                familyDocument = projectDocument.EditFamily(sourceFamily);
                Transform toFamily = panel.GetTransform().Inverse;
                double backOffset = GetSafeBackOffset(panel, target.Basis.Normal);
                XYZ modelNormal = target.Basis.Normal.Normalize();
                XYZ familyNormal = toFamily.OfVector(modelNormal).Normalize();

                using (var transaction = new Transaction(familyDocument, "커튼패널 패턴 타공"))
                {
                    transaction.Start();
                    foreach (Path64 path in punchPaths)
                    {
                        CurveArrArray profile = BuildFamilyProfile(path, target.Basis, toFamily,
                            modelNormal, backOffset, projectDocument.Application.ShortCurveTolerance,
                            out XYZ familyPlaneOrigin);
                        Plane plane = Plane.CreateByNormalAndOrigin(familyNormal, familyPlaneOrigin);
                        SketchPlane sketchPlane = SketchPlane.Create(familyDocument, plane);
                        familyDocument.FamilyCreate.NewExtrusion(false, profile, sketchPlane, backOffset * 2.0);
                    }
                    transaction.Commit();
                }

                string folder = Path.Combine(Path.GetTempPath(), "SunnyTools", "PatternPanels");
                Directory.CreateDirectory(folder);
                string cleanName = MakeSafeFileName(sourceFamily.Name);
                familyPath = Path.Combine(folder, $"{cleanName}_Sunny타공_{DateTime.Now:yyyyMMdd_HHmmss_fff}.rfa");
                var saveOptions = new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 };
                familyDocument.SaveAs(familyPath, saveOptions);
                familyDocument.Close(false);
                familyDocument = null;

                Family? loadedFamily;
                PunchExecutionResult result;
                using (var transaction = new Transaction(projectDocument, "타공 커튼패널 유형 배치"))
                {
                    transaction.Start();
                    if (!projectDocument.LoadFamily(familyPath, new SunnyFamilyLoadOptions(), out loadedFamily) || loadedFamily == null)
                        throw new InvalidOperationException("복제한 타공 패밀리를 프로젝트에 불러오지 못했습니다.");
                    FamilySymbol replacement = FindMatchingSymbol(projectDocument, loadedFamily, originalSymbol.Name);
                    if (!replacement.IsActive) replacement.Activate();
                    panel.Symbol = replacement;

                    result = new PunchExecutionResult
                    {
                        Succeeded = true,
                        CutCount = punchPaths.Count,
                        OriginalPanelTypeId = PatternPunchExecutor.GetElementIdValue(originalSymbol.Id),
                    };
                    // 안전 복원 기록을 같은 트랜잭션 안에서 함께 커밋해, 되돌리기 한 번으로 기록과
                    // 실제 패널 교체가 항상 같이 취소/유지되도록 한다.
                    TryAppendRecord(panel, plan, target, punchPaths, result);
                    transaction.Commit();
                }

                return result;
            }
            finally
            {
                if (familyDocument != null)
                {
                    try { familyDocument.Close(false); } catch { }
                }
                // 로드가 끝난 임시 파일은 프로젝트가 참조하지 않는다. 삭제 실패는 기능 실패로 취급하지 않는다.
                if (!string.IsNullOrWhiteSpace(familyPath))
                {
                    try { File.Delete(familyPath); } catch { }
                }
            }
        }

        private static void TryAppendRecord(Panel panel, PatternPunchPlan plan, PatternPunchTarget target,
            Paths64 punchPaths, PunchExecutionResult result)
        {
            try { PatternPunchRecordStore.AppendEntity(panel, plan, target, punchPaths, result); }
            catch { /* 기록 저장 실패가 이미 완료된 실제 타공 결과를 되돌리지는 않는다. */ }
        }

        private static CurveArrArray BuildFamilyProfile(Path64 path, PatternFaceBasis basis, Transform toFamily,
            XYZ modelNormal, double backOffset, double shortTolerance, out XYZ familyPlaneOrigin)
        {
            List<PatternPoint> points = PatternClipper.FromPath(path);
            if (points.Count < 3) throw new InvalidOperationException("커튼패널 타공 경계가 유효하지 않습니다.");
            var curveArray = new CurveArray();
            var shifted = new List<XYZ>();
            foreach (PatternPoint point in points)
            {
                XYZ modelPoint = basis.ToWorld(point) - modelNormal * backOffset;
                shifted.Add(toFamily.OfPoint(modelPoint));
            }
            familyPlaneOrigin = shifted[0];
            for (int i = 0; i < shifted.Count; i++)
            {
                XYZ a = shifted[i];
                XYZ b = shifted[(i + 1) % shifted.Count];
                if (a.DistanceTo(b) <= shortTolerance)
                    throw new InvalidOperationException("Revit 최소 길이보다 짧은 커튼패널 타공 선이 있습니다.");
                curveArray.Append(Line.CreateBound(a, b));
            }
            var profile = new CurveArrArray();
            profile.Append(curveArray);
            return profile;
        }

        private static void ValidatePaths(Document document, PatternPunchTarget target, Paths64 paths)
        {
            foreach (Path64 path in paths)
            {
                List<PatternPoint> points = PatternClipper.FromPath(path);
                if (points.Count < 3) throw new InvalidOperationException("세 점 미만의 타공 경계가 있습니다.");
                for (int i = 0; i < points.Count; i++)
                {
                    if ((points[(i + 1) % points.Count] - points[i]).Length <= document.Application.ShortCurveTolerance)
                        throw new InvalidOperationException("Revit 최소 길이보다 짧은 타공 선이 있습니다.");
                }
            }
        }

        private static double GetSafeBackOffset(Panel panel, XYZ faceNormal)
        {
            BoundingBoxXYZ? box = panel.get_BoundingBox(null);
            if (box == null) return 2.0;
            var values = new List<double>();
            for (int x = 0; x <= 1; x++)
            for (int y = 0; y <= 1; y++)
            for (int z = 0; z <= 1; z++)
            {
                XYZ point = new XYZ(
                    x == 0 ? box.Min.X : box.Max.X,
                    y == 0 ? box.Min.Y : box.Max.Y,
                    z == 0 ? box.Min.Z : box.Max.Z);
                values.Add(point.DotProduct(faceNormal));
            }
            double thickness = values.Max() - values.Min();
            return Math.Max(2.0, thickness + 1.0);
        }

        private static FamilySymbol FindMatchingSymbol(Document document, Family family, string originalTypeName)
        {
            var symbols = family.GetFamilySymbolIds()
                .Select(id => document.GetElement(id) as FamilySymbol)
                .Where(symbol => symbol != null)
                .Cast<FamilySymbol>()
                .ToList();
            return symbols.FirstOrDefault(symbol => string.Equals(symbol.Name, originalTypeName, StringComparison.Ordinal))
                ?? symbols.FirstOrDefault()
                ?? throw new InvalidOperationException("복제 패밀리에서 사용할 수 있는 패널 유형을 찾지 못했습니다.");
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? "CurtainPanel" : name;
        }

        private static PunchExecutionResult Failure(string message) => new PunchExecutionResult { Message = message };

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
