﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.System.Threading;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.Web.Http;
using Windows.Web.Http.Headers;

namespace IoTCoreDefaultApp
{
    internal enum CommandError
    {
        None,
        Cancelled,
        CdNotSupported,
        InvalidDirectory,
        NotAuthorized,
        TimedOut,
        GenericError
    }
    /// <summary>
    /// Command Line page.
    /// Allow executing processes and simple command lines using Windows Command Processor, cmd.exe, through a familiar interface.
    /// </summary>
    public sealed partial class CommandLinePage : Page
    {
        private const string CommandLineProcesserExe = "c:\\windows\\system32\\cmd.exe";
        private const string EnableCommandLineProcesserRegCommand = "reg ADD \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\EmbeddedMode\\ProcessLauncher\" /f /v AllowedExecutableFilesList /t REG_MULTI_SZ /d \"c:\\windows\\system32\\cmd.exe\\0\"";
        private const string DefaultHostName = "127.0.0.1";
        private const string DefaultProtocol = "http";
        private const string DefaultPort = "8080";
        private const string WdpRunCommandApi = "/api/iot/processmanagement/runcommand";
        private const string WdpRunCommandWithOutputApi = "/api/iot/processmanagement/runcommandwithoutput";
        private const string CommandHistoryKey = "CommandHistory";
        private const int BlockSize = 8192;
        private const int MaxTotalOutputBlockSizes = 30 * BlockSize;
        private const int MaxCommandHistory = 200;
        private const int MaxRetriesAfterProcessTerminates = 2;
        private const int HRESULT_AccessDenied = -2147024891;
        private const int HRESULT_InvalidDirectory = -2147024629;
        private readonly SolidColorBrush RedSolidColorBrush = new SolidColorBrush(Colors.Red);
        private readonly SolidColorBrush GraySolidColorBrush = new SolidColorBrush(Colors.Gray);
        private readonly SolidColorBrush YellowSolidColorBrush = new SolidColorBrush(Colors.Yellow);
        private readonly TimeSpan TimeOutAfterNoOutput = TimeSpan.FromSeconds(15);
        private readonly char[] readBuffer = new char[BlockSize];

        private string adminCommandLine;
        private List<string> commandLineHistory = new List<string>();
        private ResourceLoader resourceLoader = new ResourceLoader();
        private CoreDispatcher coreDispatcher;
        private IAsyncOperation<ProcessLauncherResult> processLauncherOperation;
        private int currentCommandLine = -1;
        private int totalOutputSize = 0;
        private bool isProcessRunning = true;
        private bool isProcessTimedOut = false;
        private bool isProcessingAdminCommand = false;

        public CommandLinePage()
        {
            InitializeComponent();
            DataContext = LanguageManager.GetInstance();
            NavigationCacheMode = NavigationCacheMode.Enabled;
            coreDispatcher = CoreWindow.GetForCurrentThread().Dispatcher;
            if (ApplicationData.Current.LocalSettings.Values.ContainsKey(CommandHistoryKey))
            {
                commandLineHistory.AddRange(DeserializeCommandHistory(ApplicationData.Current.LocalSettings.Values[CommandHistoryKey] as string));
                currentCommandLine = commandLineHistory.Count;
            }
        }

