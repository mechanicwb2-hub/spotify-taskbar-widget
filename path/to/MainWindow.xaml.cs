# complete code
import System.Windows
import System.Windows.Interop
import TaskbarAnchors

class MainWindow(System.Windows.Window):
    def __init__(self):
        super().__init__()
        self.taskbar_anchors = TaskbarAnchors()
        self.is_tooltip_open = False

    def OnToolTipOpen(self, sender, e):
        """
        Set the flag when a tooltip/popup is opened.
        """
        self.is_tooltip_open = True

    def OnToolTipClose(self, sender, e):
        """
        Set the flag when a tooltip/popup is closed.
        """
        self.is_tooltip_open = False

    def OnPositionUpdate(self, sender, e):
        """
        Skip topmost re-assertion while a tooltip/popup is open.
        """
        if not self.is_tooltip_open:
            self.taskbar_anchors.EnsureTopmost()