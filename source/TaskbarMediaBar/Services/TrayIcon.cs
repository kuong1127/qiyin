using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;
using TaskbarMediaBar.Interop;

namespace TaskbarMediaBar.Services;

public sealed class TrayItem
{
    public string Text { get; init; } = "";

    public Action? Click { get; init; }

    /// <summary>提供勾选状态；为 null 表示不可勾选。</summary>
    public Func<bool>? IsChecked { get; init; }

    public bool IsHeader { get; init; }
    public Func<IEnumerable<TrayItem>>? Children { get; init; }

    public static TrayItem Separator => new() { Text = "-" };

    public static TrayItem Header(string text) => new() { Text = text, IsHeader = true };
}

/// <summary>
/// 托盘图标。播控条被隐藏时（无播放），没有它就没办法退出/改配置了。
/// 图标是运行时用 MDL2 音符字形画出来的，不依赖外部 .ico 资源。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private NotifyIcon? _notify;
    private IntPtr _iconHandle = IntPtr.Zero;
    private Icon? _icon;

    public void Initialize(IEnumerable<TrayItem> items, Action? onDoubleClick, string tooltip)
    {
        try
        {
            _icon = CreateIcon(out _iconHandle);

            var menu = new ContextMenuStrip();
            foreach (var item in items)
            {
                if (item.Text == "-")
                {
                    menu.Items.Add(new ToolStripSeparator());
                    continue;
                }

                var mi = new ToolStripMenuItem(item.Text);
                if (item.Children is not null)
                {
                    mi.DropDownItems.Add("加载来源");
                    mi.DropDownOpening += (_, _) =>
                    {
                        foreach (ToolStripItem old in mi.DropDownItems.Cast<ToolStripItem>().ToArray()) old.Dispose();
                        mi.DropDownItems.Clear();
                        foreach (var child in item.Children())
                        {
                            if (child.Text == "-") { mi.DropDownItems.Add(new ToolStripSeparator()); continue; }
                            var entry = new ToolStripMenuItem(child.Text) { Enabled = !child.IsHeader, Checked = SafeCheck(child.IsChecked) };
                            entry.Click += (_, _) => child.Click?.Invoke();
                            mi.DropDownItems.Add(entry);
                        }
                    };
                }

                if (item.IsHeader)
                {
                    mi.Enabled = false;
                }

                if (item.IsChecked is not null)
                {
                    mi.CheckOnClick = true;
                    mi.Checked = SafeCheck(item.IsChecked);
                    var capturedItem = item;
                    mi.Click += (_, _) =>
                    {
                        capturedItem.Click?.Invoke();
                        mi.Checked = SafeCheck(capturedItem.IsChecked);
                    };
                }
                else if (item.Click is not null)
                {
                    mi.Click += (_, _) => item.Click();
                }

                menu.Items.Add(mi);
            }

            _notify = new NotifyIcon
            {
                Icon = _icon,
                Text = tooltip.Length > 62 ? tooltip[..62] : tooltip,
                Visible = true,
                ContextMenuStrip = menu,
            };

            if (onDoubleClick is not null)
            {
                _notify.DoubleClick += (_, _) => onDoubleClick();
            }

            Log.Write("托盘图标已创建");
        }
        catch (Exception ex)
        {
            Log.Write("创建托盘图标失败: " + ex.Message);
        }
    }

    private static bool SafeCheck(Func<bool>? f)
    {
        try
        {
            return f?.Invoke() ?? false;
        }
        catch
        {
            return false;
        }
    }

    public void SetTooltip(string text)
    {
        try
        {
            if (_notify is not null)
            {
                _notify.Text = text.Length > 62 ? text[..62] : text;
            }
        }
        catch
        {
            // ignore
        }
    }

    public void Balloon(string title, string text)
    {
        try
        {
            _notify?.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>优先使用栖音品牌图标，资源不可用时回退到系统音符。</summary>
    private static Icon CreateIcon(out IntPtr handle)
    {
        handle = IntPtr.Zero;
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("/TaskbarMediaBar;component/Assets/Qiyin.ico", UriKind.Relative));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                using var brand = new Icon(stream, 32, 32);
                return (Icon)brand.Clone();
            }
        }
        catch { /* 保留托盘入口，不让资源读取失败影响控制。 */ }
        const int size = 32;

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var bg = new SolidBrush(Color.FromArgb(255, 0x2E, 0x6D, 0xD8));
            g.FillEllipse(bg, 0, 0, size - 1, size - 1);

            using var font = new Font("Segoe MDL2 Assets", 17f, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.White);
            var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("\uE8D6", font, fg, new RectangleF(0, 1, size, size), fmt);
        }

        handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        try
        {
            if (_notify is not null)
            {
                _notify.Visible = false;
                _notify.Dispose();
                _notify = null;
            }

            _icon?.Dispose();
            _icon = null;

            if (_iconHandle != IntPtr.Zero)
            {
                DestroyIcon(_iconHandle);
                _iconHandle = IntPtr.Zero;
            }
        }
        catch
        {
            // ignore
        }
    }
}
