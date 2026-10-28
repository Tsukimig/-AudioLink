using System.Windows;
using System.Windows.Media;

namespace AudioLink.Controls;

/// <summary>Real input peak history, painted in one lightweight visual.</summary>
public sealed class SignalHistory : FrameworkElement
{
    private readonly float[] _history = new float[48];
    private readonly SolidColorBrush _active = new(Color.FromRgb(186, 239, 157));
    private readonly SolidColorBrush _idle = new(Color.FromRgb(48, 68, 49));
    public SignalHistory() { _active.Freeze(); _idle.Freeze(); }
    public void Push(double peak)
    {
        Array.Copy(_history, 1, _history, 0, _history.Length - 1);
        _history[^1] = (float)Math.Clamp(peak / 100, 0, 1);
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double step = ActualWidth / _history.Length;
        for (int i = 0; i < _history.Length; i++)
        {
            double height = Math.Max(3, Math.Sqrt(_history[i]) * (ActualHeight - 6));
            dc.DrawRoundedRectangle(_history[i] > 0.0001 ? _active : _idle, null, new Rect(i * step, (ActualHeight - height) / 2, Math.Max(2, step - 2), height), 1.5, 1.5);
        }
    }
}
