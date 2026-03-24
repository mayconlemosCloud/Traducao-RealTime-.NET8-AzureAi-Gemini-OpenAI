using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace MeetingGoogle.Converters;

public class Base64ToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string base64 && !string.IsNullOrEmpty(base64))
        {
            try
            {
                byte[] binaryData = System.Convert.FromBase64String(base64);
                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = new MemoryStream(binaryData);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                return bi;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Converter] Erro ao converter Base64 para Image: {ex.Message}");
                return null;
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
