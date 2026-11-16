using System.Windows.Media;

namespace Velocity.App;

/// <summary>A library section shown in the sidebar navigation.</summary>
public sealed class LibraryFilter : System.ComponentModel.INotifyPropertyChanged
{
    private int _count;

    public LibraryFilter(string name, Geometry icon, Func<Velocity.Services.DownloadItem, bool> match)
    {
        Name = name;
        Icon = icon;
        Match = match;
    }

    public string Name { get; }
    public Geometry Icon { get; }
    public Func<Velocity.Services.DownloadItem, bool> Match { get; }

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;
}
