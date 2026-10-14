using System.Windows.Controls;
using System.Windows.Input;

namespace FileKakari;

internal sealed class PaneLoadInteractionTracker : IDisposable
{
    private readonly ListView? _listView;
    private readonly FileListState _state;
    public bool HasInteracted { get; private set; }

    public PaneLoadInteractionTracker(ListView? listView, FileListState state)
    {
        _listView = listView;
        _state = state;
        if (listView is null) return;
        listView.PreviewMouseDown += OnMouseDown;
        listView.PreviewMouseWheel += OnMouseWheel;
        listView.PreviewKeyDown += OnKeyDown;
    }

    private void Record()
    {
        if (_state.IsPublishingItems)
        {
            HasInteracted = true;
            _state.HasPublicationInteraction = true;
        }
    }
    private void OnMouseDown(object sender, MouseButtonEventArgs e) => Record();
    private void OnMouseWheel(object sender, MouseWheelEventArgs e) => Record();
    private void OnKeyDown(object sender, KeyEventArgs e) => Record();

    public void Dispose()
    {
        if (_listView is null) return;
        _listView.PreviewMouseDown -= OnMouseDown;
        _listView.PreviewMouseWheel -= OnMouseWheel;
        _listView.PreviewKeyDown -= OnKeyDown;
    }
}
