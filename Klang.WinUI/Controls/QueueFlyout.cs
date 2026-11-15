using System;
using System.Collections.Generic;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Controls;

/// <summary>
/// 底部播放栏的“播放队列”弹出层：列出当前队列，正在播放的那首高亮并滚动到可见处，点哪首就播哪首；
/// 右键可以“下一首播放”或从队列移除。点过“下一首播放”还没播到的歌会标出来（随机 / 漫游模式下它们也会先播）。
/// 队列可能有几千首，所以用虚拟化的 ListView，行内容在 ContainerContentChanging 里填（不用绑定）。
/// </summary>
public static class QueueFlyout
{
    private const string RowTemplate =
        "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
        "<Grid Height='44' ColumnSpacing='10' Background='Transparent'>" +
        "<Grid.ColumnDefinitions><ColumnDefinition Width='24'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>" +
        "<Border Grid.ColumnSpan='3' Margin='-10,2,-10,2' CornerRadius='4' Visibility='Collapsed' Background='{ThemeResource SubtleFillColorSecondaryBrush}'/>" +
        "<TextBlock Grid.Column='0' VerticalAlignment='Center' HorizontalAlignment='Right' FontSize='12' Opacity='0.6'/>" +
        "<FontIcon Grid.Column='0' FontSize='13' Glyph='&#xE767;' HorizontalAlignment='Right' Visibility='Collapsed' Foreground='{ThemeResource AccentTextFillColorPrimaryBrush}'/>" +
        "<StackPanel Grid.Column='1' VerticalAlignment='Center' Spacing='1'>" +
        "<TextBlock FontSize='13' TextTrimming='CharacterEllipsis'/>" +
        "<StackPanel Orientation='Horizontal' Spacing='6'>" +
        "<Border Padding='5,0,5,1' CornerRadius='3' VerticalAlignment='Center' Visibility='Collapsed' Background='{ThemeResource AccentFillColorDefaultBrush}'>" +
        "<TextBlock FontSize='10' Foreground='{ThemeResource TextOnAccentFillColorPrimaryBrush}'/></Border>" +
        "<TextBlock FontSize='12' Opacity='0.65' TextTrimming='CharacterEllipsis'/>" +
        "</StackPanel>" +
        "</StackPanel>" +
        "<TextBlock Grid.Column='2' VerticalAlignment='Center' FontSize='12' Opacity='0.6' FontFamily='Consolas, Segoe UI'/>" +
        "</Grid></DataTemplate>";

