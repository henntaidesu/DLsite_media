using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DLsiteMedia.Views;

/// <summary>简单 INotifyPropertyChanged 基类，供各页面条目模型使用。</summary>
public abstract class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    /// <summary>手动通知某个派生属性变化。</summary>
    protected void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
