using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VolumeMixer.Models;

namespace VolumeMixer.Converters;

public static class SliderDragBehavior
{
    public static readonly DependencyProperty EnableProperty =
        DependencyProperty.RegisterAttached(
            "Enable",
            typeof(bool),
            typeof(SliderDragBehavior),
            new PropertyMetadata(false, OnEnableChanged));

    public static readonly DependencyProperty CommitOnReleaseProperty =
        DependencyProperty.RegisterAttached(
            "CommitOnRelease",
            typeof(bool),
            typeof(SliderDragBehavior),
            new PropertyMetadata(false));

    public static bool GetEnable(DependencyObject obj) => (bool)obj.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject obj, bool value) => obj.SetValue(EnableProperty, value);

    public static bool GetCommitOnRelease(DependencyObject obj) => (bool)obj.GetValue(CommitOnReleaseProperty);
    public static void SetCommitOnRelease(DependencyObject obj, bool value) => obj.SetValue(CommitOnReleaseProperty, value);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider) return;

        if ((bool)e.NewValue)
        {
            slider.PreviewMouseLeftButtonDown += OnMouseDown;
            slider.PreviewMouseMove += OnMouseMove;
            slider.PreviewMouseLeftButtonUp += OnMouseUp;
            slider.LostMouseCapture += OnLostMouseCapture;
        }
        else
        {
            slider.PreviewMouseLeftButtonDown -= OnMouseDown;
            slider.PreviewMouseMove -= OnMouseMove;
            slider.PreviewMouseLeftButtonUp -= OnMouseUp;
            slider.LostMouseCapture -= OnLostMouseCapture;
        }
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;

        if (GetCommitOnRelease(slider) && slider.DataContext is AudioTrackBase track)
        {
            track.IsScrubbing = true;
        }

        slider.CaptureMouse();
        UpdateValueFromMouse(slider, e.GetPosition(slider));
        e.Handled = true;
    }

    private static void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsMouseCaptured) return;
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateValueFromMouse(slider, e.GetPosition(slider));
        }
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsMouseCaptured) return;

        UpdateValueFromMouse(slider, e.GetPosition(slider));
        slider.ReleaseMouseCapture();

        if (GetCommitOnRelease(slider) && slider.DataContext is AudioTrackBase track)
        {
            track.CommitSeek(slider.Value);
        }

        e.Handled = true;
    }

    private static void OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (GetCommitOnRelease(slider) && slider.DataContext is AudioTrackBase { IsScrubbing: true } track)
        {
            track.CommitSeek(slider.Value);
        }
    }

    private static void UpdateValueFromMouse(Slider slider, System.Windows.Point pos)
    {
        double width = slider.ActualWidth;
        if (width <= 0) return;

        double ratio = Math.Clamp(pos.X / width, 0.0, 1.0);
        double newValue = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
        slider.Value = Math.Round(newValue);
    }
}
