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
using System.Windows.Threading;
using WhiteboardClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;


namespace WhiteboardClient
{

    public partial class MainWindow : Window
    {
        TcpClient client;
        NetworkStream stream;
        bool drawing = false;
        Point lastPoint;
        private Brush currentBrush = Brushes.Black;
        private double lineThickness = 1;
        private int clientCount = 0;
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private bool isErasing = false;
        private Brush previousBrush;
        private double previousThickness;
        private Ellipse eraserPreview;
        private bool isSyncing = false;
        private System.Timers.Timer stateUpdateTimer;
        private bool pendingStateUpdate = false;
        private const int MaxImageSize = 20_000_000;
        private const int StateUpdateDelayMs = 2000; // 2 giây
        private const double CANVAS_MARGIN = 25;

        // Layer management
        private Canvas imageLayer;
        private Canvas drawingLayer;

        public MainWindow()
        {
            InitializeComponent();
            InitializeLayers();
            StatusLabel.Content = "Click Connect to join whiteboard";

            // Timer để gửi state update định kỳ
            stateUpdateTimer = new System.Timers.Timer(StateUpdateDelayMs);
            stateUpdateTimer.Elapsed += (s, e) =>
            {
                if (pendingStateUpdate && client?.Connected == true)
                {
                    pendingStateUpdate = false;
                    Dispatcher.Invoke(() => SendWhiteboardStateUpdate());
                }
            };
            stateUpdateTimer.Start();
        }
        public class Stroke
        {
            public double X1 { get; set; }
            public double Y1 { get; set; }
            public double X2 { get; set; }
            public double Y2 { get; set; }
            public string Color { get; set; }
            public double Thickness { get; set; }
        }

        private void InitializeLayers()
        {
            imageLayer = new Canvas();
            drawingLayer = new Canvas();

            DrawCanvas.Children.Clear();
            DrawCanvas.Children.Add(imageLayer);
            DrawCanvas.Children.Add(drawingLayer);

            Canvas.SetZIndex(imageLayer, 0);
            Canvas.SetZIndex(drawingLayer, 1);
        }

        private void ConnectToServer()
        {
            try
            {
                client = new TcpClient("127.0.0.1", 5000);
                stream = client.GetStream();

                // Reset UI state
                imageLayer.Children.Clear();
                drawingLayer.Children.Clear();

                // Start receiving messages
                StartReceiving();

                StatusLabel.Content = "Connected to server, synchronizing...";
                ConnectButton.Content = "Connected";
                ConnectButton.IsEnabled = false;
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

        private async void StartReceiving()
        {
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                while (client?.Connected == true && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    byte[] lengthBytes = new byte[4];
                    int lengthRead = 0;
                    while (lengthRead < 4)
                    {
                        int read = await stream.ReadAsync(lengthBytes, lengthRead, 4 - lengthRead, _cancellationTokenSource.Token);
                        if (read == 0) break;
                        lengthRead += read;
                    }

                    if (lengthRead < 4) break;

                    int messageLength = BitConverter.ToInt32(lengthBytes, 0);
                    if (messageLength <= 0 || messageLength > 10_000_000) break;

                    byte[] messageBytes = new byte[messageLength];
                    int totalRead = 0;
                    while (totalRead < messageLength)
                    {
                        int read = await stream.ReadAsync(messageBytes, totalRead, messageLength - totalRead, _cancellationTokenSource.Token);
                        if (read == 0) break;
                        totalRead += read;
                    }

                    if (totalRead < messageLength) break;

                    string message = Encoding.UTF8.GetString(messageBytes);
                    await Dispatcher.InvokeAsync(() => ProcessMessage(message));
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusLabel.Content = $"Connection lost: {ex.Message}";
                    ClientCountLabel.Content = "Clients: 0";
                });
            }
        }

