using System.Windows.Controls;
using System.Windows.Input;

namespace LiveCoreStudio.UI.Views;

public partial class ProductionView : UserControl
{
    public ProductionView()
    {
        InitializeComponent();
        // Take keyboard focus so Space (CUT) and Enter (AUTO) work as soon as the page opens.
        Loaded += (_, _) => Keyboard.Focus(this);
        Focusable = true;
    }
}
