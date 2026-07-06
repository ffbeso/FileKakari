namespace FileKakari;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

internal sealed class ListViewRangeSelectionSession
{
    private readonly ListView _listView;
    private readonly Point _startPoint;
    private readonly bool _additive;
    private readonly HashSet<FileEntry> _baseSelection;
    private bool _moved;

    public ListViewRangeSelectionSession(ListView listView, Point startPoint, bool additive)
    {
        _listView = listView;
        _startPoint = startPoint;
        _additive = additive;
        _baseSelection = new HashSet<FileEntry>(listView.SelectedItems.OfType<FileEntry>());
    }

    public ListView ListView => _listView;
    public Point StartPoint => _startPoint;
    public bool Additive => _additive;
    public bool Moved => _moved;
    public IReadOnlySet<FileEntry> BaseSelection => _baseSelection;

    public bool CheckMove(Point currentPoint)
    {
        if (_moved)
        {
            return true;
        }

        if (Math.Abs(currentPoint.X - _startPoint.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(currentPoint.Y - _startPoint.Y) >= SystemParameters.MinimumVerticalDragDistance)
        {
            _moved = true;
        }

        return _moved;
    }

    public void ApplySelection(Point currentPoint)
    {
        var rect = FileListRangeSelectionHelper.CreateSelectionRect(_startPoint, currentPoint);
        FileListRangeSelectionHelper.SelectItemsInRange(_listView, rect, _additive, _baseSelection);
    }
}
