using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DLsiteMedia.Core;

namespace DLsiteMedia.Views;

/// <summary>程序内看图：大图自适应窗口，左右键 / 按钮切换同文件夹的所有图片。</summary>
public class ImageViewerDialog : Window
{
    private readonly List<string> _paths;
    private int _index;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _counter = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public ImageViewerDialog(List<string> paths, int index)
    {
        _paths = paths;
        _index = Math.Max(0, index);
        Width = 960;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["WindowBrush"];

        var layout = new Grid { Margin = new Thickness(8) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var imageHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
            CornerRadius = new CornerRadius(4),
            Child = _image,
        };
        layout.Children.Add(imageHost);

        var bar = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var prev = new Button { Content = "‹ " + I18n.Tr("上一张") };
        prev.Click += (_, _) => Step(-1);
        bar.Children.Add(prev);
        Grid.SetColumn(_counter, 1);
        bar.Children.Add(_counter);
        var next = new Button { Content = I18n.Tr("下一张") + " ›" };
        next.Click += (_, _) => Step(1);
        Grid.SetColumn(next, 2);
        bar.Children.Add(next);
        Grid.SetRow(bar, 1);
        layout.Children.Add(bar);

        Content = layout;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Left)
                Step(-1);
            else if (e.Key == Key.Right)
                Step(1);
            else if (e.Key == Key.Escape)
                Close();
        };
        Load();
    }

    private void Step(int delta)
    {
        _index = ((_index + delta) % _paths.Count + _paths.Count) % _paths.Count;
        Load();
    }

    private void Load()
    {
        var path = _paths[_index];
        Title = Path.GetFileName(path);
        _counter.Text = $"{_index + 1} / {_paths.Count}";
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            _image.Source = bmp;
        }
        catch (Exception)
        {
            _image.Source = null;
        }
    }
}