    /// <param name="getQueue">取当前队列（右键操作后队列对象可能被替换，所以每次重新取）。</param>
    /// <param name="upNext">点过“下一首播放”、还没播到的歌。</param>
    public static void Show(FrameworkElement anchor, Func<List<TrackItem>> getQueue, IReadOnlyList<TrackItem> upNext, TrackItem? current,
        Action<TrackItem> play, Action<TrackItem> playNext, Action<TrackItem> remove)
    {
        var queue = getQueue();
        var header = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 4, 12, 8)
        };
        void UpdateHeader() => header.Text = queue.Count == 0 ? L.T("播放队列为空") : L.F("播放队列 · {0} 首", queue.Count);
        UpdateHeader();

        var panel = new StackPanel { Width = 360 };
        panel.Children.Add(header);

        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Top };
        if (queue.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = L.T("从曲库或歌单里播放一首歌后，这里会列出当前的播放队列。"),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                FontSize = 12,
                Margin = new Thickness(12, 0, 12, 8)
            });
            flyout.ShowAt(anchor);
            return;
        }

        string nextTag = L.T("下一首播放");
        var list = new ListView
        {
            MaxHeight = 420,
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            ItemTemplate = (DataTemplate)XamlReader.Load(RowTemplate),
            ItemsSource = queue,
        };
        list.ContainerContentChanging += (s, e) =>
        {
            if (e.Item is not TrackItem t || e.ItemContainer.ContentTemplateRoot is not Grid g) return;
            bool isCur = IsCurrent(t, current);
            bool isUpNext = !isCur && Contains(upNext, t);
            var highlight = (Border)g.Children[0];
            var index = (TextBlock)g.Children[1];
            var playing = (FontIcon)g.Children[2];
            var texts = (StackPanel)g.Children[3];
            var title = (TextBlock)texts.Children[0];
            var line2 = (StackPanel)texts.Children[1];
            var tag = (Border)line2.Children[0];
            var artist = (TextBlock)line2.Children[1];
            var dur = (TextBlock)g.Children[4];

            index.Text = (e.ItemIndex + 1).ToString();
            index.Visibility = isCur ? Visibility.Collapsed : Visibility.Visible;
            playing.Visibility = isCur ? Visibility.Visible : Visibility.Collapsed;
            title.Text = t.Title;
            artist.Text = t.Artist;
            ((TextBlock)tag.Child).Text = nextTag;
            tag.Visibility = isUpNext ? Visibility.Visible : Visibility.Collapsed;
            dur.Text = t.Duration > 0 ? TimeSpan.FromSeconds(t.Duration).ToString(t.Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "";

            // 正在播放的那首：加粗 + 行底色（底色取当前主题的画刷，深浅色下都看得清）
            title.FontWeight = isCur ? FontWeights.SemiBold : FontWeights.Normal;
            highlight.Visibility = isCur ? Visibility.Visible : Visibility.Collapsed;
        };
        list.ItemClick += (s, e) =>
        {
            if (e.ClickedItem is not TrackItem t) return;
            flyout.Hide();
            play(t);
        };

        // 右键操作后重新载入列表，保持原来的滚动位置
        void Reload()
        {
            var sv = FindScrollViewer(list);
            double offset = sv?.VerticalOffset ?? 0;
            queue = getQueue();
            list.ItemsSource = null;
            list.ItemsSource = queue;
            UpdateHeader();
            list.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => FindScrollViewer(list)?.ChangeView(null, offset, null, true));
        }

        list.RightTapped += (s, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is not TrackItem t) return;
            e.Handled = true;
            bool isCur = IsCurrent(t, current);
            var menu = new MenuFlyout();

            var playNow = new MenuFlyoutItem { Text = L.T("播放"), Icon = new FontIcon { Glyph = "" } };
            playNow.Click += (_, _) => { flyout.Hide(); play(t); };
            menu.Items.Add(playNow);

            var next = new MenuFlyoutItem { Text = L.T("下一首播放"), Icon = new FontIcon { Glyph = "" }, IsEnabled = !isCur };
            next.Click += (_, _) => { playNext(t); Reload(); };
            menu.Items.Add(next);

            var rm = new MenuFlyoutItem { Text = L.T("从播放队列移除"), Icon = new FontIcon { Glyph = "" }, IsEnabled = !isCur };
            rm.Click += (_, _) => { remove(t); Reload(); };
            menu.Items.Add(rm);

            menu.ShowAt((UIElement)e.OriginalSource, e.GetPosition((UIElement)e.OriginalSource));
        };
        panel.Children.Add(list);

        // 正在播放的那首滚到可见处，上面留两行，看得到前后的歌
        void ScrollToCurrent()
        {
            int cur = -1;
            for (int k = 0; k < queue.Count; k++) if (IsCurrent(queue[k], current)) { cur = k; break; }
            if (cur < 0) return;
            list.ScrollIntoView(queue[Math.Max(0, cur - 2)], ScrollIntoViewAlignment.Leading);
        }
        bool scrolled = false;
        list.Loaded += (s, e) =>
        {
            if (scrolled) return;
            scrolled = true;
            ScrollToCurrent();
            // 虚拟化列表第一次滚动时行高是估计值，布局完成后再对一次
            list.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ScrollToCurrent);
        };
        flyout.Opened += (s, e) => L.Apply(panel);
        flyout.ShowAt(anchor);
    }

    private static bool IsCurrent(TrackItem t, TrackItem? current) =>
        current != null && (ReferenceEquals(t, current) || (!string.IsNullOrEmpty(t.Id) && t.Id == current.Id));

    private static bool Contains(IReadOnlyList<TrackItem> list, TrackItem t)
    {
        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], t)) return true;
        return false;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
