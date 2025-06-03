using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.ConstrainedExecution;

namespace WhiteboardServer
{
    internal class Program
    {
        static List<TcpClient> clients = new List<TcpClient>();
        static readonly object clientLock = new object();
        static byte[] currentWhiteboardState = null;

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
                        Console.WriteLine($"Total clients: {clients.Count}");
                        // Send client count update to all clients
                        BroadcastClientCount();
                    }

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

        static void SendCurrentState(TcpClient client)
        {
            try
            {
                NetworkStream stream = client.GetStream();

                // Gửi signal bắt đầu sync
                string startSync = "START_SYNC_IMAGE";
                byte[] startData = Encoding.UTF8.GetBytes(startSync);
                stream.Write(startData, 0, startData.Length);
                Thread.Sleep(100); // Đợi client sẵn sàng

                if (currentWhiteboardState != null)
                {
                    // Gửi kích thước của ảnh
                    byte[] sizeBytes = BitConverter.GetBytes(currentWhiteboardState.Length);
                    stream.Write(sizeBytes, 0, sizeBytes.Length);
                    Thread.Sleep(100);

                    // Gửi dữ liệu ảnh
                    stream.Write(currentWhiteboardState, 0, currentWhiteboardState.Length);
                }

                // Gửi signal kết thúc sync
                string endSync = "END_SYNC";
                byte[] endData = Encoding.UTF8.GetBytes(endSync);
                stream.Write(endData, 0, endData.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending current state: {ex.Message}");
            }
        }

        static void HandleClient(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[4096];

            try
            {
                SendDrawingHistory(client);

                while (client.Connected)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break; // client disconnected

                    string msg = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    if(msg == "DISCONNECT")
                    {
                        Console.WriteLine($"Client requested disconnect: {client.Client.RemoteEndPoint}");
                        break;
                    }
                    else if(msg == "END_SESSION")
                    {
                        Console.WriteLine($"Client ended session: {client.Client.RemoteEndPoint}");
                        // Broadcast END_SESSION to all other clients
                        BroadcastMessage("END_SESSION", client);
                        break;
                    }
                    else
                    {
                        // Regular drawing data - broadcast to other clients
                        Broadcast(buffer, bytesRead, client);
                    }
                }
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

        static void SendDrawingHistory(TcpClient client)
        {
            try
            {
                NetworkStream stream = client.GetStream();

                // Gửi signal bắt đầu sync
                string startSync = "START_SYNC";
                byte[] startData = Encoding.UTF8.GetBytes(startSync);
                stream.Write(startData, 0, startData.Length);

                // Gửi signal kết thúc sync
                string endSync = "END_SYNC";
                byte[] endData = Encoding.UTF8.GetBytes(endSync);
                stream.Write(endData, 0, endData.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending history: {ex.Message}");
            }
        }

        static void BroadcastClientCount()
        {
            string countMsg = $"CLIENT_COUNT:{clients.Count}";
            byte[] countData = Encoding.UTF8.GetBytes(countMsg);

            foreach (var client in clients.ToArray()) // ToArray to avoid modification during iteration
            {
                if (client.Connected)
                {
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        stream.Write(countData, 0, countData.Length);
                    }
                    catch
                    {
                        // Remove disconnected client
                        lock (clientLock)
                        {
                            clients.Remove(client);
                        }
                    }
                }
            }
        }

        static void BroadcastMessage(string message, TcpClient sender)
        {
            byte[] data = Encoding.UTF8.GetBytes(message);

            lock (clientLock)
            {
                foreach (var client in clients.ToArray())
                {
                    if (client != sender && client.Connected)
                    {
                        try
                        {
                            NetworkStream stream = client.GetStream();
                            stream.Write(data, 0, data.Length);
                        }
                        catch
                        {
                            // Remove disconnected client
                            clients.Remove(client);
                        }
                    }
                }
            }
        }

        static void Broadcast(byte[] data, int length, TcpClient sender)
        {
            lock (clientLock)
            {
                foreach (var client in clients.ToArray())
                {
                    if (client != sender && client.Connected)
                    {
                        try
                        {
                            NetworkStream stream = client.GetStream();
                            stream.Write(data, 0, length);
                        }
                        catch
                        {
                            // Remove disconnected client
                            clients.Remove(client);
                        }
                    }
                }
            }
        }

        static void Relay(TcpClient from, TcpClient to)
        {
            try
            {
                NetworkStream fromStream = from.GetStream();
            NetworkStream toStream = to.GetStream();
            byte[] buffer = new byte[4096];

            while (true)
            {
                    int bytesRead = fromStream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    // Kiểm tra to có còn kết nối
                    if (to.Connected)
                    {
                        toStream.Write(buffer, 0, bytesRead);
                    }
                }
            }

            catch (IOException ex)
            {
                Console.WriteLine("IO Exception: " + ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception: " + ex.Message);
            }
            finally
            {
                from.Close();
                to.Close();
                Console.WriteLine("Một client đã ngắt kết nối.");
            }
        }
    }
}
