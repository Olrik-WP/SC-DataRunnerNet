using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using DataRunner.App.ViewModels;

namespace DataRunner.App.Behaviors;

/// <summary>
/// Two-way bridge between <see cref="ListBox.SelectedItems"/> (read-only on the
/// binding side, but mutable from code) and an <see cref="IList"/> exposed by a
/// view model. WPF intentionally makes ListBox.SelectedItems hard to bind, so
/// every multi-select scenario in MVVM needs a behavior of this kind.
///
/// Usage in XAML:
///   <ListBox SelectionMode="Extended"
///            beh:ListBoxSelectedItemsBehavior.SelectedItems="{Binding SelectedItems}" />
///
/// One-way sync: ListBox -> ViewModel. (We don't push back from VM -> ListBox
/// because the only producer of selection changes is user interaction.)
/// </summary>
public static class ListBoxSelectedItemsBehavior
{
    public static readonly DependencyProperty SelectedItemsProperty =
        DependencyProperty.RegisterAttached(
            "SelectedItems",
            typeof(IList),
            typeof(ListBoxSelectedItemsBehavior),
            new PropertyMetadata(null, OnSelectedItemsChanged));

    public static IList? GetSelectedItems(DependencyObject obj)
        => (IList?)obj.GetValue(SelectedItemsProperty);

    public static void SetSelectedItems(DependencyObject obj, IList? value)
        => obj.SetValue(SelectedItemsProperty, value);

    private static void OnSelectedItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox lb) return;

        // Detach previous handler if any.
        lb.SelectionChanged -= LbOnSelectionChanged;

        if (e.NewValue is null) return;

        lb.SelectionChanged += LbOnSelectionChanged;
    }

    private static void LbOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox lb) return;
        if (GetSelectedItems(lb) is not IList target) return;

        // Apply diff (avoid Clear+AddAll which would fire two events on
        // observers and break the merge button's CanExecute timing).
        foreach (var removed in e.RemovedItems)
        {
            if (target.Contains(removed)) target.Remove(removed);
        }
        foreach (var added in e.AddedItems)
        {
            if (!target.Contains(added)) target.Add(added);
        }
    }
}