        private void ProcessMessage(string message)
        {
            try
            {
                // Xử lý tín hiệu đồng bộ
                if (message == "SYNC_START")
                {
                    isSyncing = true;
                    StatusLabel.Content = "Receiving whiteboard state...";
                    // Clear canvas trước khi nhận state mới
                    imageLayer.Children.Clear();
                    drawingLayer.Children.Clear();
                    return;
                }
                else if (message == "SYNC_END")
                {
                    isSyncing = false;
                    StatusLabel.Content = "Whiteboard synchronized successfully";
                    return;
                }

                // Xử lý JSON messages (full_state và stroke)
                if (message.StartsWith("{"))
                {
                    try
                    {
                        JObject json = JObject.Parse(message);
                        string type = json["type"]?.ToString();

                        if (type == "full_state")
                        {
                            var strokes = json["data"]?.ToObject<List<Stroke>>();
                            if (strokes != null)
                            {
                                DrawStrokes(strokes);
                            }
                            return;
                        }
                        else if (type == "stroke")
                        {
                            var stroke = json["data"]?.ToObject<Stroke>();
                            if (stroke != null)
                            {
                                DrawStroke(stroke);
                            }
                            return;
                        }
                    }
                    catch (JsonException)
                    {
                        // Không phải JSON, tiếp tục xử lý như message thường
                    }
                }

                // Xử lý các message khác
                if (message.StartsWith("INSERT_IMAGE:"))
                {
                    string base64Image = message.Substring("INSERT_IMAGE:".Length);
                    DrawImageFromBase64(base64Image);
                    return;
                }
                else if (message.StartsWith("CLIENT_COUNT:"))
                {
                    string countStr = message.Substring("CLIENT_COUNT:".Length);
                    if (int.TryParse(countStr, out int count))
                    {
                        clientCount = count;
                        ClientCountLabel.Content = $"Clients: {clientCount}";
                        if (!isSyncing)
                        {
                            StatusLabel.Content = $"Connected - {clientCount} clients";
                        }
                    }
                    return;
                }
                else if (message == "END_SESSION")
                {
                    MessageBox.Show("Session ended by another client.", "Session Ended",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    SaveCanvasAsImage();
                    Application.Current.Shutdown();
                    return;
                }
                else if (IsDrawingData(message))
                {
                    DrawLineFromMessage(message);
                    return;
                }

                Console.WriteLine($"Unknown message: {message.Substring(0, Math.Min(50, message.Length))}");
            }
            catch (Exception ex)
            {
                StatusLabel.Content = $"Error processing message: {ex.Message}";
            }
        }

        private void DrawStrokes(List<Stroke> strokes)
        {
            foreach (var stroke in strokes)
            {
                DrawStroke(stroke);
            }
        }

        private void DrawStroke(Stroke stroke)
        {
            try
            {
                Line line = new Line
                {
                    X1 = stroke.X1,
                    Y1 = stroke.Y1,
                    X2 = stroke.X2,
                    Y2 = stroke.Y2,
                    Stroke = (Brush)new BrushConverter().ConvertFromString(stroke.Color),
                    StrokeThickness = stroke.Thickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };

                drawingLayer.Children.Add(line);
            }
            catch (Exception ex)
            {
                StatusLabel.Content = $"Error drawing stroke: {ex.Message}";
            }
        }

        private bool IsDrawingData(string message)
        {
            if (!message.Contains(",")) return false;

            string[] parts = message.Split(',');
            if (parts.Length != 6) return false;

            return double.TryParse(parts[0], out _) &&
                   double.TryParse(parts[1], out _) &&
                   double.TryParse(parts[2], out _) &&
                   double.TryParse(parts[3], out _) &&
                   double.TryParse(parts[5], out _);
        }

        private void DrawLineFromMessage(string message)
        {
            try
            {
                string[] parts = message.Split(',');
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
                        StrokeThickness = thickness,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };

                    drawingLayer.Children.Add(line);
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Content = $"Error drawing line: {ex.Message}";
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
                StrokeThickness = lineThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            drawingLayer.Children.Add(line);
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
                string message = $"{from.X},{from.Y},{to.X},{to.Y},{colorStr},{lineThickness}";

                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                byte[] lengthBytes = BitConverter.GetBytes(messageBytes.Length);

                // Gửi length + content
                stream.Write(lengthBytes, 0, 4);
                stream.Write(messageBytes, 0, messageBytes.Length);

                // Schedule state update
                pendingStateUpdate = true;
            }
            catch (Exception ex)
            {
                StatusLabel.Content = "Send error: " + ex.Message;
            }
        }

