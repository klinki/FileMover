using Avalonia.Controls;

namespace BackupNormalizer.Ui.Views;

public partial class InventoryDetailsControl : UserControl
{
    private Window? _window;
    private bool _settingResponsiveExpansion;
    private bool _userChangedExpansion;

    public InventoryDetailsControl()
    {
        InitializeComponent();
        DetailsExpander.PropertyChanged += (_, args) =>
        {
            if (args.Property == Expander.IsExpandedProperty && !_settingResponsiveExpansion)
            {
                _userChangedExpansion = true;
            }
        };
        AttachedToVisualTree += (_, _) => AttachWindow();
        DetachedFromVisualTree += (_, _) => DetachWindow();
    }

    private void AttachWindow()
    {
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window == null)
        {
            return;
        }

        _window.SizeChanged += OnWindowSizeChanged;
        UpdateResponsiveExpansion();
    }

    private void DetachWindow()
    {
        if (_window != null)
        {
            _window.SizeChanged -= OnWindowSizeChanged;
        }

        _window = null;
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs args) =>
        UpdateResponsiveExpansion();

    private void UpdateResponsiveExpansion()
    {
        if (_window == null || _userChangedExpansion)
        {
            return;
        }

        _settingResponsiveExpansion = true;
        DetailsExpander.IsExpanded = _window.Bounds.Height >= 720;
        _settingResponsiveExpansion = false;
    }
}
