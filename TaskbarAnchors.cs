using System.Windows.Automation;

namespace SpotifyTaskbarWidget;

/// <summary>
/// Localiza, via UI Automation, os elementos da barra de tarefas que servem
/// de âncora ao widget: o botão de widgets/tempo (limite esquerdo) e o botão
/// Iniciar (limite direito). Os valores vêm em píxeis físicos de ecrã.
/// </summary>
internal static class TaskbarAnchors
{
    public static (double? widgetsRight, double? startLeft) Get(IntPtr tray)
    {
        double? widgetsRight = null, startLeft = null;
        try
        {
            var root = AutomationElement.FromHandle(tray);

            var widgets = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetsButton"));
            if (widgets != null)
            {
                var r = widgets.Current.BoundingRectangle;
                if (!r.IsEmpty) widgetsRight = r.Right;
            }

            var start = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
            if (start != null)
            {
                var r = start.Current.BoundingRectangle;
                if (!r.IsEmpty) startLeft = r.Left;
            }
        }
        catch { }
        return (widgetsRight, startLeft);
    }
}
