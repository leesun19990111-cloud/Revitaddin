using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    internal static class RevitCompatibility
    {
        internal static IntPtr GetMainWindowHandle(this UIApplication application)
        {
#if REVIT2017
            // Revit 2017은 UIApplication에서 소유 창 핸들을 노출하지 않는다.
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.MainWindowHandle;
#else
            return application.MainWindowHandle;
#endif
        }

        internal static bool IsMaterial(this Definition definition)
        {
#if REVIT2017
            return definition.ParameterType == ParameterType.Material;
#else
            return definition.GetDataType() == SpecTypeId.Reference.Material;
#endif
        }

        internal static bool IsYesNo(this Definition definition)
        {
#if REVIT2017
            return definition.ParameterType == ParameterType.YesNo;
#else
            return definition.GetDataType() == SpecTypeId.Boolean.YesNo;
#endif
        }

        internal static bool IsInteger(this Definition definition)
        {
#if REVIT2017
            return definition.ParameterType == ParameterType.Integer;
#else
            return definition.GetDataType() == SpecTypeId.Int.Integer;
#endif
        }

        internal static string DataTypeKey(this Definition definition)
        {
#if REVIT2017
            return definition.ParameterType.ToString();
#else
            return definition.GetDataType().TypeId;
#endif
        }
    }
}
