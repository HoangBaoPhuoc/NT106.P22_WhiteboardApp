using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Net.Mail;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Configuration;

namespace WhiteboardServer
{
    internal class Program
    {
        static List<TcpClient> clients = new List<TcpClient>();
        static readonly object clientLock = new object();
        static byte[] currentWhiteboardState = null;
        static readonly object stateLock = new object();
        static List<string> drawingHistory = new List<string>(); // Lưu lịch sử các nét vẽ
        static readonly object historyLock = new object();
        static List<string> imageHistory = new List<string>(); // Lưu lịch sử các hình ảnh
        static readonly object imageHistoryLock = new object();

        static void Main()
        {
            TcpListener server = new TcpListener(IPAddress.Any, 5000);
            server.Start();
            Console.WriteLine("Server started on port 5000...");
            Console.WriteLine("Waiting for clients to connect...");

            while (true)
            {
                try
                {
                    TcpClient client = server.AcceptTcpClient();

                    lock (clientLock)
                    {
                        clients.Add(client);
                        Console.WriteLine($"Client connected: {client.Client.RemoteEndPoint}");
                        if (clients.Count > 5)
                        {
                            SendAlertEmail(clients.Count);
                        }
                        Console.WriteLine($"Total clients: {clients.Count}");
                        BroadcastClientCount();
                    }

                    // Khởi tạo thread xử lý client với sync đầy đủ
                    Thread thread = new Thread(() => HandleClient(client));
                    thread.IsBackground = true;
                    thread.Start();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error accepting client: {ex.Message}");
                }
            }
        }

        static void HandleClient(TcpClient client)
        {
            NetworkStream stream = client.GetStream();

            try
            {
                // Đảm bảo client mới nhận được toàn bộ state hiện tại
                SendCompleteStateToNewClient(client);

                // Bắt đầu lắng nghe messages từ client
                HandleClientMessages(client, stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Client error: {ex.Message}");
            }
            finally
            {
                lock (clientLock)
                {
                    clients.Remove(client);
                    Console.WriteLine($"Client disconnected: {client.Client.RemoteEndPoint}");
                    Console.WriteLine($"Remaining clients: {clients.Count}");
                    BroadcastClientCount();
                }
                try
                {
                    client.Close();
                }
                catch { }
            }
        }

        static void SendCompleteStateToNewClient(TcpClient client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                Console.WriteLine("Sending complete state to new client...");

                // Bước 1: Gửi tín hiệu bắt đầu đồng bộ
                SendMessage(client, "SYNC_START");
                Thread.Sleep(100);

                // Bước 2: Gửi tất cả hình ảnh trong lịch sử
                List<string> imageSnapshot;
                lock (imageHistoryLock)
                {
                    imageSnapshot = new List<string>(imageHistory);
                }

                foreach (var imageData in imageSnapshot)
                {
                    SendMessage(client, $"INSERT_IMAGE:{imageData}");
                    Thread.Sleep(50); // Đảm bảo client xử lý từng hình ảnh
                }

                // Bước 3: Gửi tất cả strokes trong lịch sử
                SendDrawingHistoryToClient(client);

                // Bước 4: Gửi tín hiệu kết thúc đồng bộ
                SendMessage(client, "SYNC_END");

                Console.WriteLine("Complete state sent to new client successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending complete state: {ex.Message}");
            }
        }

