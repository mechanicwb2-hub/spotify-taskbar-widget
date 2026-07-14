# complete code
import System.Windows

class App(System.Windows.Application):
    def OnStartup(self, sender, e):
        """
        Initialize the application.
        """
        self.main_window = MainWindow()
        self.main_window.Show()