using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DLsiteMedia.Core;

namespace DLsiteMedia.Views;

/// <summary>
/// 下载目标 / 移动目标选择对话框（程序内覆盖层，卡片每行三个）：先选媒体库，多文件夹时再选具体文件夹。
/// excludeLib 非空时排除该媒体库（移动场景排除当前所在库）。
/// </summary>
public class DownTargetDialog
{
    public string? SelectedLib { get; private set; }
    public string? SelectedFolder { get; private set; }

    private readonly string? _excludeLib;
    private readonly bool _moveMode;

    private Action<bool> _close = _ => { };
    private Action _onBottom = () => { };
    private TextBlock _titleText = null!;
    private UniformGrid _grid = null!;
    private Button _bottomButton = null!;

    public DownTargetDialog(string? excludeLib = null, bool moveMode = false)
    {
        _excludeLib = excludeLib;
        _moveMode = moveMode;
    }

    /// <summary>程序内模态显示，返回是否选定了目标（owner 用于定位所属窗口）。</summary>
    public bool Show(DependencyObject? owner)
    {
        var r = OverlayHost.ShowModal(owner, close =>
        {
            _close = close;
            return BuildShell();
        });
        return r == true;
    }

    private FrameworkElement BuildShell()
    {
        var dim = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0x9A, 0, 0, 0)),
            Focusable = true,
        };
        var card = new Border
        {
            Background = Res("WindowBrush", Brushes.Black),
            BorderBrush = Res("BorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 18, 20, 16),
            MinWidth = 540,
            MaxWidth = 780,
            MaxHeight = 640,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var layout = new DockPanel();

        _titleText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Foreground = Res("TextBrush", Brushes.White),
            Margin = new Thickness(0, 0, 0, 12),
        };
        DockPanel.SetDock(_titleText, Dock.Top);
        layout.Children.Add(_titleText);

        _bottomButton = new Button
        {
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 88,
        };
        _bottomButton.Click += (_, _) => _onBottom();
        DockPanel.SetDock(_bottomButton, Dock.Bottom);
        layout.Children.Add(_bottomButton);

        _grid = new UniformGrid { Columns = 3 };
        layout.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _grid,
        });

        card.Child = layout;
        dim.Children.Add(card);
        dim.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _close(false);
                e.Handled = true;
            }
        };

        BuildLibPage();
        return dim;
    }

    private void BuildLibPage()
    {
        _titleText.Text = I18n.Tr(_moveMode ? "选择要移动到的媒体库" : "选择下载到哪个媒体库");
        _grid.Children.Clear();
        var hasTarget = false;
        foreach (var lib in AppConfig.ReadMediaLibs())
        {
            if (_excludeLib != null && lib.Name == _excludeLib)
                continue;  // 排除当前所在媒体库
            var folders = lib.Folders.Where(f => f.Length > 0).ToList();
            if (folders.Count == 0)
                continue;
            hasTarget = true;
            var caption = folders.Count > 1
                ? I18n.Format(I18n.Tr("{n} 个文件夹"), ("n", folders.Count))
                : folders[0];
            var libName = lib.Name;
            _grid.Children.Add(MakeCard(lib.Name, caption, folders.Count > 1 ? null : folders[0], () =>
            {
                SelectedLib = libName;
                if (folders.Count == 1)
                {
                    SelectedFolder = folders[0];
                    _close(true);
                }
                else
                {
                    BuildFolderPage(folders);
                }
            }));
        }
        if (_moveMode && !hasTarget)
            _grid.Children.Add(new TextBlock
            {
                Text = I18n.Tr("没有可移动到的其它媒体库"),
                Foreground = Res("CaptionBrush", Brushes.Gray),
                Margin = new Thickness(2, 6, 0, 0),
            });

        _bottomButton.Content = I18n.Tr("取消");
        _onBottom = () => _close(false);
    }

    private void BuildFolderPage(List<string> folders)
    {
        _titleText.Text = I18n.Tr(_moveMode ? "选择目标文件夹" : "选择下载文件夹");
        _grid.Children.Clear();
        foreach (var folder in folders)
        {
            var f = folder;
            var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name))
                name = folder;
            _grid.Children.Add(MakeCard(name, folder, folder, () =>
            {
                SelectedFolder = f;
                _close(true);
            }));
        }
        _bottomButton.Content = I18n.Tr("← 返回");
        _onBottom = BuildLibPage;
    }

    /// <summary>单张可点击卡片：标题 + 副标题，悬停高亮。</summary>
    private Border MakeCard(string title, string caption, string? tip, Action onClick)
    {
        var baseBrush = Res("CardBrush", new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)));
        var hoverBrush = Res("ButtonHoverBrush", new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)));
        var card = new Border
        {
            Background = baseBrush,
            BorderBrush = Res("BorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(5),
            MinHeight = 84,
            Cursor = Cursors.Hand,
            ToolTip = tip,
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = Res("TextBrush", Brushes.White),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 44,
        });
        panel.Children.Add(new TextBlock
        {
            Text = caption,
            Foreground = Res("CaptionBrush", Brushes.Gray),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        card.Child = panel;
        card.MouseEnter += (_, _) => card.Background = hoverBrush;
        card.MouseLeave += (_, _) => card.Background = baseBrush;
        card.MouseLeftButtonUp += (_, _) => onClick();
        return card;
    }

    private static Brush Res(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}
