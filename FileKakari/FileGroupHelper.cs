using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;

namespace FileKakari;

public enum GroupMode
{
    None,
    Extension,
    Date,
    SimilarName
}

public sealed class GroupModeToVirtualizationModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is GroupMode mode && mode != GroupMode.None
            ? VirtualizationMode.Standard
            : VirtualizationMode.Recycling;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

internal static class FileGroupHelper
{
    public static string GetGroupKey(FileEntry entry, GroupMode mode, SimilarNameGroupIndex? groupIndex = null)
    {
        if (mode == GroupMode.SimilarName)
        {
            if (entry.IsDirectory)
            {
                return "フォルダ";
            }
            return groupIndex?.GetGroupKey(entry) ?? FileNameNormalizer.Normalize(entry.Name);
        }

        if (mode == GroupMode.Extension)
        {
            if (entry.IsDirectory)
            {
                return "フォルダ";
            }
            return string.IsNullOrEmpty(entry.Extension) ? "拡張子なし" : entry.Extension.ToLowerInvariant();
        }

        if (mode == GroupMode.Date)
        {
            var date = entry.ModifiedAt.Date;
            var today = DateTime.Today;

            if (date == today)
            {
                return "今日";
            }
            if (date == today.AddDays(-1))
            {
                return "昨日";
            }

            int diff = (7 + ((int)today.DayOfWeek - (int)DayOfWeek.Monday)) % 7;
            var startOfWeek = today.AddDays(-1 * diff);
            if (date >= startOfWeek)
            {
                return "今週";
            }

            var startOfMonth = new DateTime(today.Year, today.Month, 1);
            if (date >= startOfMonth)
            {
                return "今月";
            }

            var startOfYear = new DateTime(today.Year, 1, 1);
            if (date >= startOfYear)
            {
                return "今年";
            }

            return "それ以前";
        }

        return "";
    }

    public static int GetDateGroupOrder(FileEntry entry)
    {
        var date = entry.ModifiedAt.Date;
        var today = DateTime.Today;

        if (date == today) return 0;
        if (date == today.AddDays(-1)) return 1;

        int diff = (7 + ((int)today.DayOfWeek - (int)DayOfWeek.Monday)) % 7;
        var startOfWeek = today.AddDays(-1 * diff);
        if (date >= startOfWeek) return 2;

        var startOfMonth = new DateTime(today.Year, today.Month, 1);
        if (date >= startOfMonth) return 3;

        var startOfYear = new DateTime(today.Year, 1, 1);
        if (date >= startOfYear) return 4;

        return 5;
    }

    public static int CompareGroup(FileEntry x, FileEntry y, GroupMode mode, SimilarNameGroupIndex? groupIndex = null)
    {
        if (mode == GroupMode.SimilarName)
        {
            if (x.IsDirectory != y.IsDirectory)
            {
                return x.IsDirectory ? -1 : 1;
            }
            if (x.IsDirectory)
            {
                return 0;
            }

            string groupX = groupIndex?.GetGroupKey(x) ?? FileNameNormalizer.Normalize(x.Name);
            string groupY = groupIndex?.GetGroupKey(y) ?? FileNameNormalizer.Normalize(y.Name);
            string keyX = FileNameNormalizer.BuildCompareKey(groupX);
            string keyY = FileNameNormalizer.BuildCompareKey(groupY);
            return string.Compare(keyX, keyY, StringComparison.OrdinalIgnoreCase);
        }

        if (mode == GroupMode.Date)
        {
            int orderX = GetDateGroupOrder(x);
            int orderY = GetDateGroupOrder(y);
            return orderX.CompareTo(orderY);
        }

        if (mode == GroupMode.Extension)
        {
            int categoryX = x.IsDirectory ? 0 : (string.IsNullOrEmpty(x.Extension) ? 1 : 2);
            int categoryY = y.IsDirectory ? 0 : (string.IsNullOrEmpty(y.Extension) ? 1 : 2);

            if (categoryX != categoryY)
            {
                return categoryX.CompareTo(categoryY);
            }

            if (categoryX == 2)
            {
                return string.Compare(x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase);
            }

            return 0;
        }

        return 0;
    }

    public static void ApplyGroupMode(ICollectionView? view, GroupMode mode, SimilarNameGroupIndex? groupIndex = null)
    {
        if (view is not ListCollectionView listCollectionView)
        {
            return;
        }

        var currentGroup = listCollectionView.GroupDescriptions.FirstOrDefault() as FileEntryGroupDescription;
        var currentMode = currentGroup?.Mode ?? GroupMode.None;
        var currentGroupIndex = currentGroup?.GroupIndex;

        if (currentMode == mode && ReferenceEquals(currentGroupIndex, groupIndex))
        {
            return;
        }

        PerfLog.WriteVerbose($"apply-group-mode mode={mode} previousMode={currentMode} hasIndex={groupIndex != null}");
        listCollectionView.GroupDescriptions.Clear();
        if (mode != GroupMode.None)
        {
            listCollectionView.GroupDescriptions.Add(new FileEntryGroupDescription(mode, groupIndex));
        }
    }

    public static void ApplyVirtualizationForGroupMode(ListView? listView, GroupMode mode)
    {
        if (listView == null) return;

        var targetMode = mode == GroupMode.None ? VirtualizationMode.Recycling : VirtualizationMode.Standard;
        if (VirtualizingPanel.GetVirtualizationMode(listView) != targetMode)
        {
            VirtualizingPanel.SetVirtualizationMode(listView, targetMode);
            VirtualizingPanel.SetIsVirtualizingWhenGrouping(listView, true);
        }
    }

    public static void ApplyGroupMode(ListView? listView, GroupMode mode, SimilarNameGroupIndex? groupIndex = null)
    {
        ApplyVirtualizationForGroupMode(listView, mode);
        if (listView?.ItemsSource is System.Collections.IEnumerable itemsSource)
        {
            ApplyGroupMode(CollectionViewSource.GetDefaultView(itemsSource), mode, groupIndex);
        }
        else if (listView?.Items is not null)
        {
            ApplyGroupMode(listView.Items, mode, groupIndex);
        }
    }

    public static void ApplyGroupMode(FileListState? fileList, GroupMode mode, SimilarNameGroupIndex? groupIndex = null)
    {
        if (fileList is not null)
        {
            ApplyGroupMode(fileList.ItemsView, mode, groupIndex);
        }
    }
}

internal sealed class FileEntryGroupDescription : GroupDescription
{
    public GroupMode Mode { get; }
    public SimilarNameGroupIndex? GroupIndex { get; set; }

    public FileEntryGroupDescription(GroupMode mode, SimilarNameGroupIndex? groupIndex = null)
    {
        Mode = mode;
        GroupIndex = groupIndex;
    }

    public override object GroupNameFromItem(object item, int level, CultureInfo culture)
    {
        if (item is FileEntry entry)
        {
            return FileGroupHelper.GetGroupKey(entry, Mode, GroupIndex);
        }
        return "";
    }
}
