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

internal static class FileGroupHelper
{
    public static string GetGroupKey(FileEntry entry, GroupMode mode)
    {
        if (mode == GroupMode.SimilarName)
        {
            if (entry.IsDirectory)
            {
                return "フォルダ";
            }
            return FileNameNormalizer.Normalize(entry.Name);
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

    public static int CompareGroup(FileEntry x, FileEntry y, GroupMode mode)
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

            string groupX = FileNameNormalizer.Normalize(x.Name);
            string groupY = FileNameNormalizer.Normalize(y.Name);
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

    public static void ApplyGroupMode(ICollectionView? view, GroupMode mode)
    {
        if (view is not ListCollectionView listCollectionView)
        {
            return;
        }

        var currentGroup = listCollectionView.GroupDescriptions.FirstOrDefault() as FileEntryGroupDescription;
        var currentMode = currentGroup?.Mode ?? GroupMode.None;
        if (currentMode == mode)
        {
            return;
        }

        listCollectionView.GroupDescriptions.Clear();
        if (mode != GroupMode.None)
        {
            listCollectionView.GroupDescriptions.Add(new FileEntryGroupDescription(mode));
        }
    }

    public static void ApplyGroupMode(ListView? listView, GroupMode mode)
    {
        if (listView?.ItemsSource is System.Collections.IEnumerable itemsSource)
        {
            ApplyGroupMode(CollectionViewSource.GetDefaultView(itemsSource), mode);
        }
        else if (listView?.Items is not null)
        {
            ApplyGroupMode(listView.Items, mode);
        }
    }

    public static void ApplyGroupMode(FileListState? fileList, GroupMode mode)
    {
        if (fileList is not null)
        {
            ApplyGroupMode(fileList.ItemsView, mode);
        }
    }
}

internal sealed class FileEntryGroupDescription : GroupDescription
{
    public GroupMode Mode { get; }

    public FileEntryGroupDescription(GroupMode mode)
    {
        Mode = mode;
    }

    public override object GroupNameFromItem(object item, int level, CultureInfo culture)
    {
        if (item is FileEntry entry)
        {
            return FileGroupHelper.GetGroupKey(entry, Mode);
        }
        return "";
    }
}
