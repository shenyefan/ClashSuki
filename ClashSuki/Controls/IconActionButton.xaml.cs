using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using System.Windows.Input;

namespace ClashSuki.Controls;

public sealed partial class IconActionButton : UserControl
{
    private IAsyncRelayCommand? _asyncCommand;

    public IconActionButton()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => UpdateInteractionEnabled());
    }

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(IconActionButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(IconActionButton),
            new PropertyMetadata(false, (d, _) =>
            {
                if (d is IconActionButton button)
                {
                    button.UpdateInteractionEnabled();
                }
            }));

    public static readonly DependencyProperty AutoSyncLoadingFromCommandProperty =
        DependencyProperty.Register(nameof(AutoSyncLoadingFromCommand), typeof(bool), typeof(IconActionButton),
            new PropertyMetadata(true, (d, _) =>
            {
                if (d is IconActionButton button)
                {
                    button.TryBindLoadingFromCommand();
                }
            }));

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(IconActionButton),
            new PropertyMetadata(false, (d, _) =>
            {
                if (d is IconActionButton button)
                {
                    button.UpdateSelectionState();
                }
            }));

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(IconActionButton),
            new PropertyMetadata(null, OnCommandChanged));

    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(IconActionButton), new PropertyMetadata(null));

    public static readonly DependencyProperty FlyoutProperty =
        DependencyProperty.Register(nameof(Flyout), typeof(FlyoutBase), typeof(IconActionButton), new PropertyMetadata(null));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public bool AutoSyncLoadingFromCommand
    {
        get => (bool)GetValue(AutoSyncLoadingFromCommandProperty);
        set => SetValue(AutoSyncLoadingFromCommandProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public FlyoutBase? Flyout
    {
        get => (FlyoutBase?)GetValue(FlyoutProperty);
        set => SetValue(FlyoutProperty, value);
    }

    public event RoutedEventHandler? Click;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TryBindLoadingFromCommand();
        UpdateInteractionEnabled();
        UpdateSelectionState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachAsyncCommand();
    }

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not IconActionButton button)
        {
            return;
        }

        button.DetachAsyncCommand();

        if (e.NewValue is IAsyncRelayCommand asyncCommand)
        {
            button._asyncCommand = asyncCommand;
            asyncCommand.PropertyChanged += button.OnAsyncCommandPropertyChanged;
        }

        button.TryBindLoadingFromCommand();
        button.UpdateInteractionEnabled();
    }

    private void TryBindLoadingFromCommand()
    {
        if (!AutoSyncLoadingFromCommand || Command is not IAsyncRelayCommand asyncCommand)
        {
            return;
        }

        SetBinding(IsLoadingProperty, new Binding
        {
            Source = asyncCommand,
            Path = new PropertyPath(nameof(IAsyncRelayCommand.IsRunning)),
            Mode = BindingMode.OneWay
        });
    }

    private void OnAsyncCommandPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
        {
            UpdateInteractionEnabled();
        }
    }

    private void DetachAsyncCommand()
    {
        if (_asyncCommand is null)
        {
            return;
        }

        _asyncCommand.PropertyChanged -= OnAsyncCommandPropertyChanged;
        _asyncCommand = null;
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        Click?.Invoke(this, e);
    }

    private void UpdateInteractionEnabled()
    {
        ActionButton.IsEnabled = IsEnabled && !IsLoading;
    }

    private void UpdateSelectionState()
    {
        VisualStateManager.GoToState(this, IsSelected ? "Selected" : "Unselected", false);
    }
}
