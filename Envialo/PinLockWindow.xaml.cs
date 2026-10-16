using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace Envialo
{
    public partial class PinLockWindow : Window
    {
        // pin.dat format (one value per line):
        //   line 1: pin hash
        //   line 2: security question   (only present if the user has set one up)
        //   line 3: security answer hash
        // Older pin.dat files may only have line 1 — handled as "no question set".

        private enum PinState
        {
            EnterPin,
            SetupPin,
            SetupSecurityQuestion,
            ForgotPin
        }

        private string pinFilePath;
        private PinState state;
        private int failedAttempts = 0;

        // Held in memory only between "choose a PIN" and "save the question" steps.
        private string pendingPinHash;

        public PinLockWindow()
        {
            InitializeComponent();

            string appFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Envíalo");

            Directory.CreateDirectory(appFolder);

            pinFilePath = Path.Combine(appFolder, "pin.dat");

            state = File.Exists(pinFilePath) ? PinState.EnterPin : PinState.SetupPin;
            ApplyState();
        }

        private void ApplyState()
        {
            // Reset everything, then show only what this state needs.
            PinBox.Visibility = Visibility.Collapsed;
            SetupQuestionPanel.Visibility = Visibility.Collapsed;
            ForgotPanel.Visibility = Visibility.Collapsed;
            ForgotPinLink.Visibility = Visibility.Collapsed;
            BackLink.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            SubmitButton.IsEnabled = true;

            switch (state)
            {
                case PinState.EnterPin:
                    TitleText.Text = "Envíalo";
                    SubtitleText.Text = "Welcome back! Enter your PIN 🔐";
                    SubmitButton.Content = "Continue";
                    PinBox.Visibility = Visibility.Visible;
                    PinBox.Password = "";
                    ForgotPinLink.Visibility = Visibility.Visible;
                    PinBox.Focus();
                    break;

                case PinState.SetupPin:
                    TitleText.Text = "Envíalo";
                    SubtitleText.Text = "Let's set up your PIN 🔐";
                    SubmitButton.Content = "Next";
                    PinBox.Visibility = Visibility.Visible;
                    PinBox.Password = "";
                    PinBox.Focus();
                    break;

                case PinState.SetupSecurityQuestion:
                    TitleText.Text = "Almost done!";
                    SubtitleText.Text = "This helps you get back in if you forget your PIN.";
                    SubmitButton.Content = "Save & Continue";
                    SetupQuestionPanel.Visibility = Visibility.Visible;
                    SetupQuestionBox.Text = "";
                    SetupAnswerBox.Text = "";
                    SetupQuestionBox.Focus();
                    break;

                case PinState.ForgotPin:
                    TitleText.Text = "Let's recover it";
                    SubtitleText.Text = "Answer your security question to set a new PIN.";
                    SubmitButton.Content = "Verify";
                    ForgotPanel.Visibility = Visibility.Visible;
                    ForgotAnswerBox.Text = "";
                    BackLink.Visibility = Visibility.Visible;
                    ForgotAnswerBox.Focus();
                    break;
            }
        }

        private void PinBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                SubmitButton_Click(sender, e);
        }

        private void SetupAnswerBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                SubmitButton_Click(sender, e);
        }

        private void ForgotAnswerBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                SubmitButton_Click(sender, e);
        }

        private void ForgotPinLink_Click(object sender, MouseButtonEventArgs e)
        {
            if (!File.Exists(pinFilePath))
            {
                // Shouldn't happen from the EnterPin screen, but just in case.
                state = PinState.SetupPin;
                ApplyState();
                return;
            }

            string[] lines = File.ReadAllLines(pinFilePath);

            if (lines.Length < 3)
            {
                ShowError("No recovery question was set up for this PIN. " +
                           "Ask an admin to delete pin.dat to reset it:\n" + pinFilePath);
                return;
            }

            StoredQuestionText.Text = lines[1];
            state = PinState.ForgotPin;
            ApplyState();
        }

        private void BackLink_Click(object sender, MouseButtonEventArgs e)
        {
            state = PinState.EnterPin;
            ApplyState();
        }

        private void SubmitButton_Click(object sender, RoutedEventArgs e)
        {
            switch (state)
            {
                case PinState.EnterPin:
                    HandleEnterPin();
                    break;

                case PinState.SetupPin:
                    HandleSetupPin();
                    break;

                case PinState.SetupSecurityQuestion:
                    HandleSetupSecurityQuestion();
                    break;

                case PinState.ForgotPin:
                    HandleForgotPin();
                    break;
            }
        }

        private void HandleEnterPin()
        {
            string pin = PinBox.Password;

            if (pin.Length != 4 || !IsAllDigits(pin))
            {
                ShowError("Your PIN needs exactly 4 digits 🔢");
                return;
            }

            string[] lines = File.ReadAllLines(pinFilePath);
            string storedHash = lines.Length > 0 ? lines[0] : "";
            string enteredHash = HashValue(pin);

            if (enteredHash == storedHash)
            {
                OpenMainWindow();
            }
            else
            {
                failedAttempts++;
                PinBox.Password = "";

                if (failedAttempts >= 5)
                {
                    ShowError("Whoa, slow down! Wait 30 seconds and try again ⏳");
                    LockTemporarily();
                }
                else
                {
                    ShowError("That's not quite right — try again 🔐");
                }
            }
        }

        private void HandleSetupPin()
        {
            string pin = PinBox.Password;

            if (pin.Length != 4 || !IsAllDigits(pin))
            {
                ShowError("Your PIN needs exactly 4 digits 🔢");
                return;
            }

            pendingPinHash = HashValue(pin);
            state = PinState.SetupSecurityQuestion;
            ApplyState();
        }

        private void HandleSetupSecurityQuestion()
        {
            string question = SetupQuestionBox.Text.Trim();
            string answer = SetupAnswerBox.Text.Trim();

            if (question.Length == 0)
            {
                ShowError("Please write a security question 📝");
                return;
            }

            if (answer.Length == 0)
            {
                ShowError("Please write an answer 📝");
                return;
            }

            // Keep the question on one line so the 3-line file format stays simple.
            string sanitizedQuestion = question.Replace("\r", " ").Replace("\n", " ");
            string answerHash = HashValue(answer.ToLowerInvariant());

            File.WriteAllLines(pinFilePath, new[]
            {
                pendingPinHash,
                sanitizedQuestion,
                answerHash
            });

            pendingPinHash = null;
            OpenMainWindow();
        }

        private void HandleForgotPin()
        {
            string[] lines = File.ReadAllLines(pinFilePath);
            string storedAnswerHash = lines.Length > 2 ? lines[2] : "";

            string enteredAnswer = ForgotAnswerBox.Text.Trim().ToLowerInvariant();
            string enteredAnswerHash = HashValue(enteredAnswer);

            if (enteredAnswerHash == storedAnswerHash)
            {
                File.Delete(pinFilePath);
                state = PinState.SetupPin;
                ApplyState();
            }
            else
            {
                ShowError("That answer doesn't match — try again 🤔");
            }
        }

        private async void LockTemporarily()
        {
            SubmitButton.IsEnabled = false;
            PinBox.IsEnabled = false;

            await System.Threading.Tasks.Task.Delay(30000);

            failedAttempts = 0;
            SubmitButton.IsEnabled = true;
            PinBox.IsEnabled = true;
            ErrorText.Visibility = Visibility.Collapsed;
        }

        private void OpenMainWindow()
        {
            MainWindow main = new MainWindow();
            main.Show();
            this.Close();
        }

        private bool IsAllDigits(string s)
        {
            foreach (char c in s)
            {
                if (!char.IsDigit(c))
                    return false;
            }
            return true;
        }

        private string HashValue(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                StringBuilder sb = new StringBuilder();
                foreach (byte b in bytes)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
