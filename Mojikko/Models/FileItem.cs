using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mojikko.Core;

namespace Mojikko.Models;

/// <summary>一覧の 1 行。</summary>
public sealed class FileItem : INotifyPropertyChanged
{
    public required TextSource Source { get; init; }
    public string DisplayName => Source.DisplayName;

    private string _encoding = "";
    public string EncodingName { get => _encoding; set => Set(ref _encoding, value); }

    private string _size = "";
    public string Size { get => _size; set => Set(ref _size, value); }

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024:0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:0} KB";
        return $"{bytes} B";
    }
}
