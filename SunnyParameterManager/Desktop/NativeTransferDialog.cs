using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace SunnyParameterManager.Desktop;

// 작업용 Revit 프로세스의 기본 표준 전송 대화상자만 조작한다.
internal static class NativeTransferDialog
{
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromMinutes(3);
    private static readonly string[] ProjectParameterNames = ["프로젝트 매개변수", "Project Parameters"];

    public static Task Run(int processId, string sourceTitle, Action<string>? progress)
    {
        var context = SynchronizationContext.Current;
        Action<string>? report = progress == null ? null : message =>
        {
            if (context == null) progress(message);
            else context.Post(_ => progress(message), null);
        };
        return Task.Run(() => RunOnAutomationThread(processId, sourceTitle, report));
    }

    private static void RunOnAutomationThread(int processId, string sourceTitle, Action<string>? progress)
    {
        var dialog = WaitFor(processId, IsTransferDialog, DialogTimeout);
        progress?.Invoke("Revit 기본 전송 창 · 기준 파일 지정 중");
        SelectSource(dialog, processId, sourceTitle);
        var deselect = FindById(dialog, ControlType.Button, "2222") ??
            FindByName(dialog, ControlType.Button, "모두 선택 안 함", "Check None", "Deselect All");
        if (deselect == null) throw new InvalidOperationException("Revit 기본 창의 '모두 선택 안 함'을 찾지 못했습니다. 작업용 Revit을 확인해 주세요.");
        if (!ClickNativeButton(dialog, 2222)) Invoke(deselect);
        progress?.Invoke("Revit 기본 전송 창 · 프로젝트 매개변수만 선택 중");
        SelectProjectParameters(dialog);
        var ok = FindById(dialog, ControlType.Button, "1") ?? FindByName(dialog, ControlType.Button, "확인", "OK");
        if (ok == null) throw new InvalidOperationException("Revit 기본 전송 창의 확인 버튼을 찾지 못했습니다.");
        if (!ClickNativeButton(dialog, 1)) Invoke(ok);
        progress?.Invoke("Revit 기본 프로젝트 표준 전송 처리 중");

        var watch = Stopwatch.StartNew();
        DateTime? closedSince = null;
        while (watch.Elapsed < TimeSpan.FromMinutes(15))
        {
            var windows = Windows(processId);
            var duplicate = windows.FirstOrDefault(IsDuplicateDialog);
            if (duplicate != null) { closedSince = null; ClickNativeOverwrite(duplicate); }
            else if (windows.Any(w => !IsTransferDialog(w) && IsUnexpectedModal(w)))
                throw new InvalidOperationException("Revit 기본 전송 중 추가 확인 창이 나타났습니다. 작업용 Revit에서 해당 창을 확인해 주세요.");
            else if (windows.Any(IsTransferDialog)) closedSince = null;
            else
            {
                closedSince ??= DateTime.UtcNow;
                // 대화상자가 닫힌 직후 나타나는 Revit 중복 항목 창까지 처리한다.
                if (DateTime.UtcNow - closedSince.Value >= TimeSpan.FromSeconds(3)) return;
            }
            Thread.Sleep(350);
        }
        throw new TimeoutException("Revit 기본 전송 창이 닫히지 않았습니다. 작업용 Revit을 확인해 주세요.");
    }

