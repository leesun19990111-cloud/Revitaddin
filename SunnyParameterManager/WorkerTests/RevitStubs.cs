// 실제 WorkerApplication의 실패·문서 보호 흐름 검사. 실제 Revit 종료/동기화를 대신하지 않는다.
using SunnyParameterManager.Core;
namespace Autodesk.Revit.DB { public sealed class Document { } }
namespace Autodesk.Revit.ApplicationServices
{
    public sealed class Application
    {
        public string VersionNumber => "2026";
        public List<DB.Document> Documents { get; } = [];
    }
}
namespace Autodesk.Revit.UI
{
    public enum Result { Succeeded }
    public enum ExternalEventRequest { Accepted }
    public enum PostableCommand { ExitRevit }
    public sealed class RevitCommandId { public static RevitCommandId LookupPostableCommandId(PostableCommand _) => new(); }
    public sealed class UIApplication
    {
        public ApplicationServices.Application Application { get; } = new();
        public int Posted;
        public bool CanPostCommand(RevitCommandId _) => true;
        public void PostCommand(RevitCommandId _) => Posted++;
    }
    public sealed class UIControlledApplication { public ApplicationServices.Application ControlledApplication { get; } = new(); }
    public interface IExternalApplication { Result OnStartup(UIControlledApplication app); Result OnShutdown(UIControlledApplication app); }
    public interface IExternalEventHandler { void Execute(UIApplication ui); string GetName(); }
    public sealed class ExternalEvent : IDisposable
    {
        public static ExternalEvent Create(IExternalEventHandler _) => new();
        public ExternalEventRequest Raise() => ExternalEventRequest.Accepted;
        public void Dispose() { }
    }
}
namespace SunnyParameterManager.Bridge
{
    public sealed class ModelEngine
    {
        public static Response Result = new();
        public static Exception? Error;
        public static bool LeaveDocument;
        public static int Calls;
        public ModelEngine(string folder, Action<Response> progress) { }
        public Response Run(Autodesk.Revit.ApplicationServices.Application app, Request request)
        {
            Calls++;
            if (LeaveDocument) app.Documents.Add(new());
            if (Error != null) throw Error;
            Result.Id = request.Id; return Result;
        }
    }
}
