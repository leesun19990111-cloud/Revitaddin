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
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: WindowSmokeV52 <WallSplitter.dll> <Revit 2026 folder>");
            return 2;
        }

        string wallSplitterPath = Path.GetFullPath(args[0]);
        string wallSplitterFolder = Path.GetDirectoryName(wallSplitterPath)!;
        string revitFolder = Path.GetFullPath(args[1]);

        AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
        {
            string fileName = assemblyName.Name + ".dll";
            foreach (string candidate in new[]
                     {
                         Path.Combine(wallSplitterFolder, fileName),
                         Path.Combine(revitFolder, fileName),
                     })
            {
                if (File.Exists(candidate))
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            }

            return null;
        };

        try
        {
            Assembly revitApi = AssemblyLoadContext.Default.LoadFromAssemblyPath(
                Path.Combine(revitFolder, "RevitAPI.dll"));
            AssemblyLoadContext.Default.LoadFromAssemblyPath(
                Path.Combine(wallSplitterFolder, "Clipper2Lib.dll"));
            Assembly wallSplitter = AssemblyLoadContext.Default.LoadFromAssemblyPath(wallSplitterPath);

            AssemblyName? referencedRevitApi = wallSplitter.GetReferencedAssemblies()
                .SingleOrDefault(name => name.Name == "RevitAPI");
            Console.WriteLine($"Installed RevitAPI: {revitApi.GetName().Version}");
            Console.WriteLine($"Referenced RevitAPI: {referencedRevitApi?.Version}");

            if (referencedRevitApi?.Version != revitApi.GetName().Version)
                throw new InvalidOperationException("WallSplitter.dll의 RevitAPI 참조 버전이 설치본과 일치하지 않습니다.");

            Type definitionType = RequireType(wallSplitter, "WallSplitter.PatternDefinition");
            Type planType = RequireType(wallSplitter, "WallSplitter.PatternPunchPlan");
            Type targetType = RequireType(wallSplitter, "WallSplitter.PatternPunchTarget");
            Type windowType = RequireType(wallSplitter, "WallSplitter.PatternPunchWindow");

            object definition = Create(definitionType);
            PropertyInfo targetProperty = RequireProperty(definitionType, "Target");
            targetProperty.SetValue(definition, Enum.Parse(targetProperty.PropertyType, "Drafting"));

            object plan = Create(planType);
            RequireProperty(planType, "Pattern").SetValue(plan, definition);
            RequireProperty(planType, "PatternName").SetValue(plan, "WPF 초기화 스모크 패턴");
            RequireProperty(planType, "PatternLayerLabel").SetValue(plan, "표면 전경");
            RequireProperty(planType, "ViewScale").SetValue(plan, 100);

            object target = Create(targetType);
            RequireProperty(targetType, "Label").SetValue(target, "QA 대상 면");
            RequireProperty(targetType, "CategoryLabel").SetValue(target, "벽");
            ((IList)RequireProperty(planType, "Targets").GetValue(plan)!).Add(target);

            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            object windowObject = Activator.CreateInstance(
                windowType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new[] { plan },
                culture: null) ?? throw new InvalidOperationException("PatternPunchWindow 생성에 실패했습니다.");
            var window = (Window)windowObject;

            TextBlock patternName = RequireName<TextBlock>(window, "PatternNameText");
            TextBlock patternInfo = RequireName<TextBlock>(window, "PatternInfoText");
            ListBox targetList = RequireName<ListBox>(window, "TargetList");
            Button runButton = RequireName<Button>(window, "RunButton");
            TextBox minimumWidth = RequireName<TextBox>(window, "MinimumWidthBox");
            TextBox minimumHeight = RequireName<TextBox>(window, "MinimumHeightBox");

            if (patternName.Text != "WPF 초기화 스모크 패턴")
                throw new InvalidOperationException("PatternNameText가 계획 값을 받지 못했습니다.");
            if (!patternInfo.Text.Contains("제도 패턴", StringComparison.Ordinal))
                throw new InvalidOperationException("PatternInfoText가 제도 패턴 정보를 표시하지 않습니다.");
            if (targetList.Items.Count != 1 || targetList.SelectedIndex != 0)
                throw new InvalidOperationException("대상 목록의 초기 선택이 올바르지 않습니다.");
            if (runButton.IsEnabled)
                throw new InvalidOperationException("사전 검증 전 타공 실행 버튼이 활성화되어 있습니다.");
            if (minimumWidth.Text != "10" || minimumHeight.Text != "10")
                throw new InvalidOperationException("최소 크기 입력 상자의 XAML 기본값이 손실되었습니다.");

            Console.WriteLine("InitializeComponent: PASS");
            Console.WriteLine("Early TextChanged/SelectionChanged path: PASS");
            Console.WriteLine("Named controls and constructor state: PASS");
            Console.WriteLine($"Window title: {window.Title}");

            window.Close();
            application.Shutdown();
            return 0;
        }
        catch (Exception exception)
        {
            Exception current = exception;
            while (current is TargetInvocationException invocation && invocation.InnerException != null)
                current = invocation.InnerException;

            Console.Error.WriteLine("SMOKE TEST FAILED");
            Console.Error.WriteLine(current);
            return 1;
        }
    }

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: true, ignoreCase: false)!;

    private static object Create(Type type) =>
        Activator.CreateInstance(type, nonPublic: true)
        ?? throw new InvalidOperationException($"{type.FullName} 생성에 실패했습니다.");

    private static PropertyInfo RequireProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMemberException(type.FullName, name);

    private static T RequireName<T>(FrameworkElement root, string name) where T : class =>
        root.FindName(name) as T
        ?? throw new InvalidOperationException($"XAML 컨트롤 '{name}'을 찾지 못했습니다.");
}
