using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using Point = System.Windows.Point;

namespace MeetingTranslator;

public partial class ScreenCaptureWindow : Window
{
    private Point _startPoint;
    private bool _isDrawing;
    public string? Base64CapturedImage { get; private set; }

    public ScreenCaptureWindow()
    {
        InitializeComponent();
        
        // Configura a janela para cobrir toda a área virtual (todos os monitores)
        this.Left = SystemParameters.VirtualScreenLeft;
        this.Top = SystemParameters.VirtualScreenTop;
        this.Width = SystemParameters.VirtualScreenWidth;
        this.Height = SystemParameters.VirtualScreenHeight;
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _startPoint = e.GetPosition(CaptureCanvas);
            _isDrawing = true;
            SelectionRectangle.Visibility = Visibility.Visible;
            CaptureCanvas.CaptureMouse();
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDrawing)
        {
            var currentPoint = e.GetPosition(CaptureCanvas);
            
            var x = Math.Min(currentPoint.X, _startPoint.X);
            var y = Math.Min(currentPoint.Y, _startPoint.Y);
            var width = Math.Max(currentPoint.X, _startPoint.X) - x;
            var height = Math.Max(currentPoint.Y, _startPoint.Y) - y;

            SelectionRectangle.Width = width;
            SelectionRectangle.Height = height;
            System.Windows.Controls.Canvas.SetLeft(SelectionRectangle, x);
            System.Windows.Controls.Canvas.SetTop(SelectionRectangle, y);
        }
    }

    private async void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDrawing)
        {
            _isDrawing = false;
            CaptureCanvas.ReleaseMouseCapture();
            SelectionRectangle.Visibility = Visibility.Hidden;

            int screenWidth = (int)SelectionRectangle.Width;
            int screenHeight = (int)SelectionRectangle.Height;

            if (screenWidth > 0 && screenHeight > 0)
            {
                // Obter as coordenadas físicas se houver diferença de DPI ANTES de esconder a janela
                PresentationSource source = PresentationSource.FromVisual(this);
                double dpiX = 1.0, dpiY = 1.0;
                
                if (source?.CompositionTarget != null)
                {
                    dpiX = source.CompositionTarget.TransformToDevice.M11;
                    dpiY = source.CompositionTarget.TransformToDevice.M22;
                }
                else
                {
                    // Fallback para DPI padrão
                    dpiX = VisualTreeHelper.GetDpi(this).DpiScaleX;
                    dpiY = VisualTreeHelper.GetDpi(this).DpiScaleY;
                }

                // Coordenadas relativas ao Canvas (que cobre todo o VirtualScreen)
                double left = System.Windows.Controls.Canvas.GetLeft(SelectionRectangle);
                double top = System.Windows.Controls.Canvas.GetTop(SelectionRectangle);

                this.Hide(); 

                // Aguarda a janela ser removida visualmente da composição do Desktop
                await System.Threading.Tasks.Task.Delay(100); 

                // Converter coordenadas lógicas (WPF) para coordenadas de tela reais (físicas)
                var screenX = (int)((this.Left + left) * dpiX);
                var screenY = (int)((this.Top + top) * dpiY);

                int physicalWidth = (int)(screenWidth * dpiX);
                int physicalHeight = (int)(screenHeight * dpiY);

                try
                {
                    using (var bitmap = new Bitmap(physicalWidth, physicalHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (var graphics = Graphics.FromImage(bitmap))
                        {
                            graphics.CopyFromScreen(screenX, screenY, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                        }

                        using (var ms = new MemoryStream())
                        {
                            bitmap.Save(ms, ImageFormat.Png);
                            Base64CapturedImage = Convert.ToBase64String(ms.ToArray());
                            System.Diagnostics.Debug.WriteLine($"[Capture] Sucesso! Base64 length: {Base64CapturedImage.Length}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Capture] Erro ao capturar: {ex.Message}");
                }
            }

            this.DialogResult = true;
            this.Close();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
