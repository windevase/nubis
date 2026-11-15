using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>7차 검수 — SFTP 패널: 큰 목록 선택 복원 (SftpListBox.SelectItems)</summary>
public class Review7SftpTests
{
    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    [Fact]
    public void 수만_개_선택을_한_번에_복원한다()
    {
        RunSta(() =>
        {
            var items = Enumerable.Range(0, 30000)
                .Select(i => new SftpItem { Name = $"f{i:00000}", FullName = $"/d/f{i:00000}" }).ToList();
            var list = new SftpListBox { SelectionMode = System.Windows.Controls.SelectionMode.Extended, ItemsSource = items };
            var changed = 0;
            list.SelectionChanged += (_, _) => changed++;

            var sw = Stopwatch.StartNew();
            Assert.True(list.SelectItems(items.Where((_, i) => i % 2 == 0).ToList()));
            sw.Stop();

            Assert.Equal(15000, list.SelectedItems.Count);
            Assert.Equal(1, changed);   // 선택 변경 이벤트는 한 번
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"선택 복원이 너무 느림: {sw.Elapsed}");
        });
    }

    [Fact]
    public void 빈_목록을_주면_선택을_모두_해제한다()
    {
        RunSta(() =>
        {
            var items = Enumerable.Range(0, 10).Select(i => new SftpItem { Name = $"f{i}", FullName = $"/d/f{i}" }).ToList();
            var list = new SftpListBox { SelectionMode = System.Windows.Controls.SelectionMode.Extended, ItemsSource = items };
            list.SelectItems(items.Take(3).ToList());
            Assert.Equal(3, list.SelectedItems.Count);
            list.SelectItems(new List<SftpItem>());
            Assert.Empty(list.SelectedItems);
        });
    }
}
