using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    [Transaction(TransactionMode.Manual)]
    public class MaterialAssignCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            List<ElementId> preSelected = uiDoc.Selection.GetElementIds().ToList();

            MaterialAssignWindow window = new MaterialAssignWindow(doc, preSelected);
            new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
            bool? dialogResult = window.ShowDialog();
            if (dialogResult != true)
                return Result.Cancelled;

            // 세 탭("재료 지정"/"재료 삭제"/"클래스/설명 변경")은 한 세션에 하나만 결과를 낼 수 있다 -
            // 어느 쪽 버튼을 눌렀는지에 따라 셋 중 하나만 채워진 채로 창이 닫힌다
            // (MaterialAssignWindow.DeleteButton_Click/FinalApplyButton_Click/IdFinalApplyButton_Click 참고).
            if (window.DeleteResult != null && window.DeleteResult.Count > 0)
                return ExecuteDelete(doc, window.DeleteResult);

            if (window.Result != null && window.Result.Count > 0)
                return ExecuteAssign(doc, window.Result);

            if (window.IdentityResult != null && window.IdentityResult.Count > 0)
                return ExecuteIdentityEdit(doc, window.IdentityResult);

            return Result.Cancelled;
        }

        private static Result ExecuteAssign(Document doc, List<(ElementId TypeId, ElementId NewMaterialId)> assignments)
        {
            var failed = new List<string>();
            TransactionStatus status;

            using (Transaction tx = new Transaction(doc, "재료 일괄 지정"))
            {
                tx.Start();

                foreach ((ElementId typeId, ElementId newMaterialId) in assignments)
                {
                    Element? el = doc.GetElement(typeId);
                    if (el is not ElementType type) continue;

                    try
                    {
                        bool applied = MaterialSlotFinder.Apply(type, newMaterialId);
                        if (!applied)
                        {
                            failed.Add($"{type.Name} (재료를 지정할 위치를 찾지 못함)");
                            continue;
                        }

                        // Apply()가 예외 없이 반환했다고 해서 실제로 반영됐다고 단정하지 않는다 - 커밋 전에
                        // 같은 트랜잭션 안에서 다시 읽어 실제 값이 지정하려던 재료와 일치하는지 확인하고,
                        // 다르면 "조용한 미반영"으로 실패 목록에 남긴다("정확하게 반영됐으면 좋겠다"는
                        // 라이브 피드백으로 추가 - 반영 여부를 눈으로 다시 확인하지 않아도 알 수 있어야 한다).
                        MaterialSlot? verify = MaterialSlotFinder.Find(type);
                        if (verify == null || verify.Value.MaterialId != newMaterialId)
                            failed.Add($"{type.Name} (반영 확인 실패 - 지정 후에도 이전 재료로 남아있음)");
                    }
                    catch (System.Exception ex)
                    {
                        failed.Add($"{type.Name} ({ex.Message})");
                    }
                }

                // NAMER의 이름 변경 트랜잭션에서 확인된 라이브 버그(커스텀 IFailuresPreprocessor를 붙이면
                // 실패 메시지 없이도 조용히 롤백되는 현상)와 같은 부류의 위험을 새로 감수하지 않기 위해,
                // 여기도 커스텀 전처리기 없이 Revit 기본 처리에 맡기고 TransactionStatus만 확인한다.
                status = tx.Commit();
            }

            if (status != TransactionStatus.Committed)
            {
                TaskDialog.Show("재료 지정", $"재료 지정이 모델에 반영되지 않았습니다 (트랜잭션 롤백: {status}).");
                return Result.Failed;
            }

            if (failed.Count > 0)
            {
                string detail = string.Join("\n", failed.Take(30));
                if (failed.Count > 30) detail += $"\n... 외 {failed.Count - 30}개";
                TaskDialog.Show("재료 지정", $"{failed.Count}개 유형의 재료를 지정하지 못했습니다:\n" + detail);
            }

            return Result.Succeeded;
        }

        private static Result ExecuteDelete(Document doc, List<ElementId> materialIds)
        {
            var failed = new List<string>();
            TransactionStatus status;

            using (Transaction tx = new Transaction(doc, "재료 일괄 삭제"))
            {
                tx.Start();

                foreach (ElementId materialId in materialIds)
                {
                    Element? mat = doc.GetElement(materialId);
                    if (mat == null) continue;

                    try
                    {
                        // Document.Delete가 실제로 삭제한 id 목록을 반환값으로 알려준다 - 예외 없이
                        // 반환됐다고 삭제가 실제로 됐다고 단정하지 않고, 그 목록에 이 재료 id가 들어있는지
                        // 확인해야 "조용한 미삭제"(예: 시스템 기본 재료 등 Revit이 내부적으로 보호하는
                        // 경우)를 실패로 정확히 보고할 수 있다("정확하게 반영됐으면 좋겠다"는 라이브 피드백).
                        ICollection<ElementId> deletedIds = doc.Delete(materialId);
                        if (!deletedIds.Contains(materialId))
                            failed.Add($"{mat.Name} (삭제되지 않음 - Revit이 보호하는 재료일 수 있음)");
                    }
                    catch (System.Exception ex)
                    {
                        failed.Add($"{mat.Name} ({ex.Message})");
                    }
                }

                // 위 ExecuteAssign과 같은 이유로 커스텀 IFailuresPreprocessor를 붙이지 않는다.
                status = tx.Commit();
            }

            if (status != TransactionStatus.Committed)
            {
                TaskDialog.Show("재료 삭제", $"재료 삭제가 모델에 반영되지 않았습니다 (트랜잭션 롤백: {status}).");
                return Result.Failed;
            }

            if (failed.Count > 0)
            {
                string detail = string.Join("\n", failed.Take(30));
                if (failed.Count > 30) detail += $"\n... 외 {failed.Count - 30}개";
                TaskDialog.Show("재료 삭제", $"{failed.Count}개 재료를 삭제하지 못했습니다:\n" + detail);
            }

            return Result.Succeeded;
        }

        private static Result ExecuteIdentityEdit(Document doc, List<(ElementId MaterialId, string? NewClass, string? NewDescription)> edits)
        {
            var failed = new List<string>();
            TransactionStatus status;

            using (Transaction tx = new Transaction(doc, "재료 클래스/설명 일괄 변경"))
            {
                tx.Start();

                foreach ((ElementId materialId, string? newClass, string? newDescription) in edits)
                {
                    Element? el = doc.GetElement(materialId);
                    if (el is not Material mat) continue;

                    try
                    {
                        Parameter? descriptionParam = newDescription != null
                            ? mat.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)
                            : null;
                        if (newDescription != null && descriptionParam == null)
                        {
                            failed.Add($"{mat.Name} (설명 파라미터를 찾지 못함)");
                            continue;
                        }

                        if (newClass != null) mat.MaterialClass = newClass;
                        if (newDescription != null) descriptionParam!.Set(newDescription);

                        // Apply 계열 다른 커맨드와 같은 이유("정확하게 반영됐으면 좋겠다")로, 커밋 전에
                        // 같은 트랜잭션 안에서 다시 읽어 실제 값이 지정하려던 값과 일치하는지 확인한다.
                        bool classOk = newClass == null || mat.MaterialClass == newClass;
                        bool descriptionOk = newDescription == null ||
                            (mat.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString() ?? "") == newDescription;
                        if (!classOk || !descriptionOk)
                            failed.Add($"{mat.Name} (반영 확인 실패 - 지정 후에도 이전 값으로 남아있음)");
                    }
                    catch (System.Exception ex)
                    {
                        failed.Add($"{mat.Name} ({ex.Message})");
                    }
                }

                // 위 ExecuteAssign/ExecuteDelete와 같은 이유로 커스텀 IFailuresPreprocessor를 붙이지 않는다.
                status = tx.Commit();
            }

            if (status != TransactionStatus.Committed)
            {
                TaskDialog.Show("클래스/설명 변경", $"변경 사항이 모델에 반영되지 않았습니다 (트랜잭션 롤백: {status}).");
                return Result.Failed;
            }

            if (failed.Count > 0)
            {
                string detail = string.Join("\n", failed.Take(30));
                if (failed.Count > 30) detail += $"\n... 외 {failed.Count - 30}개";
                TaskDialog.Show("클래스/설명 변경", $"{failed.Count}개 재료의 클래스/설명을 변경하지 못했습니다:\n" + detail);
            }

            return Result.Succeeded;
        }
    }
}
