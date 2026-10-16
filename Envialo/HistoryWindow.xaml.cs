using System;
using System.IO;
using System.Windows;

namespace Envialo
{
    public partial class HistoryWindow : Window
    {
        private string logFilePath;

        public HistoryWindow()
        {
            InitializeComponent();

            string appFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Envíalo");

            Directory.CreateDirectory(appFolder);

            logFilePath = Path.Combine(appFolder, "history.log");

            LoadHistory();
        }

        private void LoadHistory()
        {
            HistoryList.Items.Clear();

            if (!File.Exists(logFilePath))
            {
                HistoryList.Items.Add("No transfers yet — send or receive a file to see it here! 📭");
                return;
            }

            string[] lines = File.ReadAllLines(logFilePath);

            if (lines.Length == 0)
            {
                HistoryList.Items.Add("No transfers yet — send or receive a file to see it here! 📭");
                return;
            }

            // Show most recent first
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                HistoryList.Items.Add(lines[i]);
            }
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult result = MessageBox.Show(
                "Clear all your transfer history? This can't be undone 🗑️",
                "Envíalo",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                if (File.Exists(logFilePath))
                {
                    File.Delete(logFilePath);
                }

                LoadHistory();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
