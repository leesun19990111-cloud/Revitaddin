using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace SunnyParameterManager.Desktop;

// 작업용 Revit 프로세스의 기본 표준 전송 대화상자만 조작한다.
internal static class NativeTransferDialog
{
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromMinutes(3);
    private static readonly string[] ProjectParameterNames = ["프로젝트 매개변수", "Project Parameters"];

    public static async Task Run(int processId, string sourceTitle, Action<string>? progress)
    {
        var context = SynchronizationContext.Current;
        Action<string>? report = progress == null ? null : message =>
        {
            if (context == null) progress(message);
            else context.Post(_ => progress(message), null);
        };
        try { await Task.Run(() => RunOnAutomationThread(processId, sourceTitle, report)); }
        catch (ManualSelectionRequiredException ex)
        {
            System.Windows.MessageBox.Show(
                "Revit 기본 전송 창에서 자동 체크 상태를 확인할 수 없습니다.\n\n" +
                "작업용 Revit 창에서 '프로젝트 매개변수'만 체크되어 있는지 확인하고 Revit의 '확인'을 눌러 주세요.\n" +
                "전송을 취소하려면 Revit의 '취소'를 누르세요.\n\n" +
                "Revit 창이 닫힐 때까지 매개변수 관리자는 기다립니다.\n\n" + ex.Message,
                "Sunny 매개변수 관리자 · Revit 기본 창 확인",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            report?.Invoke("Revit 기본 창에서 사용자 선택 대기 중");
            await Task.Run(() => WaitForTransferCompletion(processId));
        }
    }

    private static void RunOnAutomationThread(int processId, string sourceTitle, Action<string>? progress)
    {
        RestoreWorkerWindow(processId);
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

        WaitForTransferCompletion(processId);
    }

    private static void WaitForTransferCompletion(int processId)
    {
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
        var visibleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nativeList = (IntPtr)list.Current.NativeWindowHandle;
        if (nativeList == IntPtr.Zero) nativeList = GetDlgItem((IntPtr)dialog.Current.NativeWindowHandle, 2372);
        var nativeCount = NativeItemCount(nativeList);
        var nativeItems = ReadNativeListItems(nativeList);
        var nativeMatches = nativeItems.Where(item => IsProjectParameterName(item.Name)).ToList();
        if (nativeMatches.Count > 1)
            throw new InvalidOperationException("Revit 기본 전송 목록에 '프로젝트 매개변수' 항목이 여러 개입니다. 자동으로 고르지 않았습니다.");
        if (nativeMatches.Count == 1)
        {
            var index = nativeMatches[0].Index;
            Send(nativeList, 0x0197, new IntPtr(index), IntPtr.Zero); // LB_SETTOPINDEX
            Send(nativeList, 0x019E, new IntPtr(index), IntPtr.Zero); // LB_SETCARETINDEX
            Thread.Sleep(120);
            list = FindById(dialog, ControlType.List, "2372") ?? list;
            if (TrySelectNamedParameter(list, visibleNames)) return;
            try
            {
                var fromHandle = AutomationElement.FromHandle(nativeList);
                if (fromHandle != null && TrySelectNamedParameter(fromHandle, visibleNames)) return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            { /* 기본 목록 위치는 옮겼으므로 나머지 접근성 경로를 계속 확인한다. */ }
            if (TryCheckNativeRow(nativeList, index, list, visibleNames)) return;
            throw new ManualSelectionRequiredException(
                "목록 " + nativeCount + "개 중 해당 행: " + (index + 1) + "번째 · 자동 체크 확인 실패");
        }
        for (int i = 0; i < Math.Min(100, Math.Max(20, nativeCount)); i++)
        {
            // Revit 목록은 화면 밖 행을 접근성 트리에서 제외할 수 있으므로 매번 새로 읽는다.
            list = FindById(dialog, ControlType.List, "2372") ?? list;
            if (TrySelectNamedParameter(list, visibleNames)) return;
            if (list.TryGetCurrentPattern(ItemContainerPattern.Pattern, out var container))
            {
                foreach (var name in ProjectParameterNames)
                {
                    try
                    {
                        var item = ((ItemContainerPattern)container).FindItemByProperty(null, AutomationElement.NameProperty, name);
                        if (item == null) continue;
                        if (item.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualized))
                            ((VirtualizedItemPattern)virtualized).Realize();
                        if (item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var itemScroll))
                            ((ScrollItemPattern)itemScroll).ScrollIntoView();
                        if (TrySelectNamedParameter(list, visibleNames)) return;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or COMException)
                    { /* Revit 목록이 가상 항목 실현을 거부하면 기본 스크롤 경로를 사용한다. */ }
                }
            }
            var items = list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            var namedItem = items.Cast<AutomationElement>().FirstOrDefault(item => IsProjectParameterName(item.Current.Name));
            if (namedItem != null && namedItem.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scrollItem))
            {
                ((ScrollItemPattern)scrollItem).ScrollIntoView();
                continue;
            }
            bool moved = false;
            if (list.TryGetCurrentPattern(ScrollPattern.Pattern, out var scroll))
            {
                var pattern = (ScrollPattern)scroll;
                if (pattern.Current.VerticallyScrollable && pattern.Current.VerticalScrollPercent < 100)
                { pattern.ScrollVertical(ScrollAmount.LargeIncrement); moved = true; }
            }
            if (!moved)
            {
                var bar = list.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ScrollBar));
                if (bar != null && bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out var range))
                {
                    var pattern = (RangeValuePattern)range;
                    if (pattern.Current.Value < pattern.Current.Maximum)
                    { pattern.SetValue(Math.Min(pattern.Current.Maximum, pattern.Current.Value + Math.Max(pattern.Current.LargeChange, 1))); moved = true; }
                }
            }
            if (!moved) moved = ScrollNativeList(nativeList, i, nativeCount);
            if (!moved) break;
            Thread.Sleep(80);
        }
        throw new ManualSelectionRequiredException("Revit 기본 창에서 '프로젝트 매개변수' 자동 선택을 확인하지 못했습니다. " +
            "목록 종류: " + NativeClass(nativeList) + " · 전체 항목: " + nativeCount +
            " · 원시 목록에서 찾음: " + (nativeMatches.Count == 1 ? nativeMatches[0].Index.ToString() : "아니오") +
            " · 원시 목록 앞/뒤: " + string.Join(", ", nativeItems.Take(5).Select(item => item.Name)
                .Concat(nativeItems.TakeLast(5).Select(item => item.Name))) +
            " · 접근성 항목 수: " + visibleNames.Count +
            " · 접근성 항목 앞/뒤: " + string.Join(", ", visibleNames.Take(5).Concat(visibleNames.TakeLast(5))));
    }

    private static bool IsProjectParameterName(string value) =>
        ProjectParameterNames.Any(name => NormalizeItemName(value).Contains(NormalizeItemName(name), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeItemName(string value) => new(value.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '_').ToArray());

    private static bool TryCheckNativeRow(IntPtr handle, int index, AutomationElement list, HashSet<string> names)
    {
        if (handle == IntPtr.Zero || !NativeClass(handle).Equals("ListBox", StringComparison.OrdinalIgnoreCase)) return false;
        if (TryGetMsaaCheck(handle, index, out var alreadyChecked) && alreadyChecked) return true;
        Send(handle, 0x0197, new IntPtr(index), IntPtr.Zero); // LB_SETTOPINDEX
        var rectPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
        try
        {
            if (Send(handle, 0x0198, new IntPtr(index), rectPtr).ToInt64() < 0) return false; // LB_GETITEMRECT
            var row = Marshal.PtrToStructure<NativeRect>(rectPtr);
            if (!GetClientRect(handle, out var client)) return false;
            var x = Math.Min(row.Right - 1, Math.Max(row.Left + 8, client.Left + 8));
            var y = row.Top + (row.Bottom - row.Top) / 2;
            if (x < client.Left || x >= client.Right || y < client.Top || y >= client.Bottom) return false;
            var point = new IntPtr((y & 0xffff) << 16 | (x & 0xffff));
            var hit = Send(handle, 0x01A9, IntPtr.Zero, point).ToInt64(); // LB_ITEMFROMPOINT
            if ((hit & 0xffff) != index || ((hit >> 16) & 0xffff) != 0) return false;
            Send(handle, 0x0201, new IntPtr(1), point); // WM_LBUTTONDOWN
            Send(handle, 0x0202, IntPtr.Zero, point); // WM_LBUTTONUP
            Thread.Sleep(150);
            if (TryGetMsaaCheck(handle, index, out var checkedAfter) && checkedAfter) return true;
            try
            {
                list = AutomationElement.FromHandle(handle);
                return TrySelectNamedParameter(list, names);
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException) { return false; }
        }
        finally { Marshal.FreeHGlobal(rectPtr); }
    }

    private static bool TryGetMsaaCheck(IntPtr handle, int index, out bool isChecked)
    {
        isChecked = false;
        var iid = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71"); // IID_IAccessible
        object? accessible = null;
        try
        {
            if (AccessibleObjectFromWindow(handle, 0xFFFFFFFC, ref iid, out accessible) != 0 || accessible == null)
                return false; // OBJID_CLIENT
            var type = accessible.GetType();
            var child = index + 1; // MSAA 목록 항목은 1부터 시작한다.
            var name = Convert.ToString(type.InvokeMember("accName", System.Reflection.BindingFlags.GetProperty,
                null, accessible, [child]));
            if (string.IsNullOrEmpty(name) || !IsProjectParameterName(name)) return false;
            var state = Convert.ToInt32(type.InvokeMember("accState", System.Reflection.BindingFlags.GetProperty,
                null, accessible, [child]));
            isChecked = (state & 0x10) != 0; // STATE_SYSTEM_CHECKED
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or System.Reflection.TargetInvocationException)
        { return false; }
        finally { if (accessible != null && Marshal.IsComObject(accessible)) Marshal.ReleaseComObject(accessible); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private sealed class ManualSelectionRequiredException(string message) : InvalidOperationException(message);

    private static List<(int Index, string Name)> ReadNativeListItems(IntPtr list)
    {
        var result = new List<(int, string)>();
        if (list == IntPtr.Zero || !NativeClass(list).Equals("ListBox", StringComparison.OrdinalIgnoreCase)) return result;
        var count = Math.Min(NativeItemCount(list), 5000);
        for (var index = 0; index < count; index++)
        {
            var length = Send(list, 0x018A, new IntPtr(index), IntPtr.Zero).ToInt64(); // LB_GETTEXTLEN
            if (length < 0 || length > 1024) continue;
            var buffer = Marshal.AllocHGlobal(Math.Max(IntPtr.Size + sizeof(char), checked((int)(length + 1) * sizeof(char))));
            try
            {
                Marshal.WriteInt16(buffer, 0);
                var copied = Send(list, 0x0189, new IntPtr(index), buffer).ToInt64(); // LB_GETTEXT
                if (copied >= 0 && copied <= length)
                {
                    var name = Marshal.PtrToStringUni(buffer, (int)copied)?.Trim() ?? "";
                    if (name.Length > 0) result.Add((index, name));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }

    private static bool TrySelectNamedParameter(AutomationElement list, HashSet<string> visibleNames)
    {
        var elements = list.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToList();
        foreach (var element in elements)
        {
            var type = element.Current.ControlType;
            if (type != ControlType.CheckBox && type != ControlType.ListItem && type != ControlType.DataItem && type != ControlType.Text) continue;
            var name = element.Current.Name;
            if (name.Length > 0) visibleNames.Add(name);
            if (!IsProjectParameterName(name)) continue;
            AutomationElement? target = type == ControlType.CheckBox ? element : null;
            if (target == null && (type == ControlType.ListItem || type == ControlType.DataItem))
                target = element.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
            if (target == null && type == ControlType.Text)
            {
                var walker = TreeWalker.ControlViewWalker;
                var parent = walker.GetParent(element);
                if (parent != null && (parent.Current.ControlType == ControlType.ListItem || parent.Current.ControlType == ControlType.DataItem))
                    target = parent.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
                if (target == null && !element.Current.BoundingRectangle.IsEmpty)
                {
                    var row = element.Current.BoundingRectangle;
                    var rowCenter = (row.Top + row.Bottom) / 2;
                    target = elements.Where(candidate => candidate.Current.ControlType == ControlType.CheckBox)
                        .FirstOrDefault(candidate =>
                        {
                            var box = candidate.Current.BoundingRectangle;
                            return !box.IsEmpty && box.Right <= row.Right &&
                                Math.Abs((box.Top + box.Bottom) / 2 - rowCenter) <= Math.Max(8, row.Height / 2);
                        });
                }
            }
            target ??= element;
            if (!target.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) continue;
            var pattern = (TogglePattern)toggle;
            if (pattern.Current.ToggleState != ToggleState.On) pattern.Toggle();
            if (pattern.Current.ToggleState != ToggleState.On)
                throw new InvalidOperationException("'프로젝트 매개변수' 선택 상태를 확인할 수 없습니다.");
            return true;
        }
        return false;
    }

    private static int NativeItemCount(IntPtr list)
    {
        if (list == IntPtr.Zero) return 0;
        var name = NativeClass(list);
        var message = name.Equals("SysListView32", StringComparison.OrdinalIgnoreCase) ? 0x1004u :
            name.Equals("ListBox", StringComparison.OrdinalIgnoreCase) ? 0x018Bu : 0u;
        if (message == 0) return 0;
        try { return Math.Max(0, (int)Send(list, message, IntPtr.Zero, IntPtr.Zero).ToInt64()); }
        catch (InvalidOperationException) { return 0; }
    }

    private static bool ScrollNativeList(IntPtr list, int step, int count)
    {
        if (list == IntPtr.Zero) return false;
        var name = NativeClass(list);
        if (count > 0 && name.Equals("SysListView32", StringComparison.OrdinalIgnoreCase))
        {
            var index = Math.Min(count - 1, (step + 1) * 8);
            Send(list, 0x1013, new IntPtr(index), IntPtr.Zero); // LVM_ENSUREVISIBLE
            return step <= (count - 1) / 8;
        }
        if (count > 0 && name.Equals("ListBox", StringComparison.OrdinalIgnoreCase))
        {
            var index = Math.Min(count - 1, (step + 1) * 8);
            Send(list, 0x0197, new IntPtr(index), IntPtr.Zero); // LB_SETTOPINDEX
            return step <= (count - 1) / 8;
        }
        // Autodesk 전용 목록은 UIA 스크롤 패턴과 표준 ListBox 메시지가 모두 없을 수 있다.
        Send(list, 0x0115, new IntPtr(3), IntPtr.Zero); // WM_VSCROLL / SB_PAGEDOWN
        var parent = GetParent(list);
        if (parent != IntPtr.Zero) Send(parent, 0x0115, new IntPtr(3), list);
        return step < 40;
    }

    private static string NativeClass(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "창 핸들 없음";
        var name = new System.Text.StringBuilder(128);
        return GetClassName(handle, name, name.Capacity) > 0 ? name.ToString() : "알 수 없음";
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

    private static void RestoreWorkerWindow(int processId)
    {
        using var process = Process.GetProcessById(processId);
        var handle = process.MainWindowHandle;
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("작업용 Revit의 창을 찾지 못했습니다. 전송 창을 직접 확인해 주세요.");
        ShowWindow(handle, 9); // SW_RESTORE: 최소화된 Revit의 전송 항목을 접근성 트리에 표시한다.
        if (IsIconic(handle))
            throw new InvalidOperationException("작업용 Revit 창을 복원하지 못했습니다. 최소화된 창을 열고 다시 확인해 주세요.");
        SetForegroundWindow(handle);
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

    private static IntPtr Send(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (SendMessageTimeout(handle, message, wParam, lParam, 0x0002, 5000, out var result) == IntPtr.Zero)
            throw new InvalidOperationException("Revit 기본 대화상자가 응답하지 않습니다. 작업용 Revit을 확인해 주세요.");
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr handle, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object? accessible);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr handle, System.Text.StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}