        static void SendDrawingHistoryToClient(TcpClient client)
        {
            try
            {
                List<string> historySnapshot;
                lock (historyLock)
                {
                    historySnapshot = new List<string>(drawingHistory);
                }

                if (historySnapshot.Count == 0)
                {
                    Console.WriteLine("No drawing history to send");
                    return;
                }

                Console.WriteLine($"Sending {historySnapshot.Count} drawing commands to client");

                // Chuyển thành danh sách strokes với format JSON
                var strokes = new List<object>();

                foreach (var msg in historySnapshot)
                {
                    var parts = msg.Split(',');
                    if (parts.Length != 6) continue;

                    if (!double.TryParse(parts[0], out double x1)) continue;
                    if (!double.TryParse(parts[1], out double y1)) continue;
                    if (!double.TryParse(parts[2], out double x2)) continue;
                    if (!double.TryParse(parts[3], out double y2)) continue;
                    string color = parts[4];
                    if (!double.TryParse(parts[5], out double thickness)) continue;

                    strokes.Add(new
                    {
                        X1 = x1,
                        Y1 = y1,
                        X2 = x2,
                        Y2 = y2,
                        Color = color,
                        Thickness = thickness
                    });
                }

                var fullState = new
                {
                    type = "full_state",
                    data = strokes
                };

                string json = JsonConvert.SerializeObject(fullState);
                SendMessage(client, json);

                Console.WriteLine($"Drawing history sent to client ({json.Length} bytes)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending drawing history: {ex.Message}");
            }
        }

        static void SendMessage(TcpClient client, string message)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                byte[] lengthBytes = BitConverter.GetBytes(messageBytes.Length);

                stream.Write(lengthBytes, 0, 4);
                stream.Write(messageBytes, 0, messageBytes.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending message to client: {ex.Message}");
            }
        }

        static void HandleClientMessages(TcpClient client, NetworkStream stream)
        {
            byte[] lengthBuffer = new byte[4];

            while (client.Connected)
            {
                try
                {
                    // Đọc length của message trước
                    int lengthRead = 0;
                    while (lengthRead < 4)
                    {
                        int read = stream.Read(lengthBuffer, lengthRead, 4 - lengthRead);
                        if (read == 0) return; // Client disconnected
                        lengthRead += read;
                    }

                    int messageLength = BitConverter.ToInt32(lengthBuffer, 0);

                    // Kiểm tra message length hợp lệ
                    if (messageLength <= 0 || messageLength > 50_000_000) // 50MB max
                    {
                        Console.WriteLine($"Invalid message length: {messageLength}");
                        continue;
                    }

                    // Đọc message content
                    byte[] messageBuffer = new byte[messageLength];
                    int totalRead = 0;
                    while (totalRead < messageLength)
                    {
                        int read = stream.Read(messageBuffer, totalRead, messageLength - totalRead);
                        if (read == 0) return; // Client disconnected
                        totalRead += read;
                    }

                    string message = Encoding.UTF8.GetString(messageBuffer);
                    ProcessClientMessage(client, message, messageBuffer, messageLength);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading from client: {ex.Message}");
                    break;
                }
            }
        }

