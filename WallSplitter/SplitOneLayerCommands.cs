using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    [Transaction(TransactionMode.Manual)]
    public class SplitWallOneLayerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            => new SplitWallCommand().ExecuteOneLayer(commandData, ref message, elements);
    }

    [Transaction(TransactionMode.Manual)]
    public class SplitFloorOneLayerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            => new SplitFloorCommand().ExecuteOneLayer(commandData, ref message, elements);
    }
}
