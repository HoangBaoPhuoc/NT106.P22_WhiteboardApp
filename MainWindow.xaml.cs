using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Threading;
using System.IO;

namespace WhiteboardClient
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    /// 

    public partial class MainWindow : Window
    {
        TcpClient client;
        NetworkStream stream;
        bool drawing = false;
        Point lastPoint;
        private Thread receiveThread;
        private Brush currentBrush = Brushes.Black;
        private double lineThickness = 1;
        private int clientCount = 0;
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private bool isErasing = false;
        private Brush previousBrush;
        private double previousThickness;
        private Ellipse eraserPreview;
        private bool isSyncing = false;

        public MainWindow()
        {
            InitializeComponent();
            StatusLabel.Content = "Click Connect to join whiteboard";
        }

        private byte[] CaptureWhiteboard()
        {
            try
            {
                // Tạo bitmap từ canvas
                RenderTargetBitmap renderBitmap = new RenderTargetBitmap(
                    (int)DrawCanvas.ActualWidth,
                    (int)DrawCanvas.ActualHeight,
                    96d, 96d, PixelFormats.Pbgra32);
                renderBitmap.Render(DrawCanvas);

                // Chuyển thành PNG
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

                // Chuyển thành byte array
                using (MemoryStream ms = new MemoryStream())
                {
                    encoder.Save(ms);
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error capturing whiteboard: {ex.Message}");
                return null;
            }
        }

        private void ConnectToServer()
        {
            try {
             // Thay "127.0.0.1" bằng địa chỉ IP thực của máy chạy server
            client = new TcpClient("127.0.0.1", 5000);
            stream = client.GetStream();
            StartReceiving();

                StatusLabel.Content = "Connected to server";
                ConnectButton.Content = "Connected";
                DisconnectButton.IsEnabled = true;
                EndButton.IsEnabled = true;
                EraserButton.IsEnabled = true;
                ColorPicker.IsEnabled = true; 
                ThicknessPicker.IsEnabled = true;
                DrawCanvas.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StatusLabel.Content = $"Connection failed: {ex.Message}";
                ConnectButton.IsEnabled = true;
                ConnectButton.Content = "Connect";
                DisconnectButton.IsEnabled = false;
                EraserButton.IsEnabled = false;
                EndButton.IsEnabled = false;
                DrawCanvas.IsEnabled = false;
            }
        }

        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            DisconnectFromServer();
        }

        private void DisconnectFromServer()
        {
            try
            {
                // Send disconnect message to server
                if (stream != null && client?.Connected == true)
                {
                    string disconnectMsg = "DISCONNECT";
                    byte[] buffer = Encoding.UTF8.GetBytes(disconnectMsg);
                    stream.Write(buffer, 0, buffer.Length);
                }
            }
            catch { }
            _cancellationTokenSource.Cancel();

            if (stream != null)
            {
                stream.Close();
                stream = null;
            }

            if (client != null)
            {
                client.Close();
                client = null;
            }

            StatusLabel.Content = "Disconnected";
            ClientCountLabel.Content = "Clients: 0";
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Connect";
            DisconnectButton.IsEnabled = false;
            EraserButton.IsEnabled = false;
            DrawCanvas.IsEnabled = false;
            if (isErasing)
            {
                isErasing = false;
                currentBrush = previousBrush;
                lineThickness = previousThickness;
                EraserButton.Content = "Eraser";
            }

        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (client == null || !client.Connected)
            {
                ConnectToServer();
            }
        }

        private void EndButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Send END message to server to notify all clients
                if (stream != null && client?.Connected == true)
                {
                    string endMsg = "END_SESSION";
                    byte[] buffer = Encoding.UTF8.GetBytes(endMsg);
                    stream.Write(buffer, 0, buffer.Length);
                }

                // Save canvas as image
                SaveCanvasAsImage();

                // Close application
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                StatusLabel.Content = $"Error ending session: {ex.Message}";
            }
        }

        private void SaveCanvasAsImage()
        {
            try
            {
                // Create a render target bitmap
                RenderTargetBitmap renderBitmap = new RenderTargetBitmap(
                    (int)DrawCanvas.ActualWidth,
                    (int)DrawCanvas.ActualHeight,
                    96d, 96d, PixelFormats.Pbgra32);

                // Render the canvas
                renderBitmap.Render(DrawCanvas);

                // Create PNG encoder
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

                // Save to file
                string fileName = $"Whiteboard_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string filePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

                using (FileStream file = File.Create(filePath))
                {
                    encoder.Save(file);
                }

                MessageBox.Show($"Whiteboard saved as: {fileName}", "Save Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving image: {ex.Message}", "Save Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DrawLine(double x1, double y1, double x2, double y2)
        {
            Line line = new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = currentBrush,
                StrokeThickness = lineThickness
            };
            DrawCanvas.Children.Add(line);
        }

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            drawing = true;
            lastPoint = e.GetPosition(DrawCanvas);
        }

        private void Canvas_MouseLeave(object sender, MouseEventArgs e)
        {
            if (eraserPreview != null)
            {
                DrawCanvas.Children.Remove(eraserPreview);
                eraserPreview = null;
            }
        }


        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            
            Point currentPoint = e.GetPosition(DrawCanvas);

            if (isErasing)
            {
                UpdateEraserPreview(currentPoint);
            }

            if (!drawing) return;
            DrawLine(lastPoint.X, lastPoint.Y, currentPoint.X, currentPoint.Y);
            SendLine(lastPoint, currentPoint);
            lastPoint = currentPoint;
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            drawing = false;
        }

        private void StartReceiving()
        {
            _cancellationTokenSource = new CancellationTokenSource(); // Reset token source

            receiveThread = new Thread(() =>
            {
                byte[] buffer = new byte[4096];

                while (client?.Connected == true && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        int bytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead == 0) break;

                        string msg = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessMessage(msg);
                        });
                    }
                    catch
                    {
                        break;
                    }
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusLabel.Content = "Connection lost";
                    ClientCountLabel.Content = "Clients: 0";
                });
            });

            receiveThread.IsBackground = true;
            receiveThread.Start();
        }

        private void ProcessMessage(string msg)
        {
            try
            {
                if (msg == "REQUEST_STATE")
                {
                    byte[] imageData = CaptureWhiteboard();
                    if (imageData != null)
                    {
                        // Gửi kích thước trước
                        byte[] sizeBytes = BitConverter.GetBytes(imageData.Length);
                        stream.Write(sizeBytes, 0, sizeBytes.Length);
                        Thread.Sleep(100);

                        // Gửi dữ liệu ảnh
                        stream.Write(imageData, 0, imageData.Length);
                    }
                    return;
                }
                // ..
                if (msg == "START_SYNC")
                {
                    isSyncing = true;
                    DrawCanvas.Children.Clear();
                    ReceiveWhiteboardImage();
                    return;
                }

                if (msg == "END_SYNC")
                {
                    isSyncing = false;
                    return;
                }
                if (msg.StartsWith("CLIENT_COUNT:"))
                {
                    string countStr = msg.Substring("CLIENT_COUNT:".Length);
                    if (int.TryParse(countStr, out int count))
                    {
                        clientCount = count;
                        ClientCountLabel.Content = $"Clients: {clientCount}";
                        StatusLabel.Content = isSyncing ?
                                           "Synchronizing whiteboard..." :
                                           $"Client count updated: {clientCount}";
                    }
                }
                else if (msg == "END_SESSION")
                {
                    // Another client ended the session
                    MessageBox.Show("Session ended by another client.", "Session Ended",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    SaveCanvasAsImage();
                    Application.Current.Shutdown();
                }
                else
                {
                    // Drawing data
                    DrawLineFromMessage(msg);
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Content = $"Error processing message: {ex.Message}";
            }
        }

        private void ReceiveWhiteboardImage()
        {
            try
            {
                // Nhận kích thước ảnh
                byte[] sizeBytes = new byte[4];
                stream.Read(sizeBytes, 0, 4);
                int imageSize = BitConverter.ToInt32(sizeBytes, 0);

                // Nhận dữ liệu ảnh
                byte[] imageData = new byte[imageSize];
                int bytesRead = 0;
                while (bytesRead < imageSize)
                {
                    bytesRead += stream.Read(imageData, bytesRead, imageSize - bytesRead);
                }

                // Chuyển đổi thành ảnh và hiển thị
                using (MemoryStream ms = new MemoryStream(imageData))
                {
                    BitmapImage bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();

                    Image image = new Image
                    {
                        Source = bmp,
                        Stretch = Stretch.None
                    };

                    DrawCanvas.Children.Add(image);
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Content = $"Error receiving image: {ex.Message}";
            }
        }

        static void UpdateCurrentState(TcpClient sender)
        {
            try
            {
                NetworkStream stream = sender.GetStream();
                // Yêu cầu client gửi trạng thái hiện tại
                string requestState = "REQUEST_STATE";
                byte[] requestData = Encoding.UTF8.GetBytes(requestState);
                stream.Write(requestData, 0, requestData.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error requesting state: {ex.Message}");
            }
        }

        private void DrawLineFromMessage(string msg)
        {
            try
            {
                string[] parts = msg.Split(',');

                if (parts.Length == 6)
                {
                    double x1 = double.Parse(parts[0]);
                    double y1 = double.Parse(parts[1]);
                    double x2 = double.Parse(parts[2]);
                    double y2 = double.Parse(parts[3]);
                    string colorStr = parts[4];
                    double thickness = double.Parse(parts[5]);

                    Brush brush = (Brush)new BrushConverter().ConvertFromString(colorStr);

                    Line line = new Line
                    {
                        X1 = x1,
                        Y1 = y1,
                        X2 = x2,
                        Y2 = y2,
                        Stroke = brush,
                        StrokeThickness = thickness
                    };

                    DrawCanvas.Children.Add(line);
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Content = $"Error drawing line: {ex.Message}";
            }
        }

        private void EraserButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isErasing)
            {
                // Switch to eraser
                previousBrush = currentBrush;
                previousThickness = lineThickness;
                currentBrush = new SolidColorBrush(Colors.White);
                lineThickness = double.Parse(((ComboBoxItem)ThicknessPicker.SelectedItem).Tag.ToString()) * 10; // Nhân 5 để tạo kích thước tẩy lớn hơn
                EraserButton.Content = "Drawing";
                isErasing = true;

                // Disable color và thickness pickers khi đang tẩy
                ColorPicker.IsEnabled = false;
                ThicknessPicker.IsEnabled = true;

                UpdateEraserPreview(Mouse.GetPosition(DrawCanvas));

            }
            else
            {
                // Switch back to drawing
                currentBrush = previousBrush;
                lineThickness = previousThickness;
                EraserButton.Content = "Eraser";
                isErasing = false;

                // Enable lại color và thickness pickers
                ColorPicker.IsEnabled = true;
                ThicknessPicker.IsEnabled = true;

                if (eraserPreview != null)
                {
                    DrawCanvas.Children.Remove(eraserPreview);
                    eraserPreview = null;
                }
            }

            StatusLabel.Content = isErasing ? "Erasing" : "Drawing";
        }

        private void UpdateEraserPreview(Point position)
        {
            if (isErasing)
            {
                if (eraserPreview == null)
                {
                    // Tạo preview nếu chưa tồn tại
                    eraserPreview = new Ellipse
                    {
                        Stroke = Brushes.Gray,
                        StrokeThickness = 1,
                        Fill = new SolidColorBrush(Color.FromArgb(50, 200, 200, 200))
                    };
                    DrawCanvas.Children.Add(eraserPreview);
                }

                // Cập nhật kích thước và vị trí
                double size = lineThickness;
                eraserPreview.Width = size;
                eraserPreview.Height = size;

                // Đặt vị trí (căn giữa với con trỏ chuột)
                Canvas.SetLeft(eraserPreview, position.X - size / 2);
                Canvas.SetTop(eraserPreview, position.Y - size / 2);
                Canvas.SetZIndex(eraserPreview, 9999); // Đảm bảo hiển thị trên cùng
            }
            else if (eraserPreview != null)
            {
                // Xóa preview khi không ở chế độ tẩy
                DrawCanvas.Children.Remove(eraserPreview);
                eraserPreview = null;
            }
        }


        private void SendLine(Point from, Point to)
        {
            if (stream == null || !client.Connected)
            {
                StatusLabel.Content = "Not connected";
                return;
            }

            try
            {
                string colorStr = ((SolidColorBrush)currentBrush).Color.ToString();
                string msg = $"{from.X},{from.Y},{to.X},{to.Y},{colorStr},{lineThickness}";
                byte[] buffer = Encoding.UTF8.GetBytes(msg);
                stream.Write(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                StatusLabel.Content = "Send error: " + ex.Message;
            }
        }

        private void ColorPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ColorPicker?.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                try
                {
                    string color = item.Tag.ToString();
                    if (!string.IsNullOrEmpty(color))
                    {
                        currentBrush = (Brush)new BrushConverter().ConvertFromString(color);
                        if (StatusLabel != null)
                        {
                            StatusLabel.Content = $"Color: {item.Content}";
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (StatusLabel != null)
                    {
                        StatusLabel.Content = $"Color error: {ex.Message}";
                    }
                }
            }
        }


        private void ThicknessPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThicknessPicker?.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                try
                {
                    double thickness = double.Parse(item.Tag.ToString());
                    if (isErasing)
                    {
                        lineThickness = thickness * 10; // Kích thước tẩy lớn hơn
                    }
                    else
                    {
                        lineThickness = thickness;
                    }
                    if (StatusLabel != null)
                    {
                        StatusLabel.Content = $"Thickness: {lineThickness}";
                    }

                    if (isErasing && eraserPreview != null)
                    {
                        UpdateEraserPreview(Mouse.GetPosition(DrawCanvas));
                    }
                }
                catch (Exception ex)
                {
                    if (StatusLabel != null)
                    {
                        StatusLabel.Content = $"Thickness error: {ex.Message}";
                    }
                }
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            DisconnectFromServer();
            base.OnClosing(e);
        }
    }
}