        private void SendWhiteboardStateUpdate()
        {
            try
            {
                if (stream == null || !client.Connected) return;

                byte[] imageData = CaptureWhiteboard();
                if (imageData == null) return;

                string base64Image = Convert.ToBase64String(imageData);
                string message = $"WHITEBOARD_STATE:{base64Image}";

                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                byte[] lengthBytes = BitConverter.GetBytes(messageBytes.Length);

                stream.Write(lengthBytes, 0, 4);
                stream.Write(messageBytes, 0, messageBytes.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending state update: {ex.Message}");
            }
        }

        private byte[] CaptureWhiteboard()
        {
            try
            {
                RenderTargetBitmap renderBitmap = new RenderTargetBitmap(
                    (int)DrawCanvas.ActualWidth,
                    (int)DrawCanvas.ActualHeight,
                    96d, 96d, PixelFormats.Pbgra32);
                renderBitmap.Render(DrawCanvas);

                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

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

        // Mouse Events
        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            drawing = true;
            lastPoint = e.GetPosition(DrawCanvas);
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            Point currentPoint = e.GetPosition(DrawCanvas);

            // Kiểm tra bounds
            currentPoint.X = Math.Max(CANVAS_MARGIN, Math.Min(currentPoint.X, DrawCanvas.ActualWidth - CANVAS_MARGIN));
            currentPoint.Y = Math.Max(CANVAS_MARGIN, Math.Min(currentPoint.Y, DrawCanvas.ActualHeight - CANVAS_MARGIN));

            bool isInBounds = currentPoint.X >= CANVAS_MARGIN &&
                             currentPoint.X <= DrawCanvas.ActualWidth - CANVAS_MARGIN &&
                             currentPoint.Y >= CANVAS_MARGIN &&
                             currentPoint.Y <= DrawCanvas.ActualHeight - CANVAS_MARGIN;

            if (isErasing)
            {
                if (isInBounds)
                {
                    UpdateEraserPreview(currentPoint);
                    if (drawing)
                    {
                        DrawLine(lastPoint.X, lastPoint.Y, currentPoint.X, currentPoint.Y);
                        SendLine(lastPoint, currentPoint);
                    }
                }
                else
                {
                    if (eraserPreview != null)
                    {
                        drawingLayer.Children.Remove(eraserPreview);
                        eraserPreview = null;
                    }
                    drawing = false;
                }
            }
            else if (drawing && isInBounds)
            {
                DrawLine(lastPoint.X, lastPoint.Y, currentPoint.X, currentPoint.Y);
                SendLine(lastPoint, currentPoint);
            }

            if (isInBounds)
            {
                lastPoint = currentPoint;
            }
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            drawing = false;
        }

        private void Canvas_MouseLeave(object sender, MouseEventArgs e)
        {
            drawing = false;

            if (eraserPreview != null)
            {
                drawingLayer.Children.Remove(eraserPreview);
                eraserPreview = null;
            }

            if (isErasing)
            {
                StatusLabel.Content = "Eraser outside drawing area";
            }
        }

        private void UpdateEraserPreview(Point position)
        {
            if (!isErasing) return;

            position.X = Math.Max(CANVAS_MARGIN, Math.Min(position.X, DrawCanvas.ActualWidth - CANVAS_MARGIN));
            position.Y = Math.Max(CANVAS_MARGIN, Math.Min(position.Y, DrawCanvas.ActualHeight - CANVAS_MARGIN));

            if (eraserPreview == null)
            {
                eraserPreview = new Ellipse
                {
                    Stroke = Brushes.Gray,
                    StrokeThickness = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(50, 200, 200, 200))
                };
                drawingLayer.Children.Add(eraserPreview);
            }

            double size = lineThickness;
            eraserPreview.Width = size;
            eraserPreview.Height = size;

            Canvas.SetLeft(eraserPreview, position.X - size / 2);
            Canvas.SetTop(eraserPreview, position.Y - size / 2);
            Canvas.SetZIndex(eraserPreview, 9999);
            eraserPreview.Opacity = 0.5;
        }

        // Button Events
        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (client == null || !client.Connected)
            {
                ConnectToServer();
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
                if (stream != null && client?.Connected == true)
                {
                    string disconnectMsg = "DISCONNECT";
                    byte[] messageBytes = Encoding.UTF8.GetBytes(disconnectMsg);
                    byte[] lengthBytes = BitConverter.GetBytes(messageBytes.Length);

                    stream.Write(lengthBytes, 0, 4);
                    stream.Write(messageBytes, 0, messageBytes.Length);
                }
            }
            catch { }

            _cancellationTokenSource?.Cancel();

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

        private void EndButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (stream != null && client?.Connected == true)
                {
                    string endMsg = "END_SESSION";
                    byte[] messageBytes = Encoding.UTF8.GetBytes(endMsg);
                    byte[] lengthBytes = BitConverter.GetBytes(messageBytes.Length);

                    stream.Write(lengthBytes, 0, 4);
                    stream.Write(messageBytes, 0, messageBytes.Length);
                }

                SaveCanvasAsImage();
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                StatusLabel.Content = $"Error ending session: {ex.Message}";
            }
        }

