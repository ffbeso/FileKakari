using System.Windows.Input;

namespace FileKakari;

internal static class InputSuppressionService
{
    public static bool ShouldSuppressItemsListInputDuringLoad(
        bool isLoading,
        MouseButton changedButton,
        bool isInsideScrollBar,
        bool isColumnHeader,
        bool isInsideItemsList)
    {
        return isLoading
            && changedButton is MouseButton.Left or MouseButton.Right
            && !isInsideScrollBar
            && !isColumnHeader
            && isInsideItemsList;
    }

    public static bool ShouldAllowSingleSelectionInputDuringLoad(
        bool isLoading,
        MouseButton changedButton,
        int clickCount,
        ModifierKeys modifiers,
        bool isInsideScrollBar,
        bool isColumnHeader,
        bool hasItemHit)
    {
        return isLoading
            && changedButton == MouseButton.Left
            && clickCount == 1
            && (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == ModifierKeys.None
            && !isInsideScrollBar
            && !isColumnHeader
            && hasItemHit;
    }

    public static bool CanStartUserPathNavigation(InputBusyState state)
    {
        return !state.IsLoading
            && !state.IsFileOperationInProgress
            && !state.IsFileDragInProgress
            && !state.IsSelecting
            && !state.IsAutoScrolling;
    }

    public static bool CanProcessBackgroundRefresh(InputBusyState state)
    {
        return !state.IsLoading
            && !state.IsRenameActive
            && !state.IsFileDragInProgress
            && !state.IsFileOperationInProgress
            && !state.IsSelecting
            && !state.IsAutoScrolling;
    }
}

internal readonly record struct InputBusyState(
    bool IsLoading,
    bool IsRenameActive,
    bool IsFileDragInProgress,
    bool IsFileOperationInProgress,
    bool IsSelecting,
    bool IsAutoScrolling);
