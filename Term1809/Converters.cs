using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Term1809 {

	/// <summary>true → Collapsed, false → Visible. Used to hide a tab header's TextBlock while editing.</summary>
	public sealed class InverseBoolToVisibilityConverter : IValueConverter {
		public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
			value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
			value is Visibility v && v != Visibility.Visible;
	}
}