        private void EraserButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isErasing)
            {
                previousBrush = currentBrush;
                previousThickness = lineThickness;
                currentBrush = new SolidColorBrush(Colors.White);
                lineThickness = double.Parse(((ComboBoxItem)ThicknessPicker.SelectedItem).Tag.ToString()) * 10;
                EraserButton.Content = "Drawing";
                isErasing = true;

                ColorPicker.IsEnabled = false;
                ThicknessPicker.IsEnabled = true;

                UpdateEraserPreview(Mouse.GetPosition(DrawCanvas));
            }
            else
            {
                currentBrush = previousBrush;
                lineThickness = previousThickness;
                EraserButton.Content = "Eraser";
                isErasing = false;

                ColorPicker.IsEnabled = true;
                ThicknessPicker.IsEnabled = true;

                if (eraserPreview != null)
                {
                    drawingLayer.Children.Remove(eraserPreview);
                    eraserPreview = null;
                }
            }

            StatusLabel.Content = isErasing ? "Erasing" : "Drawing";
        }

        private void btnInsertImage_Click(object sender, RoutedEventArgs e)
        {
            string url = txtImageUrl.Text.Trim();
            if (!string.IsNullOrEmpty(url))
            {
                // Trường hợp có URL → tải từ internet
                try
                {
                    WebClient webClient = new WebClient();
                    byte[] imageBytes = webClient.DownloadData(url);

                    using (MemoryStream ms = new MemoryStream(imageBytes))
                    {
                        BitmapImage bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                        bitmap.Freeze();

                        DrawImage(bitmap);
                        SendImageToServer(bitmap);
                        StatusLabel.Content = "Image inserted from URL.";
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Failed to load image from URL.\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image files (*.png;*.jpeg;*.jpg;*.bmp)|*.png;*.jpeg;*.jpg;*.bmp|All files (*.*)|*.*",
                Title = "Select an image"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    if (stream == null || !client?.Connected == true)
                    {
                        MessageBox.Show("No connection to server", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var fileInfo = new FileInfo(openFileDialog.FileName);
                    if (fileInfo.Length > MaxImageSize)
                    {
                        MessageBox.Show($"Image size too large (max {MaxImageSize / 1024 / 1024}MB)",
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    // Convert image to base64
                    byte[] imageBytes = File.ReadAllBytes(openFileDialog.FileName);
                    string base64Image = Convert.ToBase64String(imageBytes);

                    // Send to server
                    string message = $"INSERT_IMAGE:{base64Image}";
                    byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                    byte[] lengthBytes = BitConverter.GetBytes(messageBytes.Length);

                    stream.Write(lengthBytes, 0, 4);
                    stream.Write(messageBytes, 0, messageBytes.Length);

                    // Draw locally
                    DrawImageFromBase64(base64Image);

                    // Schedule state update
                    pendingStateUpdate = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error inserting image: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                }
            }
        }

        private void DrawImage(BitmapImage bitmap)
        {
            double canvasWidth = DrawCanvas.ActualWidth;
            double canvasHeight = DrawCanvas.ActualHeight;

            double imgWidth = bitmap.PixelWidth;
            double imgHeight = bitmap.PixelHeight;

            double scale = 1.0;

            // Tính tỉ lệ để ảnh vừa trong canvas
            if (imgWidth > canvasWidth || imgHeight > canvasHeight)
            {
                double scaleX = canvasWidth / imgWidth;
                double scaleY = canvasHeight / imgHeight;
                scale = Math.Min(scaleX, scaleY) * 0.8; // 80% kích thước canvas
            }

            double displayWidth = imgWidth * scale;
            double displayHeight = imgHeight * scale;

            // Tạo đối tượng Image
            Image image = new Image
            {
                Source = bitmap,
                Width = displayWidth,
                Height = displayHeight
            };

            // Căn giữa ảnh trên canvas
            double left = (canvasWidth - displayWidth) / 2;
            double top = (canvasHeight - displayHeight) / 2;

            Canvas.SetLeft(image, left);
            Canvas.SetTop(image, top);

            DrawCanvas.Children.Add(image);
        }

        private void SendImageToServer(BitmapImage bitmap)
        {
            if (stream == null || !client?.Connected == true)
            {
                StatusLabel.Content = "Not connected";
                return;
            }
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    PngBitmapEncoder encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(ms);
                    byte[] imageBytes = ms.ToArray();
                    if (imageBytes.Length > MaxImageSize)
                    {
                        MessageBox.Show($"Image size too large (max {MaxImageSize / 1024 / 1024}MB)",
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    string base64Image = Convert.ToBase64String(imageBytes);
                    string message = $"INSERT_IMAGE:{base64Image}";
                    byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                    byte[] lengthBytes = BitConverter.GetBytes(messageBytes.Length);
                    stream.Write(lengthBytes, 0, 4);
                    stream.Write(messageBytes, 0, messageBytes.Length);
                    // Schedule state update
                    pendingStateUpdate = true;
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Content = $"Error sending image: {ex.Message}";
            }
        }
        private void DrawImageFromBase64(string base64String)
        {
            try
            {
                byte[] imageBytes = Convert.FromBase64String(base64String);
                using (MemoryStream ms = new MemoryStream(imageBytes))
                {
                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();

                    Image image = new Image
                    {
                        Source = bitmap,
                        Width = 200,
                        Height = 200,
                        Stretch = Stretch.Uniform
                    };

                    // Center the image
                    double left = (DrawCanvas.ActualWidth - image.Width) / 2;
                    double top = (DrawCanvas.ActualHeight - image.Height) / 2;

                    Canvas.SetLeft(image, left);
                    Canvas.SetTop(image, top);

                    imageLayer.Children.Add(image);
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Content = $"Error drawing image: {ex.Message}";
            }
        }

        private void SaveCanvasAsImage()
        {
            try
            {
                RenderTargetBitmap renderBitmap = new RenderTargetBitmap(
                    (int)DrawCanvas.ActualWidth,
                    (int)DrawCanvas.ActualHeight,
                    96d, 96d, PixelFormats.Pbgra32);

                renderBitmap.Render(DrawCanvas);

                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

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

        // UI Event Handlers
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
                        lineThickness = thickness * 10;
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
            _cancellationTokenSource?.Dispose();
            stateUpdateTimer?.Dispose();
            stream?.Dispose();
            client?.Dispose();
            base.OnClosing(e);
        }

        private void txtImageUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Placeholder for future functionality
        }
    }
}