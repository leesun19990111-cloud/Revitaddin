using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDocument = Autodesk.Revit.DB.Document;

namespace WallSplitter
{
    // 리본 "전체 갱신" 버튼. 실시간 반영을 꺼둔 상태에서도, 또는 애드인을 설치하기 전에 만들어진
    // 기존 요소들을 한 번에 맞출 때 쓴다(Dynamo Player의 "Run"에 해당하는 자리).
    [Transaction(TransactionMode.Manual)]
    public class ParamCombineRefreshCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            RevitDocument? doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null)
            {
                message = "열려 있는 문서가 없습니다.";
                return Result.Failed;
            }

            ParamCombineSettings settings = ParamCombineSettings.Current;
            int ruleCount = 0;
            foreach (CombineRule _ in settings.ActiveRules) ruleCount++;

            if (ruleCount == 0)
            {
                TaskDialog.Show("매개변수 조합", "적용할 규칙이 없습니다.\n'매개변수 조합' 버튼을 눌러 소스 매개변수와 대상 매개변수를 먼저 정해주세요.");
                return Result.Succeeded;
            }

            CombineRunResult result;
            using (Transaction tx = new Transaction(doc, "매개변수 조합 - 전체 갱신"))
            {
                tx.Start();
                result = ParamCombineEngine.RunAll(doc, settings);
                tx.Commit();
            }

            TaskDialog.Show("매개변수 조합", result.Summary());
            return Result.Succeeded;
        }
    }
}