        static void ProcessClientMessage(TcpClient client, string message, byte[] messageBytes, int length)
        {
            try
            {
                if (message == "DISCONNECT")
                {
                    Console.WriteLine($"Client requested disconnect: {client.Client.RemoteEndPoint}");
                    return;
                }
                else if (message == "END_SESSION")
                {
                    Console.WriteLine($"Client ended session: {client.Client.RemoteEndPoint}");
                    BroadcastMessage("END_SESSION", client);
                    return;
                }
                else if (message.StartsWith("INSERT_IMAGE:"))
                {
                    Console.WriteLine("[Server] Processing INSERT_IMAGE message");

                    // Lưu vào lịch sử hình ảnh
                    string imageData = message.Substring("INSERT_IMAGE:".Length);
                    lock (imageHistoryLock)
                    {
                        imageHistory.Add(imageData);

                        // Giới hạn số lượng hình ảnh để tránh memory leak
                        if (imageHistory.Count > 50)
                        {
                            imageHistory.RemoveAt(0);
                        }
                    }

                    // Broadcast image message to other clients
                    BroadcastWithLength(messageBytes, client);
                    Console.WriteLine("[Server] Image broadcasted to other clients");
                }
                else if (message.StartsWith("WHITEBOARD_STATE:"))
                {
                    // Client gửi toàn bộ state của whiteboard (khi có thay đổi lớn)
                    ProcessWhiteboardStateUpdate(message);
                }
                else if (IsDrawingData(message))
                {
                    // Xử lý dữ liệu vẽ (drawing strokes)
                    ProcessDrawingData(client, message, messageBytes);
                }
                else
                {
                    Console.WriteLine($"[Server] Unknown message format: {message.Substring(0, Math.Min(100, message.Length))}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing message: {ex.Message}");
            }
        }

        static bool IsDrawingData(string message)
        {
            // Kiểm tra format: x1,y1,x2,y2,color,thickness
            if (!message.Contains(",")) return false;

            string[] parts = message.Split(',');
            if (parts.Length != 6) return false;

            // Validate các tham số số học
            return double.TryParse(parts[0], out _) &&
                   double.TryParse(parts[1], out _) &&
                   double.TryParse(parts[2], out _) &&
                   double.TryParse(parts[3], out _) &&
                   double.TryParse(parts[5], out _);
        }

        static void ProcessDrawingData(TcpClient client, string message, byte[] messageBytes)
        {
            Console.WriteLine($"[Server] Processing drawing data: {message}");

            // Lưu vào lịch sử
            lock (historyLock)
            {
                drawingHistory.Add(message);

                // Giới hạn history size để tránh memory leak
                if (drawingHistory.Count > 10000)
                {
                    drawingHistory.RemoveRange(0, 1000); // Xóa 1000 entries cũ nhất
                }
            }

            // Broadcast tới các client khác
            BroadcastWithLength(messageBytes, client);
            Console.WriteLine("[Server] Drawing data broadcasted");
        }

        static void ProcessWhiteboardStateUpdate(string message)
        {
            try
            {
                // Format: WHITEBOARD_STATE:base64_image_data
                string base64Data = message.Substring("WHITEBOARD_STATE:".Length);
                byte[] imageData = Convert.FromBase64String(base64Data);

                lock (stateLock)
                {
                    currentWhiteboardState = imageData;
                    Console.WriteLine($"[Server] Updated whiteboard state ({imageData.Length} bytes)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating whiteboard state: {ex.Message}");
            }
        }

        static void BroadcastClientCount()
        {
            string countMsg = $"CLIENT_COUNT:{clients.Count}";
            BroadcastMessage(countMsg, null);
        }

        static void BroadcastMessage(string message, TcpClient sender)
        {
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            BroadcastWithLength(messageBytes, sender);
        }

        static void BroadcastWithLength(byte[] messageBytes, TcpClient sender)
        {
            byte[] lengthBytes = BitConverter.GetBytes(messageBytes.Length);

            List<TcpClient> clientSnapshot;
            lock (clientLock)
            {
                clientSnapshot = new List<TcpClient>(clients);
            }

            foreach (var client in clientSnapshot)
            {
                if (client != sender && client.Connected)
                {
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        // Gửi length trước, sau đó gửi content
                        stream.Write(lengthBytes, 0, 4);
                        stream.Write(messageBytes, 0, messageBytes.Length);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error broadcasting to client: {ex.Message}");
                        // Remove disconnected client
                        lock (clientLock)
                        {
                            clients.Remove(client);
                        }
                    }
                }
            }
        }

        static void SendAlertEmail(int count)
        {
            try
            {
                string fromEmail = ConfigurationManager.AppSettings["EmailFrom"];
                string password = ConfigurationManager.AppSettings["EmailPassword"];
                string smtpServer = ConfigurationManager.AppSettings["SmtpServer"];
                int smtpPort = int.Parse(ConfigurationManager.AppSettings["SmtpPort"]);

                var from = new MailAddress(fromEmail, "Whiteboard Server");

                var message = new MailMessage
                {
                    From = from,
                    Subject = "⚠️ Cảnh báo: Số lượng client vượt giới hạn!",
                    Body = $"Có {count} client đang kết nối đến server."
                };

                // Danh sách người nhận
                string[] recipients = ConfigurationManager.AppSettings["EmailTo"].Split(';');
                foreach (string recipient in recipients)
                {
                    message.To.Add(recipient.Trim());
                }

                var smtp = new SmtpClient(smtpServer, smtpPort)
                {
                    Credentials = new NetworkCredential(fromEmail, password),
                    EnableSsl = true
                };

                smtp.Send(message);
                Console.WriteLine("[+] Cảnh báo gửi email thành công.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[-] Lỗi gửi email: " + ex.Message);
            }
        }
    }
}