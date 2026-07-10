using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DLsiteMedia.Core;

namespace DLsiteMedia.Views;

/// <summary>程序内嵌播放器：视频用 MediaElement 渲染，音频只显示文件名，含播放/暂停与进度条。</summary>
public class MediaPlayerDialog : Window
{
    private readonly MediaElement _player = new()
    {
        LoadedBehavior = MediaState.Manual,
        UnloadedBehavior = MediaState.Stop,
        ScrubbingEnabled = true,
    };
    private readonly Button _playButton = new() { Width = 72 };
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 0, IsMoveToPointEnabled = true };
    private readonly TextBlock _time = new()
    {
        Text = "00:00 / 00:00", VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _playing;
    private bool _dragging;

    public MediaPlayerDialog(string path, bool isVideo)
    {
        Title = Path.GetFileName(path);
        Width = isVideo ? 960 : 520;
        Height = isVideo ? 680 : 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["WindowBrush"];

        var layout = new Grid { Margin = new Thickness(8) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (isVideo)
        {
            var host = new Border { Background = Brushes.Black, Child = _player };
            layout.Children.Add(host);
        }
        else
        {
            // 音频：播放器隐藏（仍负责出声），界面只显示文件名
            _player.Width = 0;
            _player.Height = 0;
            var panel = new Grid();
            panel.Children.Add(_player);
            panel.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(path),
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            layout.Children.Add(panel);
        }

        var bar = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _playButton.Click += (_, _) => Toggle();
        bar.Children.Add(_playButton);
        _slider.Margin = new Thickness(8, 0, 8, 0);
        _slider.VerticalAlignment = VerticalAlignment.Center;
        _slider.PreviewMouseDown += (_, _) => _dragging = true;
        _slider.PreviewMouseUp += (_, _) =>
        {
            _player.Position = TimeSpan.FromMilliseconds(_slider.Value);
            _dragging = false;
        };
        Grid.SetColumn(_slider, 1);
        bar.Children.Add(_slider);
        Grid.SetColumn(_time, 2);
        bar.Children.Add(_time);
        Grid.SetRow(bar, 1);
        layout.Children.Add(bar);
        Content = layout;

        _player.MediaOpened += (_, _) =>
        {
            if (_player.NaturalDuration.HasTimeSpan)
                _slider.Maximum = _player.NaturalDuration.TimeSpan.TotalMilliseconds;
        };
        _player.MediaEnded += (_, _) =>
        {
            _playing = false;
            _playButton.Content = I18n.Tr("播放");
        };
        _timer.Tick += (_, _) =>
        {
            if (!_dragging)
                _slider.Value = _player.Position.TotalMilliseconds;
            var duration = _player.NaturalDuration.HasTimeSpan
                ? _player.NaturalDuration.TimeSpan
                : TimeSpan.Zero;
            _time.Text = $"{Fmt(_player.Position)} / {Fmt(duration)}";
        };

        Closed += (_, _) =>
        {
            _timer.Stop();
            _player.Stop();
            _player.Close();
        };

        _player.Source = new Uri(path);
        _player.Play();
        _playing = true;
        _playButton.Content = I18n.Tr("暂停");
        _timer.Start();
    }

    private void Toggle()
    {
        if (_playing)
        {
            _player.Pause();
            _playing = false;
            _playButton.Content = I18n.Tr("播放");
        }
        else
        {
            _player.Play();
            _playing = true;
            _playButton.Content = I18n.Tr("暂停");
        }
    }

    private static string Fmt(TimeSpan time) =>
        $"{(int)time.TotalMinutes:D2}:{time.Seconds:D2}";
}
