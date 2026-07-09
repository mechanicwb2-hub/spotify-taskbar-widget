using System.Windows.Automation;

namespace SpotifyTaskbarWidget;

/// <summary>
/// Localiza, via UI Automation, os elementos da barra de tarefas que servem
/// de âncora ao widget: o botão de widgets/tempo (limite esquerdo) e o botão
/// Iniciar (limite direito). Os valores vêm em píxeis físicos de ecrã.
/// </summary>
internal static class TaskbarAnchors
{
    public static (double? widgetsRight, double? startLeft, double? taskButtonsRight) Get(IntPtr tray)
    {
        double? widgetsRight = null, startLeft = null, taskButtonsRight = null;
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

            // Fim da fila de ícones das apps (para não os tapar quando o widget
            // ancora à direita, em barras alinhadas à esquerda / secundárias)
            var buttons = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons)
            {
                string cls;
                try { cls = button.Current.ClassName ?? ""; }
                catch { continue; }
                if (!cls.StartsWith("Taskbar.TaskListButton", StringComparison.Ordinal)) continue;
                var r = button.Current.BoundingRectangle;
                if (r.IsEmpty) continue;
                if (taskButtonsRight is not double cur || r.Right > cur)
                    taskButtonsRight = r.Right;
            }
        }
        catch { }
        return (widgetsRight, startLeft, taskButtonsRight);
    }
}
