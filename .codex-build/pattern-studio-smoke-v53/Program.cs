using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;

        string wallSplitterPath = Path.GetFullPath(args[0]);
        string wallSplitterFolder = Path.GetDirectoryName(wallSplitterPath)!;
        string revitFolder = Path.GetFullPath(args[1]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (string candidate in new[]
                     {
                         Path.Combine(wallSplitterFolder, name.Name + ".dll"),
                         Path.Combine(revitFolder, name.Name + ".dll"),
                     })
            {
                if (File.Exists(candidate)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            }
            return null;
        };

        try
        {
            Assembly revitApi = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(revitFolder, "RevitAPI.dll"));
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(wallSplitterFolder, "Clipper2Lib.dll"));
            Assembly wallSplitter = AssemblyLoadContext.Default.LoadFromAssemblyPath(wallSplitterPath);

            Type definitionType = RequireType(wallSplitter, "WallSplitter.PatternDefinition");
            Type gridType = RequireType(wallSplitter, "WallSplitter.PatternGridDefinition");
            Type windowType = RequireType(wallSplitter, "WallSplitter.PatternStudioWindow");
            Type targetType = RequireProperty(definitionType, "Target").PropertyType;
            object modelTarget = Enum.Parse(targetType, "Model");

            object source = Create(definitionType);
            RequireProperty(definitionType, "Name").SetValue(source, "모델선 캡처 패턴");
            RequireProperty(definitionType, "Target").SetValue(source, modelTarget);
            object grid = Create(gridType);
            RequireProperty(gridType, "Offset").SetValue(grid, 1.0);
            ((IList)RequireProperty(definitionType, "Grids").GetValue(source)!).Add(grid);

            object existing = Create(definitionType);
            RequireProperty(definitionType, "Name").SetValue(existing, "모델선 캡처 패턴_편집");
            RequireProperty(definitionType, "Target").SetValue(existing, modelTarget);
            Type elementIdType = revitApi.GetType("Autodesk.Revit.DB.ElementId", throwOnError: true)!;
            object invalidElementId = elementIdType.GetProperty("InvalidElementId", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            RequireProperty(definitionType, "SourceElementId").SetValue(existing, invalidElementId);

            IList sources = CreateList(definitionType, source);
            IList existingPatterns = CreateList(definitionType, existing);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            object created = Activator.CreateInstance(
                windowType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[] { sources, existingPatterns },
                null) ?? throw new InvalidOperationException("PatternStudioWindow 생성 실패");
            var window = (Window)created;

            ComboBox sourceCombo = RequireName<ComboBox>(window, "SourceComboBox");
            TextBox saveName = RequireName<TextBox>(window, "SaveNameBox");
            ListBox grids = RequireName<ListBox>(window, "GridList");
            if (sourceCombo.Items.Count != 1 || sourceCombo.SelectedIndex != 0)
                throw new InvalidOperationException("캡처 패턴의 초기 선택 실패");
            if (grids.Items.Count != 1 || grids.SelectedIndex != 0)
                throw new InvalidOperationException("선군 초기 선택 실패");
            if (saveName.Text != "모델선 캡처 패턴_편집 2")
                throw new InvalidOperationException("기존 이름 충돌 회피 실패: " + saveName.Text);

            Console.WriteLine("PatternStudio InitializeComponent: PASS");
            Console.WriteLine("Captured source initialization: PASS");
            Console.WriteLine("Existing-name collision avoidance: PASS");
            window.Close();
            application.Shutdown();
            return 0;
        }
        catch (Exception exception)
        {
            Exception current = exception;
            while (current is TargetInvocationException invocation && invocation.InnerException != null)
                current = invocation.InnerException;
            Console.Error.WriteLine(current);
            return 1;
        }
    }

    private static IList CreateList(Type itemType, object item)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;
        list.Add(item);
        return list;
    }

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name, true, false)!;

    private static object Create(Type type) =>
        Activator.CreateInstance(type, nonPublic: true)!;

    private static PropertyInfo RequireProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMemberException(type.FullName, name);

    private static T RequireName<T>(FrameworkElement root, string name) where T : class =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"컨트롤 누락: {name}");
}
