using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace SunnyParameterManager.Bridge;

// 이 작업의 문서 오류만 읽는다. 오류 삭제·해결·결합 해제·처리 결과 변경은 하지 않는다.
internal sealed class TransactionDiagnostics : IDisposable
{
    private readonly RevitApplication _app;
    private readonly Document _doc;
    private readonly HashSet<string> _errors = [];
    private bool _unreadable;
    internal bool HasErrors => _errors.Count > 0 || _unreadable;
    internal string Details => string.Join("\n", _errors) + (_unreadable ? "\nRevit 오류 세부정보를 읽지 못했습니다. 안전을 위해 저장하지 않습니다." : "");
    internal TransactionDiagnostics(RevitApplication app, Document doc)
    {
        _app = app; _doc = doc;
        _app.FailuresProcessing += Observe;
    }
    private void Observe(object? sender, FailuresProcessingEventArgs e)
    {
        try
        {
            using var accessor = e.GetFailuresAccessor();
            if (!accessor.GetDocument().Equals(_doc)) return;
            foreach (var failure in accessor.GetFailureMessages())
            {
                if (failure.GetSeverity() is not (FailureSeverity.Error or FailureSeverity.DocumentCorruption)) continue;
                if (_errors.Count >= 25) break;
                var ids = failure.GetFailingElementIds().Concat(failure.GetAdditionalElementIds()).Select(id => id.Value).Distinct().Order().Take(40);
                _errors.Add(failure.GetDescriptionText() + " / 요소 ID: " + string.Join(", ", ids));
            }
        }
        // Revit 이벤트 밖으로 예외를 보내지 않는다. 불명 상태는 성공으로 취급하지 않는다.
        catch { _unreadable = true; }
    }
    public void Dispose() => _app.FailuresProcessing -= Observe;
}
