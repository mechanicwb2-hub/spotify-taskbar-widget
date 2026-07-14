# complete code
import System.Windows
import System.Windows.Interop

class Interop:
    @staticmethod
    def is_tooltip_open():
        """
        Check if a tooltip/popup is open.
        """
        try:
            # Get the current window
            window = System.Windows.Window.GetWindow(System.Windows.Application.Current.MainWindow)
            # Get the tooltip service
            tooltip_service = System.Windows.Interop.TooltipServiceFactory.GetService()
            # Check if a tooltip is open
            return tooltip_service.IsOpen
        except Exception as e:
            # Handle any exceptions
            print(f"Error checking for open tooltip: {e}")
            return False