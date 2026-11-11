// 실제 WorkerApplication의 실패·문서 보호 흐름 검사. 실제 Revit 종료/동기화를 대신하지 않는다.
using SunnyParameterManager.Core;
namespace Autodesk.Revit.DB { public sealed class Document { public string PathName { get; set; } = ""; } }
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
    public sealed class TransferredProjectStandardsEventArgs : EventArgs
    {
        public DB.Document SourceDocument { get; } = new();
        public DB.Document TargetDocument { get; } = new();
    }
    public sealed class UIControlledApplication
    {
        public ApplicationServices.Application ControlledApplication { get; } = new();
        public event EventHandler<TransferredProjectStandardsEventArgs>? TransferredProjectStandards;
    }
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
    internal sealed class NativeProjectTransferSession(string folder, Action<Response> progress)
    {
        public static int Created;
        public static int Started;
        private Autodesk.Revit.DB.Document? _source;
        private Autodesk.Revit.DB.Document? _target;
        public bool HasCachedSource(Autodesk.Revit.DB.Document document) => ReferenceEquals(_source, document);
        public void MarkTransferred(Autodesk.Revit.DB.Document source, Autodesk.Revit.DB.Document target) { }
        public Response Start(Autodesk.Revit.UI.UIApplication ui, Request req, ref string anchorPath)
        {
            if (_source == null) { _source = new() { PathName = "기준" }; ui.Application.Documents.Add(_source); Created++; }
            _target = new() { PathName = "대상" }; ui.Application.Documents.Add(_target); Started++;
            return new() { Id = req.Id, Success = true };
        }
        public Response Complete(Autodesk.Revit.UI.UIApplication ui, Request req)
        {
            ui.Application.Documents.Remove(_target!); _target = null;
            return new() { Id = req.Id, Success = true };
        }
        public Response End(Autodesk.Revit.UI.UIApplication ui, Request req)
        {
            if (_source != null) ui.Application.Documents.Remove(_source);
            _source = null;
            return new() { Id = req.Id, Success = true };
        }
    }
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
    public sealed class CentralRebuildEngine
    {
        public static Response Result = new();
        public static int Calls;
        public CentralRebuildEngine(string folder, Action<Response> progress) { }
        public Response Run(Autodesk.Revit.ApplicationServices.Application app, Request request)
        {
            Calls++;
            Result.Id = request.Id;
            return Result;
        }
    }
}