    private static void SelectSource(AutomationElement dialog, int processId, string sourceTitle)
    {
        var combo = FindById(dialog, ControlType.ComboBox, "2412") ??
            dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));
        if (combo == null) throw new InvalidOperationException("Revit 기본 전송 창의 '복사 위치'를 찾지 못했습니다.");
        if (ReadValue(combo) == sourceTitle) return;
        if (SelectNativeCombo(dialog, sourceTitle) && ReadValue(combo) == sourceTitle) return;
        if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
        {
            ((ExpandCollapsePattern)expand).Expand();
            var item = FindSourceItem(dialog, processId, sourceTitle);
            if (item != null && item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
                ((SelectionItemPattern)selection).Select();
            try { ((ExpandCollapsePattern)expand).Collapse(); } catch (InvalidOperationException) { /* 항목을 고르면 자동으로 닫힐 수 있다. */ }
        }
        if (ReadValue(combo) != sourceTitle && combo.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            ((ValuePattern)value).SetValue(sourceTitle);
        if (ReadValue(combo) != sourceTitle)
            throw new InvalidOperationException("기준 파일을 Revit 기본 창에서 선택하지 못했습니다: " + sourceTitle);
    }

    private static AutomationElement? FindSourceItem(AutomationElement dialog, int processId, string title)
    {
        for (var i = 0; i < 20; i++)
        {
            foreach (var root in new[] { dialog }.Concat(Windows(processId)))
            {
                var items = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                foreach (AutomationElement item in items)
                    if (string.Equals(item.Current.Name, title, StringComparison.OrdinalIgnoreCase)) return item;
            }
            Thread.Sleep(100);
        }
        return null;
    }

    private static void SelectProjectParameters(AutomationElement dialog)
    {
        var list = FindById(dialog, ControlType.List, "2372") ??
            dialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List));
        if (list == null) throw new InvalidOperationException("Revit 기본 창의 전송 항목 목록을 찾지 못했습니다.");
        ScrollToNativeItem(dialog);
        for (int i = 0; i < 100; i++)
        {
            var boxes = list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
            foreach (AutomationElement box in boxes)
            {
                if (!ProjectParameterNames.Any(n => string.Equals(box.Current.Name, n, StringComparison.OrdinalIgnoreCase))) continue;
                if (!box.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
                    throw new InvalidOperationException("'프로젝트 매개변수' 체크박스를 선택할 수 없습니다.");
                var pattern = (TogglePattern)toggle;
                if (pattern.Current.ToggleState != ToggleState.On) pattern.Toggle();
                if (pattern.Current.ToggleState != ToggleState.On)
                    throw new InvalidOperationException("'프로젝트 매개변수' 선택 상태를 확인할 수 없습니다.");
                return;
            }
            if (list.TryGetCurrentPattern(ScrollPattern.Pattern, out var scroll))
            {
                var pattern = (ScrollPattern)scroll;
                if (pattern.Current.VerticalScrollPercent >= 100) break;
                pattern.ScrollVertical(ScrollAmount.LargeIncrement);
            }
            else
            {
                var bar = list.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ScrollBar));
                if (bar == null || !bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out var range)) break;
                var pattern = (RangeValuePattern)range;
                if (pattern.Current.Value >= pattern.Current.Maximum) break;
                pattern.SetValue(Math.Min(pattern.Current.Maximum, pattern.Current.Value + Math.Max(pattern.Current.LargeChange, 1)));
            }
        }
        throw new InvalidOperationException("Revit 기본 창에서 '프로젝트 매개변수' 항목을 찾지 못했습니다.");
    }

    private static void ClickNativeOverwrite(AutomationElement dialog)
    {
        var button = FindByName(dialog, ControlType.Button, "기존 항목 덮어쓰기", "덮어쓰기", "Overwrite", "Overwrite existing") ??
            FindByName(dialog, ControlType.RadioButton, "기존 항목 덮어쓰기", "덮어쓰기", "Overwrite", "Overwrite existing");
        if (button == null) throw new InvalidOperationException("Revit의 중복 항목 창에서 기본 '덮어쓰기' 선택을 찾지 못했습니다.");
        if (button.Current.ControlType == ControlType.RadioButton && button.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
        {
            ((SelectionItemPattern)select).Select();
            var ok = FindById(dialog, ControlType.Button, "1") ?? FindByName(dialog, ControlType.Button, "확인", "OK");
            if (ok == null) throw new InvalidOperationException("Revit 중복 항목 창의 확인 버튼을 찾지 못했습니다.");
            Invoke(ok);
        }
        else Invoke(button);
    }

    private static AutomationElement WaitFor(int processId, Func<AutomationElement, bool> predicate, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            var found = Windows(processId).FirstOrDefault(predicate);
            if (found != null) return found;
            Thread.Sleep(250);
        }
        throw new TimeoutException("작업용 Revit의 기본 프로젝트 표준 전송 창이 열리지 않았습니다.");
    }

    private static List<AutomationElement> Windows(int processId)
    {
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
        var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, condition).Cast<AutomationElement>().ToList();
        var windows = new List<AutomationElement>(roots);
        foreach (var root in roots)
            windows.AddRange(root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)).Cast<AutomationElement>());
        return windows;
    }

    private static bool IsTransferDialog(AutomationElement element) =>
        element.Current.Name.Contains("프로젝트 표준 전송", StringComparison.OrdinalIgnoreCase) ||
        element.Current.Name.Contains("Transfer Project Standards", StringComparison.OrdinalIgnoreCase);
    private static bool IsDuplicateDialog(AutomationElement element) =>
        element.Current.Name.Contains("중복", StringComparison.OrdinalIgnoreCase) ||
        element.Current.Name.Contains("Duplicate Types", StringComparison.OrdinalIgnoreCase);
    private static bool IsUnexpectedModal(AutomationElement element) =>
        element.TryGetCurrentPattern(WindowPattern.Pattern, out var window) && ((WindowPattern)window).Current.IsModal &&
        !element.Current.Name.Contains("Autodesk Revit", StringComparison.OrdinalIgnoreCase);
    private static AutomationElement? FindById(AutomationElement root, ControlType type, string id) =>
        root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, type),
            new PropertyCondition(AutomationElement.AutomationIdProperty, id)));
    private static AutomationElement? FindByName(AutomationElement root, ControlType type, params string[] names)
    {
        var all = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type));
        return all.Cast<AutomationElement>().FirstOrDefault(e => names.Any(n => e.Current.Name.Contains(n, StringComparison.OrdinalIgnoreCase)));
    }
    private static string ReadValue(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.Value;
        if (element.TryGetCurrentPattern(SelectionPattern.Pattern, out var selection))
            return ((SelectionPattern)selection).Current.GetSelection().FirstOrDefault()?.Current.Name ?? "";
        return element.Current.Name;
    }
    private static void Invoke(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            throw new InvalidOperationException("Revit 기본 대화상자의 버튼을 누를 수 없습니다: " + element.Current.Name);
        ((InvokePattern)invoke).Invoke();
    }

    private static bool SelectNativeCombo(AutomationElement dialog, string title)
    {
        var handle = GetDlgItem((IntPtr)dialog.Current.NativeWindowHandle, 2412);
        if (handle == IntPtr.Zero) return false;
        var value = Marshal.StringToHGlobalUni(title);
        try
        {
            var index = Send(handle, 0x158, new IntPtr(-1), value); // CB_FINDSTRINGEXACT
            if (index.ToInt64() < 0) return false;
            Send(handle, 0x14E, index, IntPtr.Zero); // CB_SETCURSEL
            var command = new IntPtr((1 << 16) | 2412); // CBN_SELCHANGE
            Send((IntPtr)dialog.Current.NativeWindowHandle, 0x111, command, handle); // WM_COMMAND
            return true;
        }
        finally { Marshal.FreeHGlobal(value); }
    }

    private static bool ClickNativeButton(AutomationElement dialog, int id)
    {
        var handle = GetDlgItem((IntPtr)dialog.Current.NativeWindowHandle, id);
        if (handle == IntPtr.Zero) return false;
        Send(handle, 0x00F5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
        return true;
    }

    private static void ScrollToNativeItem(AutomationElement dialog)
    {
        var list = GetDlgItem((IntPtr)dialog.Current.NativeWindowHandle, 2372);
        if (list == IntPtr.Zero) return;
        foreach (var name in ProjectParameterNames)
        {
            var value = Marshal.StringToHGlobalUni(name);
            try
            {
                var index = Send(list, 0x1A2, new IntPtr(-1), value); // LB_FINDSTRINGEXACT
                if (index.ToInt64() < 0) continue;
                Send(list, 0x197, index, IntPtr.Zero); // LB_SETTOPINDEX
                return;
            }
            finally { Marshal.FreeHGlobal(value); }
        }
    }

    private static IntPtr Send(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (SendMessageTimeout(handle, message, wParam, lParam, 0x0002, 5000, out var result) == IntPtr.Zero)
            throw new InvalidOperationException("Revit 기본 대화상자가 응답하지 않습니다. 작업용 Revit을 확인해 주세요.");
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}
