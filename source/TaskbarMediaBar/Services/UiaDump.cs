using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace TaskbarMediaBar.Services;

/// <summary>
/// UI 自动化诊断：把播放器窗口的自动化树 dump 出来，
/// 用来确认「我喜欢 / 收藏」按钮到底叫什么名字。
/// </summary>
public static class UiaDump
{
    private static readonly string[] LikeWords = { "喜欢", "收藏", "红心", "赞", "like", "favorite", "❤" };

    public static string Dump(string processName)
    {
        string name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        Process? proc = null;
        try
        {
            proc = Process.GetProcessesByName(name).FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        }
        catch (Exception ex)
        {
            return "取进程失败: " + ex.Message;
        }

        if (proc is null)
        {
            return $"没找到 {name} 的可见主窗口（进程没开，或窗口被最小化到托盘）";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"进程: {proc.ProcessName} pid={proc.Id}");
        sb.AppendLine($"主窗口标题: [{proc.MainWindowTitle}]");

        AutomationElement root;
        try
        {
            root = AutomationElement.FromHandle(proc.MainWindowHandle);
        }
        catch (Exception ex)
        {
            return "无法获取根自动化元素: " + ex.Message;
        }

        if (root is null)
        {
            return "根自动化元素为空（该窗口可能没有实现 UI 自动化）";
        }

        var nodes = new List<(AutomationElement Element, int Depth)>();
        Walk(root, 0, nodes, 4000);

        sb.AppendLine($"元素总数: {nodes.Count}");
        sb.AppendLine();
        sb.AppendLine("=== 候选（名字命中关键词）===");

        int hits = 0;
        foreach (var (el, depth) in nodes)
        {
            string n = SafeName(el);
            if (n.Length == 0)
            {
                continue;
            }

            if (!LikeWords.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            hits++;
            sb.AppendLine($"  [{depth}] {Describe(el)}");
        }

        if (hits == 0)
        {
            sb.AppendLine("  （没有任何元素名字命中关键词 —— 说明该 UI 没对 UIA 暴露收藏按钮，需要换策略）");
        }

        sb.AppendLine();
        sb.AppendLine("=== 全部按钮 / 列表项 / 可点击项 ===");
        foreach (var (el, depth) in nodes)
        {
            ControlType? ct = SafeControlType(el);
            if (ct is null)
            {
                continue;
            }

            bool interesting =
                ct == ControlType.Button
                || ct == ControlType.ListItem
                || ct == ControlType.MenuItem
                || ct == ControlType.Hyperlink
                || ct == ControlType.TabItem;

            if (!interesting)
            {
                continue;
            }

            sb.AppendLine($"  [{depth}] {Describe(el)}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 深挖某个控件的全部属性 + 支持的模式 + 父链 + 屏幕坐标。
    /// 用来判断「Invoke 到底是不是空实现」以及能不能用真实鼠标点击兜底。
    /// </summary>
    public static string DumpProps(string processName, string nameFilter)
    {
        string name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        Process? proc = null;
        try
        {
            proc = Process.GetProcessesByName(name).FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        }
        catch (Exception ex)
        {
            return "取进程失败: " + ex.Message;
        }

        if (proc is null)
        {
            return $"没找到 {name} 的可见主窗口";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"进程: {proc.ProcessName} pid={proc.Id} 标题=[{proc.MainWindowTitle}]");

        AutomationElement root;
        try
        {
            root = AutomationElement.FromHandle(proc.MainWindowHandle);
        }
        catch (Exception ex)
        {
            return "无法获取根元素: " + ex.Message;
        }

        var nodes = new List<(AutomationElement Element, int Depth)>();
        Walk(root, 0, nodes, 4000);

        int shown = 0;
        foreach (var (el, depth) in nodes)
        {
            string n = SafeName(el);
            if (n.Length == 0 || !n.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ControlType? ct = SafeControlType(el);
            if (ct != ControlType.Button)
            {
                continue;
            }

            shown++;
            sb.AppendLine();
            sb.AppendLine($"===== 候选 {shown}: depth={depth} =====");
            sb.AppendLine(DescribeFull(el));
            sb.AppendLine("--- 支持的模式 ---");
            sb.AppendLine(DescribePatterns(el));
            sb.AppendLine("--- 父链（往上 6 层）---");
            sb.AppendLine(DescribeParents(el, 6));
        }

        if (shown == 0)
        {
            sb.AppendLine($"没有名字含「{nameFilter}」的按钮");
        }

        return sb.ToString();
    }

    private static string DescribeFull(AutomationElement el)
    {
        var sb = new StringBuilder();
        void Line(string label, Func<string> get)
        {
            try
            {
                sb.AppendLine($"  {label}: {get()}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  {label}: <读取失败 {ex.GetType().Name}>");
            }
        }

        Line("Name", () => el.Current.Name ?? "");
        Line("AutomationId", () => el.Current.AutomationId ?? "");
        Line("ClassName", () => el.Current.ClassName ?? "");
        Line("ControlType", () => el.Current.ControlType?.ProgrammaticName ?? "");
        Line("LocalizedControlType", () => el.Current.LocalizedControlType ?? "");
        Line("ItemType", () => el.Current.ItemType ?? "");
        Line("ItemStatus", () => el.Current.ItemStatus ?? "");
        Line("HelpText", () => el.Current.HelpText ?? "");
        Line("AccessKey", () => el.Current.AccessKey ?? "");
        Line("AcceleratorKey", () => el.Current.AcceleratorKey ?? "");
        Line("IsEnabled", () => el.Current.IsEnabled.ToString());
        Line("IsOffscreen", () => el.Current.IsOffscreen.ToString());
        Line("IsKeyboardFocusable", () => el.Current.IsKeyboardFocusable.ToString());
        Line("HasKeyboardFocus", () => el.Current.HasKeyboardFocus.ToString());
        Line("BoundingRectangle", () =>
        {
            var r = el.Current.BoundingRectangle;
            return $"{r.Left},{r.Top} {r.Width}x{r.Height}";
        });
        Line("ProcessId", () => el.Current.ProcessId.ToString());
        Line("NativeWindowHandle", () => "0x" + el.Current.NativeWindowHandle.ToString("X"));

        return sb.ToString();
    }

    private static string DescribePatterns(AutomationElement el)
    {
        var sb = new StringBuilder();
        try
        {
            var patterns = el.GetSupportedPatterns();
            if (patterns.Length == 0)
            {
                sb.AppendLine("  (没有可用模式)");
            }

            foreach (var p in patterns)
            {
                sb.Append("  " + p.ProgrammaticName);

                try
                {
                    if (p == InvokePattern.Pattern && el.TryGetCurrentPattern(InvokePattern.Pattern, out object? ip))
                    {
                        sb.Append("  [可用]");
                    }

                    if (p == TogglePattern.Pattern && el.TryGetCurrentPattern(TogglePattern.Pattern, out object? tp))
                    {
                        sb.Append($"  toggleState={((TogglePattern)tp).Current.ToggleState}");
                    }

                    if (p == SelectionItemPattern.Pattern && el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? sp))
                    {
                        sb.Append($"  selected={((SelectionItemPattern)sp).Current.IsSelected}");
                    }
                }
                catch (Exception ex)
                {
                    sb.Append($"  <{ex.GetType().Name}>");
                }

                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  读取模式失败: " + ex.Message);
        }

        return sb.ToString();
    }

    private static string DescribeParents(AutomationElement el, int levels)
    {
        var sb = new StringBuilder();
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            AutomationElement? cur = el;
            for (int i = 0; i < levels && cur is not null; i++)
            {
                string desc = "?";
                try
                {
                    string type = cur.Current.ControlType?.ProgrammaticName.Replace("ControlType.", "") ?? "?";
                    var r = cur.Current.BoundingRectangle;
                    desc = $"{type} name='{cur.Current.Name}' id='{cur.Current.AutomationId}' rect={r.Left},{r.Top} {r.Width}x{r.Height}";
                }
                catch
                {
                    // ignore
                }

                sb.AppendLine($"  ↑{i}: {desc}");
                cur = walker.GetParent(cur);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  父链读取失败: " + ex.Message);
        }

        return sb.ToString();
    }

    private static void Walk(AutomationElement element, int depth, List<(AutomationElement, int)> sink, int max)
    {
        if (depth > 24 || sink.Count >= max)
        {
            return;
        }


        sink.Add((element, depth));

        AutomationElementCollection children;
        try
        {
            children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
        }
        catch
        {
            return;
        }

        foreach (AutomationElement child in children)
        {
            if (sink.Count >= max)
            {
                return;
            }

            Walk(child, depth + 1, sink, max);
        }
    }

    private static string SafeName(AutomationElement el)
    {
        try
        {
            return el.Current.Name ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static ControlType? SafeControlType(AutomationElement el)
    {
        try
        {
            return el.Current.ControlType;
        }
        catch
        {
            return null;
        }
    }

    private static string Describe(AutomationElement el)
    {
        string type = "?";
        string cls = "";
        string id = "";
        string name = "";
        string state = "";

        try
        {
            type = el.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
        }
        catch
        {
            // ignore
        }

        try
        {
            cls = el.Current.ClassName ?? "";
        }
        catch
        {
            // ignore
        }

        try
        {
            id = el.Current.AutomationId ?? "";
        }
        catch
        {
            // ignore
        }

        try
        {
            name = el.Current.Name ?? "";
        }
        catch
        {
            // ignore
        }

        if (el.TryGetCurrentPattern(TogglePattern.Pattern, out object tp))
        {
            try
            {
                state = " toggle=" + ((TogglePattern)tp).Current.ToggleState;
            }
            catch
            {
                // ignore
            }
        }

        if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object sp))
        {
            try
            {
                state += " selected=" + ((SelectionItemPattern)sp).Current.IsSelected;
            }
            catch
            {
                // ignore
            }
        }

        return $"{type} name='{name}' class='{cls}' id='{id}'{state}";
    }
}
