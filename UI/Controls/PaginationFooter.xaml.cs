using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace SteamPluginManager.UI.Controls
{
    public partial class PaginationFooter : UserControl
    {
        public event EventHandler<PageChangedEventArgs>? PageChanged;

        public PaginationFooter()
        {
            InitializeComponent();
            UpdatePage(0, 0, 8, "games");
        }

        public void UpdatePage(int currentPage, int totalItems, int pageSize, string itemLabel)
        {
            if (totalItems < 0)
                throw new ArgumentOutOfRangeException(nameof(totalItems));
            if (pageSize < 1)
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            if (string.IsNullOrWhiteSpace(itemLabel))
                throw new ArgumentException("An item label is required.", nameof(itemLabel));

            int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalItems / pageSize));
            currentPage = Math.Clamp(currentPage, 0, totalPages - 1);

            int firstItem = totalItems == 0 ? 0 : currentPage * pageSize + 1;
            int lastItem = Math.Min((currentPage + 1) * pageSize, totalItems);
            RangeText.Text = totalItems == 0
                ? $"Showing 0 of 0 {itemLabel}"
                : $"Showing {firstItem} – {lastItem} of {totalItems} {itemLabel}";

            PageButtonsPanel.Children.Clear();

            if (currentPage > 0)
                AddNavigationButton("‹", currentPage - 1, "Previous page");

            int firstPage = Math.Max(0, Math.Min(currentPage - 1, totalPages - 3));
            int lastPage = Math.Min(totalPages - 1, firstPage + 2);

            if (firstPage > 0)
                AddEllipsis();

            for (int page = firstPage; page <= lastPage; page++)
                AddPageButton(page, page == currentPage);

            if (lastPage < totalPages - 1)
                AddEllipsis();

            if (currentPage < totalPages - 1)
                AddNavigationButton("›", currentPage + 1, "Next page");
        }

        private void AddPageButton(int pageIndex, bool isCurrent)
        {
            var button = CreateButton((pageIndex + 1).ToString(), pageIndex, $"Page {pageIndex + 1}");
            button.Tag = isCurrent ? "Current" : null;
            PageButtonsPanel.Children.Add(button);
        }

        private void AddNavigationButton(string content, int pageIndex, string accessibleName)
        {
            PageButtonsPanel.Children.Add(CreateButton(content, pageIndex, accessibleName));
        }

        private void AddEllipsis()
        {
            PageButtonsPanel.Children.Add(new TextBlock
            {
                Text = "...",
                FontSize = 11,
                Foreground = (Brush)FindResource("MutedForegroundBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            });
        }

        private Button CreateButton(string content, int pageIndex, string accessibleName)
        {
            var button = new Button
            {
                Content = content,
                CommandParameter = pageIndex,
                Style = (Style)FindResource("PaginationButtonStyle")
            };
            button.SetResourceReference(BackgroundProperty, "ButtonBackgroundBrush");
            button.SetResourceReference(ForegroundProperty, "ForegroundBrush");
            button.SetResourceReference(BorderBrushProperty, "BorderBrush");
            AutomationProperties.SetName(button, accessibleName);
            button.Click += PageButton_Click;
            return button;
        }

        private void PageButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { CommandParameter: int pageIndex })
                PageChanged?.Invoke(this, new PageChangedEventArgs(pageIndex));
        }
    }

    public sealed class PageChangedEventArgs : EventArgs
    {
        public int PageIndex { get; }

        public PageChangedEventArgs(int pageIndex)
        {
            PageIndex = pageIndex;
        }
    }
}
