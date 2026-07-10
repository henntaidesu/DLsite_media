using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DLsiteMedia.Core;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;   // 消除与 System.Windows.Media.MediaPlayer 的歧义

namespace DLsiteMedia.Views;

/// <summary>
/// 详情页内嵌视频播放器：用 LibVLC（VLC 引擎）渲染 + 播放/暂停 + 进度条。
/// 改用 LibVLC 而非 WPF MediaElement，是为了不依赖系统编解码器，H.264/H.265 等都能播放（mp4 不再黑屏）。
/// </summary>
public class InlineMediaPlayer : UserControl
{
    // 进程内共享一个 LibVLC（创建开销较大）；首次使用时定位并加载本机 VLC 库
    private static LibVLC? _sharedLib;
    private static readonly object LibLock = new();

    private static LibVLC SharedLib
    {
        get
        {
            if (_sharedLib != null)
                return _sharedLib;
            lock (LibLock)
            {
                if (_sharedLib == null)
                {
                    LibVLCSharp.Shared.Core.Initialize();
                    _sharedLib = new LibVLC();
                }
            }
            return _sharedLib;
        }
    }

    private readonly LibVLCSharp.WPF.VideoView _videoView = new();
    private readonly MediaPlayer _player;
    private readonly Button _playButton = new() { Width = 72 };
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 0, IsMoveToPointEnabled = true };
    private readonly TextBlock _time = new()
    {
        Text = "00:00 / 00:00", VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly string _path;
    private long _length;        // 总时长（毫秒），由 LengthChanged 提供
    private bool _playing;
    private bool _dragging;
    private bool _closed;
    private bool _started;

    /// <summary>自然播放到结尾时触发（供外层做播放队列自动续播；手动 Shutdown/Stop 不触发）。</summary>
    public event Action? Ended;

    public InlineMediaPlayer(string path)
    {
        _path = path;
        _player = new MediaPlayer(SharedLib);
        _videoView.MediaPlayer = _player;

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        layout.Children.Add(new Border { Background = Brushes.Black, Child = _videoView });

        var bar = new Grid { Margin = new Thickness(0, 6, 0, 0) };
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
            if (_player.IsSeekable)
                _player.Time = (long)Math.Clamp(_slider.Value, 0, _slider.Maximum);
            _dragging = false;
        };
        Grid.SetColumn(_slider, 1);
        bar.Children.Add(_slider);
        Grid.SetColumn(_time, 2);
        bar.Children.Add(_time);
        Grid.SetRow(bar, 1);
        layout.Children.Add(bar);
        Content = layout;

        // VLC 事件在后台线程触发，需调度回 UI 线程更新控件
        _player.LengthChanged += (_, e) => OnUi(() =>
        {
            _length = e.Length;
            if (_length > 0)
                _slider.Maximum = _length;
        });
        _player.TimeChanged += (_, e) => OnUi(() =>
        {
            if (!_dragging)
                _slider.Value = e.Time;
            _time.Text = $"{Fmt(e.Time)} / {Fmt(_length)}";
        });
        _player.EndReached += (_, _) => OnUi(() =>
        {
            _playing = false;
            _playButton.Content = I18n.Tr("播放");
            Ended?.Invoke();
        });
        _player.EncounteredError += (_, _) => OnUi(() =>
        {
            _time.Text = I18n.Tr("无法播放该视频");
        });

        Unloaded += (_, _) => Shutdown();
        // 等控件加载进可视树后再播放，确保视频表面已就绪
        Loaded += (_, _) => StartPlayback();
    }

    private void StartPlayback()
    {
        if (_closed || _started)
            return;
        _started = true;
        PlayMedia();
    }

    private void PlayMedia()
    {
        // FromPath：本地文件直接按路径传入，避免 Uri 对日文等文件名的转义问题
        using var media = new Media(SharedLib, _path, FromType.FromPath);
        _player.Play(media);   // 播放后 MediaPlayer 内部持有引用，media 可立即释放
        _playing = true;
        _playButton.Content = I18n.Tr("暂停");
    }

    /// <summary>停止播放并释放媒体资源，可重复调用。</summary>
    public void Shutdown()
    {
        if (_closed)
            return;
        _closed = true;
        try
        {
            _player.Stop();
            _videoView.MediaPlayer = null;
            _player.Dispose();
            _videoView.Dispose();
        }
        catch (Exception)
        {
            // 释放期间的异常忽略
        }
    }

    private void Toggle()
    {
        if (_closed)
            return;
        if (_playing)
        {
            _player.SetPause(true);
            _playing = false;
            _playButton.Content = I18n.Tr("播放");
        }
        else
        {
            // 已播放到结尾后再点播放：从头重新播放
            if (_length > 0 && _player.Time >= _length - 200 || _player.State == VLCState.Ended)
                PlayMedia();
            else
                _player.SetPause(false);
            _playing = true;
            _playButton.Content = I18n.Tr("暂停");
        }
    }

    private void OnUi(Action action)
    {
        if (_closed)
            return;
        Dispatcher.BeginInvoke(action);
    }

    private static string Fmt(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return $"{(int)time.TotalMinutes:D2}:{time.Seconds:D2}";
    }
}
