using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RollingUpdateManager.Models;

namespace RollingUpdateManager.Converters
{
    // ── Status → Color ─────────────────────────────────────────────────────────
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is ServiceStatus status ? status switch
            {
                ServiceStatus.Running   => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)), // verde
                ServiceStatus.Starting  => new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)), // amarillo
                ServiceStatus.Updating  => new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)), // azul
                ServiceStatus.Error     => new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)), // rojo
                ServiceStatus.Unhealthy => new SolidColorBrush(Color.FromRgb(0xFF, 0x57, 0x22)), // naranja
                _                       => new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)), // gris
            } : Brushes.Gray;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }

    // ── Status → Texto legible ─────────────────────────────────────────────────
    public class StatusToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is ServiceStatus s ? s switch
            {
                ServiceStatus.Running   => "🟢 Running",
                ServiceStatus.Starting  => "🟡 Starting…",
                ServiceStatus.Updating  => "🔵 Updating…",
                ServiceStatus.Error     => "🔴 Error",
                ServiceStatus.Unhealthy => "🟠 Unhealthy",
                ServiceStatus.Stopped   => "⚫ Stopped",
                _                       => s.ToString()
            } : "—";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }

    // ── Bool/Object → Visibility ──────────────────────────────────────────────────
    // Soporta: bool true/false, objeto no-nulo/nulo, y parámetro "invert"
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool visible = value switch
            {
                bool b   => b,
                null     => false,
                _        => true   // cualquier objeto no-nulo = visible
            };
            if (parameter is string p && p == "invert") visible = !visible;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }

    // ── Slot → Color (Blue o Green) ────────────────────────────────────────────
    public class SlotLabelToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s)
            {
                if (s.Equals("Blue",  StringComparison.OrdinalIgnoreCase))
                    return new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));
                if (s.Equals("Green", StringComparison.OrdinalIgnoreCase))
                    return new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
            }
            return Brushes.Gray;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }

    // ── Log level → Color ─────────────────────────────────────────────────────
    public class LogLevelToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is string level ? level switch
            {
                "ERROR" => new SolidColorBrush(Color.FromRgb(0xFF, 0x72, 0x72)),
                "WARN"  => new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x40)),
                _       => new SolidColorBrush(Colors.LightGray)
            } : Brushes.LightGray;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }

    // ── Int 0 → "—" ───────────────────────────────────────────────────────────
    public class ZeroToEmptyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is int i && i == 0 ? "—" : value?.ToString() ?? "—";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }
}
