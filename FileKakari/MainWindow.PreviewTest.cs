using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace FileKakari;

public partial class MainWindow
{
    private void RunPreviewIntegrationTestIfNeeded()
    {
        if (Environment.GetEnvironmentVariable("FILEKAKARI_AUTO_TEST") != "1")
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            PerfLog.Write("[AutoTest] Automated integration test started.");

            // Wait for items to be populated in the active preview list view (Max 10 seconds)
            ListView? lv = null;
            var itemsLimit = DateTime.Now.AddSeconds(10);
            while (DateTime.Now < itemsLimit)
            {
                bool hasItems = false;
                await Dispatcher.InvokeAsync(() =>
                {
                    lv = GetActivePreviewListView();
                    hasItems = lv != null && lv.Items.Count > 0;
                });
                if (hasItems) break;
                await Task.Delay(200);
            }

            await Task.Delay(1000);

            await Dispatcher.InvokeAsync(() =>
            {
                PerfLog.Write("[AutoTest] Showing preview pane...");
                SetPreviewPaneVisibleByUser(true);
            });
            await Task.Delay(1000);

            for (int i = 0; i < 6; i++)
            {
                FileEntry? entry = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    lv = GetActivePreviewListView();
                    if (lv != null && lv.Items.Count > i)
                    {
                        entry = lv.Items[i] as FileEntry;
                        if (entry != null)
                        {
                            PerfLog.Write($"[AutoTest] Selecting item={entry.Name} path={entry.FullPath}");
                            lv.SelectedItem = entry;
                            lv.ScrollIntoView(entry);
                        }
                    }
                });

                if (entry != null)
                {
                    await Task.Delay(1500);

                    if (i == 0)
                    {
                        PerfLog.Write("[AutoTest] Simulating rapid preview switching...");
                        for (int j = 0; j < 6; j++)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                lv = GetActivePreviewListView();
                                if (lv != null && lv.Items.Count > 1)
                                {
                                    var target = lv.Items[j % 2] as FileEntry;
                                    if (target != null)
                                    {
                                        lv.SelectedItem = target;
                                    }
                                }
                            });
                            await Task.Delay(80);
                        }
                        await Task.Delay(1000);
                    }
                }
            }

            PerfLog.Write("[AutoTest] Triggering fallback test (non-existent bat)...");
            await Dispatcher.InvokeAsync(async () =>
            {
                var monacoClsid = new Guid("D8034CFA-F34B-41FE-AD45-62FCBB52A6DA");
                await ReplacePreviewWithShellAsync(
                    "C:\\non_existent_file_xyz.bat",
                    monacoClsid,
                    null,
                    _previewGeneration,
                    CancellationToken.None);
            });
            await Task.Delay(1500);

            PerfLog.Write("[AutoTest] Automated test complete. Shutting down application...");
            await Dispatcher.InvokeAsync(() =>
            {
                Application.Current.Shutdown();
            });
        });
    }
}
