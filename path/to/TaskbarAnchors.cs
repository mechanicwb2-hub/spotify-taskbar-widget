# complete code
import System.Windows
import System.Windows.Interop
import Interop

class TaskbarAnchors:
    def EnsureTopmost(self):
        """
        Ensure the widget is topmost, skipping re-assertion while a tooltip/popup is open.
        """
        if not Interop.is_tooltip_open():
            # Get the current window
            window = System.Windows.Window.GetWindow(System.Windows.Application.Current.MainWindow)
            # Set the window as topmost
            window.Topmost = True