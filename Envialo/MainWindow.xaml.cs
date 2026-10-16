using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Envialo
{
    public partial class MainWindow : Window
    {
        private const int FilePort = 9000;
        private const int DiscoveryPort = 9001;
        private const string DiscoveryTag = "ENVIALO_HELLO:";

        private const byte ResponseAccept = 1;
        private const byte ResponseReject = 0;

        private static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(15);

        private string localIP;
        private string deviceName;
        private string logFilePath;
        private string deviceNameFilePath;

        private Dictionary<string, string> devices =
            new Dictionary<string, string>();

        private Dictionary<string, DateTime> lastSeen =
            new Dictionary<string, DateTime>();

        private UdpClient discoveryListener;
        private UdpClient discoveryBroadcaster;

        private readonly object logLock = new object();

        // Visual transfer queue - bound directly to the QueueList ListBox.
        private ObservableCollection<QueueItem> transferQueue =
            new ObservableCollection<QueueItem>();

        public MainWindow()
        {
            InitializeComponent();

            string appFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Envíalo");

            Directory.CreateDirectory(appFolder);

            logFilePath = Path.Combine(appFolder, "history.log");
            deviceNameFilePath = Path.Combine(appFolder, "device_name.txt");

            deviceName = LoadOrCreateDeviceName();
            localIP = GetLocalIP();

            DeviceNameBox.Text = deviceName;
            QueueList.ItemsSource = transferQueue;

            ModeBox.SelectedIndex = 0;

            StatusText.Text =
                "This PC: " +
                deviceName +
                " (" +
                localIP +
                ")";

            StartFileServer();
            StartDiscoveryListener();
            StartDiscoveryBroadcaster();
            StartDeviceCleanupTimer();
        }

        // =========================
        // DEVICE NAME
        // =========================

        private string LoadOrCreateDeviceName()
        {
            if (File.Exists(deviceNameFilePath))
            {
                string saved = File.ReadAllText(deviceNameFilePath).Trim();

                if (!string.IsNullOrWhiteSpace(saved))
                    return saved;
            }

            return Environment.MachineName;
        }

        private void SaveDeviceName_Click(object sender, RoutedEventArgs e)
        {
            string newName = DeviceNameBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(newName))
            {
                MessageBox.Show("Don't forget to type a name! 😊");
                return;
            }

            deviceName = newName;

            try
            {
                File.WriteAllText(deviceNameFilePath, deviceName);
            }
            catch
            {
                // Non-critical if this fails to save to disk - the name
                // still applies for the rest of this session either way.
            }

            StatusText.Text =
                "This PC: " + deviceName + " (" + localIP + ")";

            MessageBox.Show(
                $"Nice! Your name is now \"{deviceName}\" 🎉\n\nOther devices will see it within a few seconds.",
                "Envíalo");
        }

        // =========================
        // TRANSFER HISTORY LOG
        // =========================

        private void LogTransfer(string direction, string otherIP, string fileName, long sizeBytes, string status)
        {
            try
            {
                string line =
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {direction} | {otherIP} | {fileName} | {FormatFileSize(sizeBytes)} | {status}";

                lock (logLock)
                {
                    File.AppendAllText(logFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
            }
        }

        private void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            HistoryWindow historyWindow = new HistoryWindow();
            historyWindow.Owner = this;
            historyWindow.ShowDialog();
        }

        // =========================
        // AUTO-DISCOVERY (UDP BROADCAST)
        // =========================

        private void AddOrRefreshDevice(string ip, string name)
        {
            bool isNew = !devices.ContainsKey(ip);

            devices[ip] = name;
            lastSeen[ip] = DateTime.Now;

            if (isNew)
            {
                Dispatcher.Invoke(RefreshDeviceList);
            }
        }

        private void RefreshDeviceList()
        {
            string previouslySelected =
                DeviceList.SelectedItem?.ToString();

            DeviceList.Items.Clear();

            foreach (var d in devices)
            {
                string entry = $"{d.Value} - {d.Key}";

                DeviceList.Items.Add(entry);

                if (entry == previouslySelected)
                {
                    DeviceList.SelectedItem = entry;
                }
            }

            DiscoveryStatusText.Text =
                devices.Count == 0
                ? "Looking for classmates... \ud83d\udc40"
                : $"{devices.Count} device(s) nearby \ud83d\udc4b";
        }

        private void StartDiscoveryBroadcaster()
        {
            discoveryBroadcaster = new UdpClient();
            discoveryBroadcaster.EnableBroadcast = true;

            Thread broadcastThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        string message =
                            DiscoveryTag + deviceName + "|" + localIP;

                        byte[] data =
                            Encoding.UTF8.GetBytes(message);

                        IPEndPoint broadcastEndpoint =
                            new IPEndPoint(
                                IPAddress.Broadcast,
                                DiscoveryPort);

                        discoveryBroadcaster.Send(
                            data,
                            data.Length,
                            broadcastEndpoint);
                    }
                    catch
                    {
                    }

                    Thread.Sleep(3000);
                }
            });

            broadcastThread.IsBackground = true;
            broadcastThread.Start();
        }

        private void StartDiscoveryListener()
        {
            Thread listenThread = new Thread(() =>
            {
                try
                {
                    discoveryListener =
                        new UdpClient(DiscoveryPort);

                    discoveryListener.EnableBroadcast = true;

                    while (true)
                    {
                        IPEndPoint remoteEndpoint =
                            new IPEndPoint(IPAddress.Any, 0);

                        byte[] data =
                            discoveryListener.Receive(ref remoteEndpoint);

                        string message =
                            Encoding.UTF8.GetString(data);

                        if (!message.StartsWith(DiscoveryTag))
                            continue;

                        string payload =
                            message.Substring(DiscoveryTag.Length);

                        string[] parts = payload.Split('|');

                        if (parts.Length != 2)
                            continue;

                        string senderName = parts[0];
                        string senderIP = parts[1];

                        if (senderIP == localIP)
                            continue;

                        AddOrRefreshDevice(senderIP, senderName);
                    }
                }
                catch
                {
                }
            });

            listenThread.IsBackground = true;
            listenThread.Start();
        }

        private void StartDeviceCleanupTimer()
        {
            Thread cleanupThread = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(5000);

                    List<string> toRemove = new List<string>();

                    foreach (var kvp in lastSeen)
                    {
                        if (DateTime.Now - kvp.Value > DeviceTimeout)
                        {
                            toRemove.Add(kvp.Key);
                        }
                    }

                    if (toRemove.Count > 0)
                    {
                        foreach (string ip in toRemove)
                        {
                            devices.Remove(ip);
                            lastSeen.Remove(ip);
                        }

                        Dispatcher.Invoke(RefreshDeviceList);
                    }
                }
            });

            cleanupThread.IsBackground = true;
            cleanupThread.Start();
        }

        // =========================
        // FILE SERVER
        // =========================

        private void StartFileServer()
        {
            Thread serverThread =
                new Thread(() =>
                {
                    TcpListener server =
                        new TcpListener(
                            IPAddress.Any,
                            FilePort);

                    server.Start();

                    while (true)
                    {
                        TcpClient client =
                            server.AcceptTcpClient();

                        Thread clientThread =
                            new Thread(() =>
                                ReceiveFile(client));

                        clientThread.IsBackground = true;
                        clientThread.Start();
                    }
                });

            serverThread.IsBackground = true;
            serverThread.Start();
        }

        private void ReadExact(NetworkStream stream, byte[] buffer, int count)
        {
            int totalRead = 0;

            while (totalRead < count)
            {
                int read = stream.Read(buffer, totalRead, count - totalRead);

                if (read == 0)
                {
                    throw new IOException("Connection closed before all data was received.");
                }

                totalRead += read;
            }
        }

        private void ReceiveFile(TcpClient client)
        {
            string senderIP = "unknown";
            string fileNameForLog = "unknown";
            long fileSizeForLog = 0;

            try
            {
                senderIP =
                    ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();

                using (NetworkStream stream =
                    client.GetStream())
                {
                    byte[] sizeBytes =
                        new byte[4];

                    ReadExact(stream, sizeBytes, 4);

                    int nameLength =
                        BitConverter.ToInt32(
                            sizeBytes, 0);

                    byte[] nameBytes =
                        new byte[nameLength];

                    ReadExact(stream, nameBytes, nameLength);

                    // relativePath may contain "/" if sent as part of a
                    // folder transfer, e.g. "MyFolder/sub/photo.png"
                    string relativePath =
                        Encoding.UTF8.GetString(nameBytes);

                    fileNameForLog = relativePath;

                    byte[] fileLengthBytes =
                        new byte[8];

                    ReadExact(stream, fileLengthBytes, 8);

                    long fileLength =
                        BitConverter.ToInt64(fileLengthBytes, 0);

                    fileSizeForLog = fileLength;

                    byte[] expectedHash =
                        new byte[32];

                    ReadExact(stream, expectedHash, 32);

                    bool accepted = false;
                    string senderLabel = devices.ContainsKey(senderIP) ? devices[senderIP] : senderIP;

                    Dispatcher.Invoke(() =>
                    {
                        string sizeText = FormatFileSize(fileLength);

                        MessageBoxResult result =
                            MessageBox.Show(
                                $"📨 {senderLabel} wants to send you a file:\n\n" +
                                $"{relativePath}\n({sizeText})\n\n" +
                                "Want to accept it?",
                                "Envíalo - Incoming File",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question);

                        accepted = (result == MessageBoxResult.Yes);
                    });

                    if (!accepted)
                    {
                        stream.WriteByte(ResponseReject);

                        LogTransfer("RECEIVED", senderIP, relativePath, fileLength, "Rejected by user");

                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text =
                                $"You declined a file from {senderLabel}: {relativePath}";
                        });

                        return;
                    }

                    stream.WriteByte(ResponseAccept);

                    string appFolder =
                        Path.Combine(
                            Environment.GetFolderPath(
                                Environment.SpecialFolder.MyDocuments),
                            "Envíalo", "Recibidos");

                    // Build the save path, preserving any subfolder
                    // structure from a folder transfer.
                    string[] pathParts = relativePath.Split('/');

                    string savePath = appFolder;

                    for (int i = 0; i < pathParts.Length; i++)
                    {
                        savePath = Path.Combine(savePath, pathParts[i]);
                    }

                    string saveDirectory = Path.GetDirectoryName(savePath);

                    Directory.CreateDirectory(saveDirectory);

                    savePath = GetUniqueFilePath(savePath);

                    long remaining = fileLength;

                    using (SHA256 sha = SHA256.Create())
                    using (FileStream fs =
                        new FileStream(
                            savePath,
                            FileMode.Create))
                    {
                        byte[] buffer =
                            new byte[8192];

                        while (remaining > 0)
                        {
                            int chunk =
                                (int)Math.Min(buffer.Length, remaining);

                            int read =
                                stream.Read(buffer, 0, chunk);

                            if (read == 0)
                            {
                                throw new IOException("Connection closed before file was fully received.");
                            }

                            fs.Write(buffer, 0, read);
                            sha.TransformBlock(buffer, 0, read, null, 0);

                            remaining -= read;

                            long received = fileLength - remaining;

                            int progress =
                                fileLength == 0
                                ? 100
                                : (int)((received * 100) / fileLength);

                            Dispatcher.Invoke(() =>
                            {
                                TransferProgress.Value = progress;
                                StatusText.Text = $"Receiving {relativePath}... {progress}%";
                            });
                        }

                        sha.TransformFinalBlock(new byte[0], 0, 0);

                        byte[] actualHash = sha.Hash;

                        bool integrityOk = HashesMatch(expectedHash, actualHash);

                        string finalName = Path.GetFileName(savePath);

                        LogTransfer(
                            "RECEIVED",
                            senderIP,
                            relativePath,
                            fileLength,
                            integrityOk ? "Success" : "Integrity mismatch");

                        Dispatcher.Invoke(() =>
                        {
                            TransferProgress.Value = 100;

                            if (integrityOk)
                            {
                                StatusText.Text =
                                    "Received: " + finalName + " 🎉";

                                MessageBox.Show(
                                    "🎉 Got it!\n\n" + relativePath +
                                    "\n\nEverything checked out perfectly ✔",
                                    "Envíalo");
                            }
                            else
                            {
                                StatusText.Text =
                                    "Received (needs a re-send): " + finalName;

                                MessageBox.Show(
                                    "Received:\n" + relativePath +
                                    "\n\n⚠️ Hmm, something's off — the checksum doesn't match. " +
                                    "The file may be corrupted, ask the sender to try again.",
                                    "Envíalo - Integrity Warning",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                LogTransfer("RECEIVED", senderIP, fileNameForLog, fileSizeForLog, "Error: " + ex.Message);

                Dispatcher.Invoke(() =>
                {
                    StatusText.Text =
                        "Receive error: " +
                        ex.Message;
                });
            }
        }

        private bool HashesMatch(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }

            return true;
        }

        private string FormatFileSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unitIndex = 0;

            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return $"{size:0.##} {units[unitIndex]}";
        }

        private string GetUniqueFilePath(string path)
        {
            if (!File.Exists(path))
                return path;

            string directory = Path.GetDirectoryName(path);
            string nameOnly = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);

            int counter = 1;
            string newPath;

            do
            {
                string newName = $"{nameOnly} ({counter}){extension}";
                newPath = Path.Combine(directory, newName);
                counter++;
            }
            while (File.Exists(newPath));

            return newPath;
        }

        // =========================
        // SEND FILE(S)
        // =========================

        private void SendFileButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (DeviceList.SelectedItem == null)
            {
                MessageBox.Show(
                    "Pick someone to send to first! 👀");

                return;
            }

            OpenFileDialog dialog =
                new OpenFileDialog();

            dialog.Multiselect = true;

            if (dialog.ShowDialog() == true)
            {
                string selected =
                    DeviceList.SelectedItem
                    .ToString();

                string ip =
                    ExtractIP(selected);

                string deviceLabel =
                    selected.Split('-')[0].Trim();

                var items = new List<(string path, string relativeName)>();

                foreach (string path in dialog.FileNames)
                {
                    items.Add((path, Path.GetFileName(path)));
                }

                QueueFiles(ip, deviceLabel, items);
            }
        }

        // =========================
        // SEND FOLDER
        // =========================

        private void SendFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (DeviceList.SelectedItem == null)
            {
                MessageBox.Show(
                    "Pick someone to send to first! 👀");

                return;
            }

            OpenFolderDialog dialog = new OpenFolderDialog();

            if (dialog.ShowDialog() == true)
            {
                string selected =
                    DeviceList.SelectedItem.ToString();

                string ip =
                    ExtractIP(selected);

                string deviceLabel =
                    selected.Split('-')[0].Trim();

                string folderPath = dialog.FolderName;
                string folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));

                var items = new List<(string path, string relativeName)>();

                foreach (string filePath in Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(folderPath, filePath);

                    // Use forward slashes on the wire regardless of OS,
                    // and prefix with the folder name so the receiver
                    // recreates the same top-level folder.
                    string relativeWithFolder =
                        folderName + "/" + relative.Replace('\\', '/');

                    items.Add((filePath, relativeWithFolder));
                }

                if (items.Count == 0)
                {
                    MessageBox.Show("That folder looks empty — nothing to send! 📂");
                    return;
                }

                QueueFiles(ip, deviceLabel, items);
            }
        }

        // Adds every file to the visual queue immediately (as "Queued"),
        // then sends them one at a time in the background, updating each
        // QueueItem's Status/Progress live as it goes.
        private void QueueFiles(string ip, string deviceLabel, List<(string path, string relativeName)> items)
        {
            List<QueueItem> queueItems = new List<QueueItem>();

            foreach (var item in items)
            {
                QueueItem qi = new QueueItem
                {
                    FileName = item.relativeName,
                    TargetDevice = deviceLabel,
                    Status = "Queued",
                    Progress = 0
                };

                transferQueue.Add(qi);
                queueItems.Add(qi);
            }

            Thread queueThread = new Thread(() =>
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var (path, relativeName) = items[i];
                    QueueItem qi = queueItems[i];

                    Dispatcher.Invoke(() =>
                    {
                        qi.Status = "Sending";
                        StatusText.Text =
                            $"Sending {relativeName} ({i + 1} of {items.Count})...";
                    });

                    SendFile(ip, path, relativeName, qi);
                }
            });

            queueThread.IsBackground = true;
            queueThread.Start();
        }

        // =========================
        // DRAG & DROP
        // =========================

        private void DropArea_DragOver(
            object sender,
            DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
        }

        private void DropArea_Drop(
            object sender,
            DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            if (DeviceList.SelectedItem == null)
            {
                MessageBox.Show(
                    "Pick someone to send to first! 👀");

                return;
            }

            string[] paths =
                (string[])e.Data.GetData(
                    DataFormats.FileDrop);

            string selected =
                DeviceList.SelectedItem.ToString();

            string ip =
                ExtractIP(selected);

            string deviceLabel =
                selected.Split('-')[0].Trim();

            var items = new List<(string path, string relativeName)>();

            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                {
                    // A folder was dragged in - walk it recursively too.
                    string folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

                    foreach (string filePath in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(path, filePath);
                        string relativeWithFolder = folderName + "/" + relative.Replace('\\', '/');
                        items.Add((filePath, relativeWithFolder));
                    }
                }
                else if (File.Exists(path))
                {
                    items.Add((path, Path.GetFileName(path)));
                }
            }

            if (items.Count == 0)
                return;

            QueueFiles(ip, deviceLabel, items);
        }

        // =========================
        // SEND TO ALL
        // =========================

        private void SendAllButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (GetMode() != "Teacher")
            {
                MessageBox.Show(
                    "Only Teacher mode can send to everyone! 👩\u200d🏫");

                return;
            }

            OpenFileDialog dialog =
                new OpenFileDialog();

            dialog.Multiselect = true;

            if (dialog.ShowDialog() == true)
            {
                var items = new List<(string path, string relativeName)>();

                foreach (string path in dialog.FileNames)
                {
                    items.Add((path, Path.GetFileName(path)));
                }

                foreach (var d in devices)
                {
                    string ip = d.Key;
                    string label = d.Value;

                    QueueFiles(ip, label, items);
                }

                StatusText.Text =
                    "Broadcasting file(s) to all PCs...";
            }
        }

        // =========================
        // SEND FILE CORE
        // =========================

        private void SendFile(
            string ip,
            string path,
            string relativeName,
            QueueItem queueItem)
        {
            long totalBytes = 0;

            try
            {
                TcpClient client =
                    new TcpClient(ip, FilePort);

                using (NetworkStream stream =
                    client.GetStream())
                {
                    byte[] nameBytes =
                        Encoding.UTF8.GetBytes(
                            relativeName);

                    byte[] sizeBytes =
                        BitConverter.GetBytes(
                            nameBytes.Length);

                    FileInfo info =
                        new FileInfo(path);

                    totalBytes =
                        info.Length;

                    byte[] fileHash = ComputeFileHash(path);

                    stream.Write(sizeBytes, 0, 4);
                    stream.Write(nameBytes, 0, nameBytes.Length);

                    byte[] fileLengthBytes =
                        BitConverter.GetBytes(totalBytes);

                    stream.Write(fileLengthBytes, 0, 8);
                    stream.Write(fileHash, 0, fileHash.Length);

                    int response = stream.ReadByte();

                    if (response != ResponseAccept)
                    {
                        LogTransfer("SENT", ip, relativeName, totalBytes, "Rejected by recipient");

                        Dispatcher.Invoke(() =>
                        {
                            queueItem.Status = "Declined";

                            string label = devices.ContainsKey(ip) ? devices[ip] : ip;

                            StatusText.Text =
                                $"{label} said no thanks 🙅 ({relativeName})";
                        });

                        return;
                    }

                    long sentBytes = 0;

                    byte[] buffer =
                        new byte[8192];

                    using (FileStream fs =
                        new FileStream(
                            path,
                            FileMode.Open,
                            FileAccess.Read))
                    {
                        int read;

                        while ((read =
                            fs.Read(
                                buffer,
                                0,
                                buffer.Length)) > 0)
                        {
                            stream.Write(
                                buffer,
                                0,
                                read);

                            sentBytes += read;

                            int progress =
                                totalBytes == 0
                                ? 100
                                : (int)((sentBytes * 100)
                                / totalBytes);

                            Dispatcher.Invoke(() =>
                            {
                                TransferProgress.Value = progress;
                                queueItem.Progress = progress;

                                StatusText.Text =
                                    $"Sending {relativeName}... {progress}%";
                            });
                        }
                    }
                }

                LogTransfer("SENT", ip, relativeName, totalBytes, "Success");

                Dispatcher.Invoke(() =>
                {
                    TransferProgress.Value = 100;
                    queueItem.Progress = 100;
                    queueItem.Status = "Done";

                    StatusText.Text =
                        "Transfer Complete ✔🎉";
                });
            }
            catch (Exception ex)
            {
                LogTransfer("SENT", ip, relativeName, totalBytes, "Error: " + ex.Message);

                Dispatcher.Invoke(() =>
                {
                    queueItem.Status = "Failed";

                    StatusText.Text =
                        "Send error: " +
                        ex.Message;
                });
            }
        }

        private byte[] ComputeFileHash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs =
                new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                return sha.ComputeHash(fs);
            }
        }

        // =========================
        // HELPERS
        // =========================

        private string GetMode()
        {
            ComboBoxItem item =
                (ComboBoxItem)
                ModeBox.SelectedItem;

            return item.Content.ToString();
        }

        private string ExtractIP(string text)
        {
            return text.Split('-')[1].Trim();
        }

        private string GetLocalIP()
        {
            var host =
                Dns.GetHostEntry(
                    Dns.GetHostName());

            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily ==
                    AddressFamily.InterNetwork)
                {
                    string address =
                        ip.ToString();

                    if (address.StartsWith("127."))
                        continue;

                    if (address.StartsWith("192.168.56"))
                        continue;

                    return address;
                }
            }

            return "127.0.0.1";
        }
    }
}