        private string[] DeserializeCommandHistory(string serializedCommandLineHistory)
        {
            if (serializedCommandLineHistory == null)
            {
                return new string[0];
            }

            return serializedCommandLineHistory.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private string SerializeCommandHistory()
        {
            StringBuilder builder = new StringBuilder();
            foreach (string cmdLine in commandLineHistory)
            {
                builder.AppendFormat("{0}\n", cmdLine);
            }

            return builder.ToString();
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            EnableCommandLineTextBox(true, CommandLine);
            StdOutputScroller.ChangeView(null, StdOutputScroller.ScrollableHeight, null, true);
        }

        private void RunProcess()
        {
            if (string.IsNullOrWhiteSpace(CommandLine.Text))
            {
                return;
            }

            StdOutputScroller.ChangeView(null, StdOutputScroller.ScrollableHeight, null, true);

            AddToCommandHistory(CommandLine.Text);
            currentCommandLine = commandLineHistory.Count;
            isProcessingAdminCommand = false;

            Run cmdLineRun = new Run
            {
                Foreground = GraySolidColorBrush,
                FontWeight = FontWeights.Bold,
                Text = "\n" + WorkingDirectory.Text + " " + CommandLine.Text + "\n"
            };

            var commandLineText = CommandLine.Text.Trim();

            EnableCommandLineTextBox(false);
            CommandLine.Text = string.Empty;
            MainParagraph.Inlines.Add(cmdLineRun);
            totalOutputSize += cmdLineRun.Text.Length;

            if (commandLineText.Equals("cls", StringComparison.CurrentCultureIgnoreCase) ||
                commandLineText.Equals("clear", StringComparison.CurrentCultureIgnoreCase))
            {
                ClearOutput();
            }
            else if (commandLineText.StartsWith("cd ", StringComparison.CurrentCultureIgnoreCase) || commandLineText.StartsWith("chdir ", StringComparison.CurrentCultureIgnoreCase))
            {
                ShowError(resourceLoader.GetString("CdNotSupported"));
                EnableCommandLineTextBox(true, WorkingDirectory);
            }
            else if (commandLineText.Equals("exit", StringComparison.CurrentCultureIgnoreCase))
            {
                NavigationUtils.GoBack();
            }
            else if (commandLineText.StartsWith("RunAsAdmin", StringComparison.CurrentCultureIgnoreCase))
            {
                RunAdminCommand(commandLineText);
            }
            else
            {
                LaunchCmdProcess(commandLineText);
            }
        }

        private void RunAdminCommand(string commandLineText)
        {
            adminCommandLine = commandLineText.Remove(0, "RunAsAdmin".Length).Trim();
            if (adminCommandLine.Length == 0)
            {
                ShowError(resourceLoader.GetString("RunAsAdminUsage"));
                EnableCommandLineTextBox(true, CommandLine);
            }
            else
            {
                adminCommandLine = "cd \"" + GetWorkingDirectory() + "\" & " + adminCommandLine;
                isProcessingAdminCommand = true;
                ShowGetCredentialsPopup();
            }
        }

        private void LaunchCmdProcess(string commandLineText)
        {
            var args = String.Format("/C \"{0}\"", commandLineText); ;
            var standardOutput = new InMemoryRandomAccessStream();
            var standardError = new InMemoryRandomAccessStream();
            var options = new ProcessLauncherOptions
            {
                StandardOutput = standardOutput,
                StandardError = standardError,
                WorkingDirectory = GetWorkingDirectory()
            };
            string stdErrRunText = string.Empty;
            CommandError commandError = CommandError.None;

            isProcessRunning = true;
            isProcessTimedOut = false;
            processLauncherOperation = ProcessLauncher.RunToCompletionAsync(CommandLineProcesserExe, args, options);
            processLauncherOperation.Completed = (operation, status) =>
            {
                isProcessRunning = false;

                if (status == AsyncStatus.Canceled)
                {
                    if (isProcessTimedOut)
                    {
                        commandError = CommandError.TimedOut;
                        stdErrRunText = "\n" + String.Format(resourceLoader.GetString("CommandTimeoutText"), TimeOutAfterNoOutput.Seconds);
                    }
                    else
                    {
                        commandError = CommandError.Cancelled;
                        stdErrRunText = "\n" + resourceLoader.GetString("CommandCancelled");
                    }
                }
                else if (status == AsyncStatus.Error)
                {
                    if (operation.ErrorCode.HResult == HRESULT_AccessDenied)
                    {
                        commandError = CommandError.NotAuthorized;
                        stdErrRunText = String.Format(resourceLoader.GetString("CmdNotEnabled"), EnableCommandLineProcesserRegCommand);

                    }
                    else
                    if (operation.ErrorCode.HResult == HRESULT_InvalidDirectory)
                    {
                        commandError = CommandError.InvalidDirectory;
                        stdErrRunText = String.Format(resourceLoader.GetString("WorkingDirectoryInvalid"), options.WorkingDirectory);

                    }
                    else
                    {
                        commandError = CommandError.GenericError;
                        stdErrRunText = String.Format(resourceLoader.GetString("CommandLineError"), operation.ErrorCode.Message);
                    }
                }

                if (commandError != CommandError.None)
                {
                    ShowError(stdErrRunText);

                    if (commandError == CommandError.NotAuthorized)
                    {
                        ShowAuthorizationUI();
                    }
                }

                if (commandError == CommandError.InvalidDirectory)
                {
                    EnableCommandLineTextBox(true, WorkingDirectory);
                }
                else
                {
                    EnableCommandLineTextBox(true, CommandLine);
                }
            };

            var stdOutTask = ThreadPool.RunAsync(async (t) =>
            {
                using (var outStreamRedirect = standardOutput.GetInputStreamAt(0))
                {
                    using (var streamReader = new StreamReader(outStreamRedirect.AsStreamForRead()))
                    {
                        await ReadText(streamReader);
                    }
                }
            }).AsTask();

            var stdErrTask = ThreadPool.RunAsync(async (t) =>
            {
                using (var errStreamRedirect = standardError.GetInputStreamAt(0))
                {
                    using (var streamReader = new StreamReader(errStreamRedirect.AsStreamForRead()))
                    {
                        await ReadText(streamReader, isErrorRun: true);
                    }
                }
            }).AsTask();

            Task[] tasks = new Task[2]
            {
                    stdOutTask,
                    stdErrTask
            };

            Task.WaitAll(tasks);
        }

        private async void ShowAuthorizationUI()
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                InlineUIContainer uiContainer = new InlineUIContainer();
                Button cmdEnableButton = new Button
                {
                    Content = resourceLoader.GetString("EnableCmdText")
                };
                cmdEnableButton.Click += AccessButtonClicked;
                uiContainer.Child = cmdEnableButton;
                MainParagraph.Inlines.Add(uiContainer);
                MainParagraph.Inlines.Add(new Run() { Text = "\n\n" });
            });
        }

        private async void ShowError(string stdErrRunText)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                Run stdErrRun = new Run
                {
                    Text = stdErrRunText + "\n",
                    Foreground = RedSolidColorBrush,
                    FontWeight = FontWeights.Bold
                };

                MainParagraph.Inlines.Add(stdErrRun);
            });
        }

        private void AddToCommandHistory(string cmdLine)
        {
            if (commandLineHistory.Count >= MaxCommandHistory)
            {
                commandLineHistory.RemoveAt(0);
            }

            commandLineHistory.Add(cmdLine);

            ThreadPool.RunAsync((asyncAction) =>
            {
                ApplicationData.Current.LocalSettings.Values[CommandHistoryKey] = SerializeCommandHistory();
            }).AsTask();
        }

        private void AccessButtonClicked(object sender, RoutedEventArgs e)
        {
            ShowGetCredentialsPopup();
        }

        private void ShowGetCredentialsPopup()
        {
            CoreWindow currentWindow = Window.Current.CoreWindow;
            GetCredentialsPopup.VerticalOffset = (currentWindow.Bounds.Height / 2) - (CredentialsStackPanel.Height / 2);
            GetCredentialsPopup.HorizontalOffset = (currentWindow.Bounds.Width / 2) - (CredentialsStackPanel.Width / 2);
            GetCredentialsPopup.IsOpen = true;
            Password.Focus(FocusState.Keyboard);
        }

        private async Task ReadText(StreamReader streamReader, bool isErrorRun = false)
        {
            DateTime lastOutputTime = DateTime.Now;
            uint numTriesAfterProcessCompletes = 0;

            while (true)
            {
                int charsRead = await streamReader.ReadBlockAsync(readBuffer, 0, readBuffer.Length);
                if (charsRead <= 0)
                {
                    if (isProcessRunning)
                    {
                        if (DateTime.Now.Subtract(lastOutputTime) > TimeOutAfterNoOutput)
                        {
                            // Timeout
                            isProcessTimedOut = true;
                            processLauncherOperation.Cancel();
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(1));
                        }
                    }
                    else
                    {
                        if (numTriesAfterProcessCompletes >= MaxRetriesAfterProcessTerminates)
                        {
                            break;
                        }
                        else
                        {
                            numTriesAfterProcessCompletes++;
                            await Task.Delay(TimeSpan.FromMilliseconds(1));
                        }
                    }
                }
                else
                {
                    lastOutputTime = DateTime.Now;
                    string text = new string(readBuffer, 0, charsRead);
                    if (totalOutputSize + text.Length > MaxTotalOutputBlockSizes)
                    {
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
                    }

                    await coreDispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        AddLineToParagraph(isErrorRun, text);
                    });
                }
            }
        }

        private void AddLineToParagraph(bool isErrorRun, string text)
        {
            while (totalOutputSize + text.Length > MaxTotalOutputBlockSizes)
            {
                Run firstRun = MainParagraph.Inlines[0] as Run;
                if (firstRun != null && firstRun.Text != null)
                {
                    totalOutputSize -= firstRun.Text.Length;
                }
                MainParagraph.Inlines.RemoveAt(0);
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
            }

            totalOutputSize += text.Length;
            if (!isErrorRun)
            {
                MainParagraph.Inlines.Add(new Run
                {
                    Text = text
                });
            }
            else
            {
                MainParagraph.Inlines.Add(new Run
                {
                    Text = text,
                    Foreground = RedSolidColorBrush
                });
            }
        }

        private async void CommandLine_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case VirtualKey.Enter:
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        RunProcess();
                    });
                    break;
                case VirtualKey.Up:
                    currentCommandLine = Math.Max(0, currentCommandLine - 1);
                    if (currentCommandLine < commandLineHistory.Count)
                    {
                        UpdateCommandLineFromHistory();
                    }
                    break;
                case VirtualKey.Down:
                    currentCommandLine = Math.Min(commandLineHistory.Count, currentCommandLine + 1);
                    if (currentCommandLine < commandLineHistory.Count && currentCommandLine >= 0)
                    {
                        UpdateCommandLineFromHistory();
                    }
                    else
                    {
                        CommandLine.Text = string.Empty;
                    }
                    break;
                case VirtualKey.Escape:
                    CommandLine.Text = string.Empty;
                    break;
            }
        }

        private void UpdateCommandLineFromHistory()
        {
            CommandLine.Text = commandLineHistory[currentCommandLine];
            if (CommandLine.Text.Length > 0)
            {
                CommandLine.SelectionStart = CommandLine.Text.Length;
                CommandLine.SelectionLength = 0;
            }
        }

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                RunProcess();
            });
        }

        private async void EnableCommandLineTextBox(bool isEnabled, TextBox textBoxToFocus = null)
        {
            await coreDispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                RunButton.IsEnabled = isEnabled;
                CommandLine.IsEnabled = isEnabled;
                WorkingDirectory.IsEnabled = isEnabled;
                ClearButton.IsEnabled = isEnabled;

                CancelButton.IsEnabled = !isEnabled;
                CancelButton.Foreground = isEnabled ? GraySolidColorBrush : YellowSolidColorBrush;
                CancelButton.FontWeight = isEnabled ? FontWeights.Normal : FontWeights.Bold;

                if (isEnabled && textBoxToFocus != null)
                {
                    textBoxToFocus.Focus(FocusState.Keyboard);
                }
            });
        }

        private void StdOutputText_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            StdOutputScroller.ChangeView(null, StdOutputScroller.ScrollableHeight, null);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearOutput();
        }

        private void ClearOutput()
        {
            var previousOutputSize = totalOutputSize;
            totalOutputSize = 0;
            MainParagraph.Inlines.Clear();
            if (previousOutputSize > (MaxTotalOutputBlockSizes / 2))
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
            }
            EnableCommandLineTextBox(true, CommandLine);
        }

        private async void RunAdminCommand()
        {
            GetCredentialsPopup.IsOpen = false;

            if (Password.Password.Trim().Equals(string.Empty))
            {
                // Empty password not accepted
                return;
            }

            string outputText = string.Empty;
            bool isError = false;
            if (isProcessingAdminCommand)
            {
                try
                {
                    var response = await ExecuteCommandUsingRESTApi(DefaultHostName, Username.Text, Password.Password, adminCommandLine);
                    if (response.IsSuccessStatusCode)
                    {
                        JsonObject jsonOutput = null;
                        if (JsonObject.TryParse(await response.Content.ReadAsStringAsync(), out jsonOutput))
                        {
                            if (jsonOutput.ContainsKey("output"))
                            {
                                outputText = jsonOutput["output"].GetString();
                            }
                            else
                            {
                                isError = true;
                                outputText = resourceLoader.GetString("CouldNotParseOutputFailure");
                            }
                        }
                        else
                        {
                            isError = true;
                            outputText = resourceLoader.GetString("CouldNotParseOutputFailure");
                        }
                    }
                    else
                    {
                        isError = true;
                        outputText = String.Format(resourceLoader.GetString("CmdTextAdminCommandFailure"), response.StatusCode);
                    }
                }
                catch (Exception adminCmdRunException)
                {
                    isError = true;
                    outputText = String.Format(resourceLoader.GetString("CmdTextAdminCommandFailure"), adminCmdRunException.Message);
                }
            }
            else
            {
                try
                {
                    var response = await ExecuteCommandUsingRESTApi(DefaultHostName, Username.Text, Password.Password, EnableCommandLineProcesserRegCommand, isOutputRequired: false);
                    if (response.IsSuccessStatusCode)
                    {
                        outputText = resourceLoader.GetString("CmdTextEnabledSuccess");
                    }
                    else
                    {
                        isError = true;
                        outputText = String.Format(resourceLoader.GetString("CmdTextEnabledFailure"), response.StatusCode);
                    }
                }
                catch (Exception cmdEnabledException)
                {
                    isError = true;
                    outputText = String.Format(resourceLoader.GetString("CmdTextEnabledFailure"), cmdEnabledException.Message);
                }
            }

            AddLineToParagraph(isError, outputText);
            EnableCommandLineTextBox(true, CommandLine);
        }

        private void CredentialsPopupContinueButton_Click(object sender, RoutedEventArgs e)
        {
            RunAdminCommand();
        }

        private void Password_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                RunAdminCommand();
            }
        }

        private async Task<HttpResponseMessage> ExecuteCommandUsingRESTApi(string ipAddress, string username, string password, string runCommand, bool isOutputRequired = true)
        {
            var client = new HttpClient();
            var command = CryptographicBuffer.ConvertStringToBinary(runCommand, BinaryStringEncoding.Utf8);
            var runAsDefaultAccountFalse = CryptographicBuffer.ConvertStringToBinary("false", BinaryStringEncoding.Utf8);
            var timeout = CryptographicBuffer.ConvertStringToBinary(String.Format("{0}", TimeOutAfterNoOutput.TotalMilliseconds), BinaryStringEncoding.Utf8);

            var urlContent = new HttpFormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("command", CryptographicBuffer.EncodeToBase64String(command)),
                new KeyValuePair<string,string>("runasdefaultaccount", CryptographicBuffer.EncodeToBase64String(runAsDefaultAccountFalse)),
                new KeyValuePair<string,string>("timeout", CryptographicBuffer.EncodeToBase64String(timeout)),
            });

            var wdpCommand = isOutputRequired ? WdpRunCommandWithOutputApi : WdpRunCommandApi;
            var uriString = String.Format("{0}://{1}:{2}{3}?{4}", DefaultProtocol, ipAddress, DefaultPort, wdpCommand, await urlContent.ReadAsStringAsync());
            var uri = new Uri(uriString);

            var authBuffer = CryptographicBuffer.ConvertStringToBinary(String.Format("{0}:{1}", username, password), BinaryStringEncoding.Utf8);
            client.DefaultRequestHeaders.Authorization = new HttpCredentialsHeaderValue("Basic", CryptographicBuffer.EncodeToBase64String(authBuffer));

            HttpResponseMessage response = await client.PostAsync(uri, null);
            return response;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (isProcessRunning)
            {
                processLauncherOperation.Cancel();
            }
        }

        private void WorkingDirectory_LostFocus(object sender, RoutedEventArgs e)
        {
            string workingDirectory = GetWorkingDirectory();

            WorkingDirectory.Text = workingDirectory + ">";
        }

        private bool IsDrive(string workingDirectory)
        {
            return workingDirectory.Length == 2 && workingDirectory[1] == ':';
        }

        private string GetWorkingDirectory()
        {
            var workingDirectory = WorkingDirectory.Text.Trim();
            if (workingDirectory.Length == 0)
            {
                return "C:\\";
            }

            while (workingDirectory.EndsWith(">") || workingDirectory.EndsWith("\\"))
            {
                workingDirectory = workingDirectory.Remove(workingDirectory.Length - 1);
            }

            if (IsDrive(workingDirectory))
            {
                workingDirectory = workingDirectory + "\\";
            }

            return workingDirectory;
        }

        private void WorkingDirectory_GotFocus(object sender, RoutedEventArgs e)
        {
            WorkingDirectory.Text = GetWorkingDirectory();
            WorkingDirectory.SelectAll();
        }

        private void WorkingDirectory_CharReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
        {
            if (args.Character=='\r')
            {
                CommandLine.Focus(FocusState.Keyboard);
            }
        }
    }
}
