using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Clipper2Lib;

namespace WallSplitter
{
    [Transaction(TransactionMode.Manual)]
    public sealed class PatternPunchRestoreCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument? uiDocument = commandData.Application.ActiveUIDocument;
            if (uiDocument == null) return Result.Cancelled;
            Document document = uiDocument.Document;
            try
            {
                Element? host = FindPreselectedHost(document, uiDocument.Selection.GetElementIds());
                if (host == null)
                {
                    Reference reference = uiDocument.Selection.PickObject(ObjectType.Element,
                        new PatternPunchRestoreFilter(), "최근 패턴 타공을 복원할 벽·바닥·천장·커튼패널을 선택하세요.");
                    host = document.GetElement(reference);
                }
                if (host == null) return Result.Cancelled;

                PatternPunchRecord? record = PatternPunchRecordStore.Read(host).LastOrDefault();
                if (record == null)
                {
                    TaskDialog.Show("패턴 타공 복원", "선택한 요소에 Sunny Tools 패턴 타공 기록이 없습니다.");
                    return Result.Cancelled;
                }

                string description = $"패턴: {record.PatternName}\n생성: {record.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n\n가장 최근 타공 1회를 복원합니다.";
                var confirm = new TaskDialog("패턴 타공 복원")
                {
                    MainInstruction = "최근 패턴 타공을 복원할까요?",
                    MainContent = description,
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                };
                if (confirm.Show() != TaskDialogResult.Yes) return Result.Cancelled;

                string resultMessage = Restore(document, host, record);
                TaskDialog.Show("패턴 타공 복원", resultMessage);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("패턴 타공 복원", "복원하지 못했습니다.\n\n" + ex.Message);
                return Result.Failed;
            }
        }

        private static string Restore(Document document, Element host, PatternPunchRecord record)
        {
            if (host is Panel panel && record.OriginalPanelTypeId >= 0)
            {
                FamilySymbol? originalSymbol = document.GetElement(CreateElementId(record.OriginalPanelTypeId)) as FamilySymbol;
                if (originalSymbol == null) throw new InvalidOperationException("원래 커튼패널 유형을 프로젝트에서 찾을 수 없습니다.");
                using var transaction = new Transaction(document, "커튼패널 타공 복원");
                transaction.Start();
                panel.Symbol = originalSymbol;
                PatternPunchRecordStore.RemoveLast(host);
                transaction.Commit();
                return "커튼패널을 타공 전 유형으로 복원했습니다.";
            }

            if (record.CreatedElementIds.Count > 0)
            {
                using var transaction = new Transaction(document, "패턴 개구부 복원");
                transaction.Start();
                int deleted = 0;
                foreach (long value in record.CreatedElementIds)
                {
                    ElementId id = CreateElementId(value);
                    if (document.GetElement(id) == null) continue;
                    document.Delete(id);
                    deleted++;
                }
                PatternPunchRecordStore.RemoveLast(host);
                transaction.Commit();
                return $"패턴 타공 개구부 {deleted:N0}개를 복원했습니다.";
            }

            if (string.IsNullOrWhiteSpace(record.BeforeProfileJson))
                throw new InvalidOperationException("이 기록에는 복원할 원본 프로파일이 없습니다.");
            ElementId sketchId = PatternPunchExecutor.GetSketchId(host);
            Sketch sketch = document.GetElement(sketchId) as Sketch
                ?? throw new InvalidOperationException("현재 호스트의 프로파일 스케치를 찾을 수 없습니다.");
            Plane plane = sketch.SketchPlane.GetPlane();
            var basis = new PatternFaceBasis
            {
                Origin = plane.Origin,
                XAxis = plane.XVec.Normalize(),
                YAxis = plane.YVec.Normalize(),
                Normal = plane.Normal.Normalize(),
            };
            Paths64 current = PatternPunchExecutor.BuildSketchPaths(sketch, basis);
            if (!string.IsNullOrWhiteSpace(record.AfterProfileHash) &&
                !string.Equals(PatternPunchRecordStore.HashPaths(current), record.AfterProfileHash, StringComparison.Ordinal))
                throw new InvalidOperationException("타공 뒤 프로파일이 다른 작업으로 변경되었습니다. 안전을 위해 자동 복원을 중단했습니다.");
            Paths64 original = PatternPunchRecordStore.DeserializePaths(record.BeforeProfileJson);
            if (original.Count == 0) throw new InvalidOperationException("저장된 원본 프로파일이 비어 있습니다.");

            using var group = new TransactionGroup(document, "패턴 타공 복원");
            group.Start();
            using var scope = new SketchEditScope(document, "패턴 타공 복원");
            try
            {
                scope.Start(sketchId);
                using (var transaction = new Transaction(document, "원본 프로파일 복원"))
                {
                    transaction.Start();
                    foreach (ElementId id in sketch.GetAllElements()) document.Delete(id);
                    foreach (Path64 path in original)
                        PatternPunchExecutor.CreatePathCurves(document, sketch.SketchPlane, basis, path);
                    transaction.Commit();
                }
                scope.Commit(new PatternPunchFailuresPreprocessor());
                using (var recordTransaction = new Transaction(document, "타공 복원 기록 갱신"))
                {
                    recordTransaction.Start();
                    PatternPunchRecordStore.RemoveLast(host);
                    recordTransaction.Commit();
                }
                group.Assimilate();
                return "호스트 프로파일을 타공 전 상태로 복원했습니다.";
            }
            catch
            {
                try { if (scope.IsActive) scope.Cancel(); } catch { }
                if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                throw;
            }
        }

        private static Element? FindPreselectedHost(Document document, ICollection<ElementId> ids)
        {
            if (ids.Count != 1) return null;
            Element? element = document.GetElement(ids.First());
            return element != null && PatternPunchRecordStore.Read(element).Count > 0 ? element : null;
        }

        private static ElementId CreateElementId(long value)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId(checked((int)value));
#endif
        }

        private sealed class PatternPunchRestoreFilter : ISelectionFilter
        {
            public bool AllowElement(Element element) =>
                element is Wall || element is Floor || element is Ceiling || element is Panel;

            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
