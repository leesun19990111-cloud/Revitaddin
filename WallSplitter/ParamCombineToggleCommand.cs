using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDocument = Autodesk.Revit.DB.Document;

namespace WallSplitter
{
    // 리본 "실시간 켜짐/꺼짐" 토글. 설정 창을 열지 않고 자동 반영만 껐다 켠다
    // (ToggleTypeAssignmentPersistenceCommand와 같은 패턴 - 라벨/아이콘을 App이 갱신한다).
    [Transaction(TransactionMode.Manual)]
    public class ParamCombineToggleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;

            ParamCombineSettings settings = ParamCombineSettings.Current.Clone();
            settings.AutoUpdate = !settings.AutoUpdate;
            settings.Save();

            ParamCombineUpdater.RefreshTriggers(uiapp.Application);
            App.UpdateParamCombineToggleLabel(settings.AutoUpdate);

            if (!settings.AutoUpdate)
            {
                TaskDialog.Show("매개변수 조합", "실시간 자동 반영을 껐습니다.\n규칙은 그대로 남아 있고, '전체 갱신' 버튼으로는 언제든 한 번에 적용할 수 있습니다.");
                return Result.Succeeded;
            }

            // 켜는 순간 이미 모델에 있는 값들은 아직 옛날 값이다 - 켜자마자 한 번 맞춰줘야
            // "켰는데 왜 안 바뀌지?"가 생기지 않는다(앞으로의 변경은 IUpdater가 자동으로 따라간다).
            RevitDocument? doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null)
            {
                TaskDialog.Show("매개변수 조합", "실시간 자동 반영을 켰습니다.\n문서를 열면 그 문서부터 바로 적용됩니다.");
                return Result.Succeeded;
            }

            CombineRunResult result;
            using (Transaction tx = new Transaction(doc, "매개변수 조합 - 전체 갱신"))
            {
                tx.Start();
                result = ParamCombineEngine.RunAll(doc, settings);
                tx.Commit();
            }

            TaskDialog.Show("매개변수 조합",
                "실시간 자동 반영을 켰습니다. 이제 소스 매개변수를 고치면 결과가 즉시 따라 바뀝니다.\n\n" +
                "현재 모델 기준으로 한 번 맞춘 결과: " + result.Summary());
            return Result.Succeeded;
        }
    }
}
