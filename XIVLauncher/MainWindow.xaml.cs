using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdysTech.CredentialManager;
using AutoUpdaterDotNET;
using MaterialDesignThemes.Wpf;
using XIVLauncher.Addon;
using Color = System.Windows.Media.Color;

namespace XIVLauncher
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private System.Timers.Timer _bannerChangeTimer;
        private Headlines _headlines;
        private BitmapImage[] _bannerBitmaps;
        private int _currentBannerIndex = 0;

        private System.Timers.Timer _maintenanceQueueTimer;

        private static string AppName = "FINAL FANTASY XIV";

        private XIVGame _game = new XIVGame();

        private bool _isLoggingIn = false;

        public MainWindow(string accountName)
        {
            InitializeComponent();

            this.Title += " v" + Util.GetAssemblyVersion();

            if (!string.IsNullOrEmpty(accountName))
            {
                this.Title += " - Account: " + accountName;
                AppName += "-" + accountName;
            }

#if !DEBUG
            AutoUpdater.ShowSkipButton = false;
            AutoUpdater.ShowRemindLaterButton = false;
            AutoUpdater.Mandatory = true;
            AutoUpdater.UpdateMode = Mode.Forced;

            AutoUpdater.CheckForUpdateEvent += AutoUpdaterOnCheckForUpdateEvent;

            Serilog.Log.Information("Starting update check.");
            AutoUpdater.Start("https://goaaats.github.io/ffxiv/tools/launcher/update.xml");
#else
            InitializeWindow();
#endif
        }

        private void SetupHeadlines()
        {
            try
            {
                _bannerChangeTimer?.Stop();

                _headlines = Headlines.Get(_game);

                _bannerBitmaps = new BitmapImage[_headlines.Banner.Length];
                for (var i = 0; i < _headlines.Banner.Length; i++)
                {
                    var imageBytes = _game.DownloadAsLauncher(_headlines.Banner[i].LsbBanner.ToString());

                    using (var stream = new MemoryStream(imageBytes))
                    {
                        var bitmapImage = new BitmapImage();
                        bitmapImage.BeginInit();
                        bitmapImage.StreamSource = stream;
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.EndInit();
                        bitmapImage.Freeze();

                        _bannerBitmaps[i] = bitmapImage;
                    }
                }
                
                this.Dispatcher.BeginInvoke(new Action(() => { BannerImage.Source = _bannerBitmaps[0]; }));

                _bannerChangeTimer = new System.Timers.Timer {Interval = 5000};

                _bannerChangeTimer.Elapsed += (o, args) =>
                {
                    if (_currentBannerIndex + 1 > _headlines.Banner.Length - 1)
                    {
                        _currentBannerIndex = 0;
                    }
                    else
                    {
                        _currentBannerIndex++;
                    }

                    this.Dispatcher.BeginInvoke(new Action(() => { BannerImage.Source = _bannerBitmaps[_currentBannerIndex]; }));
                };

                _bannerChangeTimer.AutoReset = true;
                _bannerChangeTimer.Start();

                this.Dispatcher.BeginInvoke(new Action(() => { NewsListView.ItemsSource = _headlines.News; }));
            }
            catch (Exception)
            {
                this.Dispatcher.BeginInvoke(new Action(() => { NewsListView.Items.Add(new News() {Title = "Could not download news data.", Tag = "DlError"}); }));
            }
        }

        private void InitializeWindow()
        {
            // Upgrade the stored settings if needed
            if (Properties.Settings.Default.UpgradeRequired)
            {
                Serilog.Log.Information("Settings upgrade required...");
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.UpgradeRequired = false;
                Properties.Settings.Default.Save();
            }

            var gateStatus = false;
            try
            {
                gateStatus = _game.GetGateStatus();
            }
            catch
            {
                // ignored
            }

            if (!gateStatus)
            {
                WorldStatusPackIcon.Foreground = new SolidColorBrush(Color.FromRgb(242, 24, 24));
            }

            var savedCredentials = CredentialManager.GetCredentials(AppName);

            if (savedCredentials != null)
            {
                LoginUsername.Text = savedCredentials.UserName;
                LoginPassword.Password = savedCredentials.Password;
                OtpCheckBox.IsChecked = Settings.NeedsOtp();
                AutoLoginCheckBox.IsChecked = Settings.IsAutologin();
                SaveLoginCheckBox.IsChecked = true;
            }

            if (Settings.IsAutologin() && savedCredentials != null && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                Serilog.Log.Information("Engaging Autologin");

                try
                {
                    if (!gateStatus)
                    {
                        MessageBox.Show(
                            "Square Enix seems to be running maintenance work right now. The game shouldn't be launched.");
                        Settings.SetAutologin(false);
                    }
                    else
                    {
                        HandleLogin(true);
                        Settings.Save();
                        return;
                    }
                }
                catch (Exception exc)
                {
                    new ErrorWindow(exc, "Additionally, please check your login information or try again.", "AutoLogin").ShowDialog();
                    Settings.SetAutologin(false);
                }

                Settings.Save();
            }

            if (Settings.GetGamePath() == string.Empty)
            {
                var setup = new FirstTimeSetup();
                setup.ShowDialog();
            }

            Task.Run(() => SetupHeadlines());
                
            Settings.LanguageChanged += SetupHeadlines;

            try
            {
                Task.Run(() => StartServiceAddon());
            }
            catch (Exception exc)
            {
                new ErrorWindow(exc, "This could be caused by your antivirus, please check its logs and add any needed exclusions.", "Addons").ShowDialog();
                isLoggingIn = false;
            }

            var version = Util.GetAssemblyVersion();
            if (Properties.Settings.Default.LastVersion != version)
            {
                MessageBox.Show($"XIVLauncher was updated to version {version}. This version features some changes and improvements:\r\n\r\n* Restructuring of In-Game addon chat features\r\n    -> WebHooks were replaced with a custom discord bot that has to be created and joined to your own server with the \"/xlbotjoin\" command\r\n    -> Chat channels can now be customized, with chatting in-game from discord coming soon\r\n\t-> Added a new notification type, retainer sales\r\n    -> Basic plugin API is now available, to be expanded. See the market board plugin as an example.\r\n* Maintenance queue was fixed in preparation for Shadowbringers\r\n* Big improvements to UI layout and loading times\r\n* News data now uses the correct language based on the set game language\r\n* Various security improvements\r\n* Basic multi-account feature(UI will come later)\r\n    -> Create shortcuts to XIVLauncher.exe with the launch arguments \"--accountName=<name>\" to save login details for different accounts\r\n\r\nBig thanks to @anoyetta and @roy-n-roy for helping out with this update!", "XIVLauncher updated!", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                Properties.Settings.Default.LastVersion = version;
                Properties.Settings.Default.Save();
            }

            Show();
            Activate();

            Serilog.Log.Information("MainWindow initialized.");
        }

        private void AutoUpdaterOnCheckForUpdateEvent(UpdateInfoEventArgs args)
        {
            Serilog.Log.Information("AutoUpdaterOnCheckForUpdateEvent called.");
            if (args != null)
            {
                if (args.IsUpdateAvailable)
                {
                    try
                    {
                        Serilog.Log.Information("Update available, trying to download.");
                        MessageBox.Show("An update for XIVLauncher is available. It will now be downloaded, the application will restart.",
                            "XIVLauncher Update", MessageBoxButton.OK, MessageBoxImage.Asterisk);

                        if (AutoUpdater.DownloadUpdate())
                        {
                            Environment.Exit(0);
                        }
                        else
                        {
                            Util.ShowError($"Could not download update. Please try again later.", "Update failed");
                            Environment.Exit(0);
                        }
                    }
                    catch (Exception exc)
                    {
                        new ErrorWindow(exc, $"Update failed. Please report this error and try again later. \n\n{exc}", "UpdateAvailableFail").ShowDialog();
                        Environment.Exit(0);
                    }
                }
                else
                {
                    Serilog.Log.Information("No update: {0}", args.CurrentVersion);
                    InitializeWindow();
                }
            }
            else
            {
                Util.ShowError($"Could not check for updates. Please try again later.", "Update failed");
                Serilog.Log.Error("Update check failed.");
                Environment.Exit(0);
            }
        }

        private void OtpTextBox_OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            /*
            LoadingMessageCancelButton.Visibility = Visibility.Hidden;
            LoadingMessageTextBlock.Text = "Logging in...";

            DialogHost.OpenDialogCommand.Execute(null, MaintenanceQueueDialogHost);
            Task.Run(() => { this.Dispatcher.BeginInvoke(new Action(() => {  })); });
            */

            if (_isLoggingIn)
                return;

            HandleLogin(false);
            _isLoggingIn = true;
        }

        internal void HandleLogin(bool autoLogin, string onetimePassword = null)
        {
            OtpTextBox.Text = onetimePassword ?? "";

            var hasValidCache = _game.Cache.HasValidCache(LoginUsername.Text) && Settings.UniqueIdCacheEnabled;

            if (OtpCheckBox.IsChecked == true && !hasValidCache)
            {
                DialogHost.OpenDialogCommand.Execute(null, OtpDialogHost);
            }

            if (SaveLoginCheckBox.IsChecked == true)
            {
                Settings.SaveCredentials(AppName, LoginUsername.Text, LoginPassword.Password);
                Settings.SetNeedsOtp(OtpCheckBox.IsChecked == true);

                if (!autoLogin)
                {
                    if (AutoLoginCheckBox.IsChecked == true)
                    {
                        var result = MessageBox.Show("This option will log you in automatically with the credentials you entered.\nTo reset it again, launch this application while holding the Shift key.\n\nDo you really want to enable it?", "Enabling Autologin", MessageBoxButton.YesNo);

                        if (result == MessageBoxResult.No)
                        {
                            AutoLoginCheckBox.IsChecked = false;
                        }
                    }
                    else
                    {
                        AutoLoginCheckBox.IsChecked = false;
                    }

                    Settings.SetAutologin(AutoLoginCheckBox.IsChecked == true);
                }

                Settings.Save();
            }
            else
            {
                Settings.ResetCredentials(AppName);
                Settings.Save();
            }

            if (OtpCheckBox.IsChecked == false || hasValidCache)
            {
                StartGame();
            }
        }

        private void StartAddons(Process gameProcess)
        {
            foreach (var addonEntry in Settings.GetAddonList().Where(x => x.IsEnabled == true && x.StartAt == AddonStartAt.GameLaunched))
            {
                Serilog.Log.Information("Starting addon {0}", addonEntry.Addon.Name);
                addonEntry.Addon.Run(gameProcess);
            }
        }

        private void StartServiceAddon()
        {
            foreach (var addonEntry in Settings.GetAddonList().Where(x => x.IsEnabled == true && x.StartAt == AddonStartAt.LauncherInitialised))
            {
                if (addonEntry.Addon is IServiceAddon)
                    (addonEntry.Addon as IServiceAddon).Run(this);
            }
        }

        private void StopServiceAddons()
        {
            foreach (var addonEntry in Settings.GetAddonList().Where(x => x.IsEnabled == true && x.StartAt == AddonStartAt.LauncherInitialised))
            {
                if (addonEntry.Addon is IServiceAddon)
                    (addonEntry.Addon as IServiceAddon).Stop();
            }
        }

        private async void StartGame()
        {
            try
            {
                var gateStatus = false;
                try
                {
                    gateStatus = await Task.Run(() => _game.GetGateStatus());
                }
                catch
                {
                    // ignored
                }

                if (!gateStatus)
                {
                    MessageBox.Show(
                        "Square Enix seems to be running maintenance work right now or the login server is unreachable. The game shouldn't be launched.", "Error", MessageBoxButton.OK, MessageBoxImage.Asterisk);

                    return;
                }

                //LoadingMessageCancelButton.Visibility = Visibility.Hidden;
                //LoadingMessageTextBlock.Text = "Logging in...";

                //DialogHost.OpenDialogCommand.Execute(null, MaintenanceQueueDialogHost);

                var gameProcess = _game.Login(LoginUsername.Text, LoginPassword.Password, OtpTextBox.Text, Settings.UniqueIdCacheEnabled);

                if (gameProcess == null)
                    return;

                try
                {
                    await Task.Run(() => StartAddons(gameProcess));
                }
                catch (Exception exc)
                {
                    new ErrorWindow(exc, "This could be caused by your antivirus, please check its logs and add any needed exclusions.", "Addons").ShowDialog();
                    _isLoggingIn = false;
                }

                try
                {
                    if (Settings.IsInGameAddonEnabled())
                    {
                        await Task.Run(() =>
                        {
                            new HooksAddon().Run(gameProcess);
                        });
                    }
                }
                catch (Exception exc)
                {
                    new ErrorWindow(exc, "This could be caused by your antivirus, please check its logs and add any needed exclusions.", "Hooks").ShowDialog();
                    _isLoggingIn = false;
                }

                Environment.Exit(0);
            }
            catch (Exception exc)
            {
                new ErrorWindow(exc, "Additionally, please check your login information or try again.", "Login").ShowDialog();
                _isLoggingIn = false;
            }
        }

        private void BannerCard_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (_headlines != null)
            {
                Process.Start(_headlines.Banner[_currentBannerIndex].Link.ToString());
            }
        }

        private void SaveLoginCheckBox_OnChecked(object sender, RoutedEventArgs e)
        {
            AutoLoginCheckBox.IsEnabled = true;
        }

        private void SaveLoginCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
        {
            AutoLoginCheckBox.IsChecked = false;
            AutoLoginCheckBox.IsEnabled = false;
        }

        private void NewsListView_OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (_headlines == null)
                return;

            if (NewsListView.SelectedItem is News item)
            {
                if (item.Url != string.Empty)
                {
                    Process.Start(item.Url);
                }
                else
                {
                    string url;
                    switch (Settings.GetLanguage())
                    {
                        case ClientLanguage.Japanese:

                            url = "https://jp.finalfantasyxiv.com/lodestone/news/detail/";
                            break;

                        case ClientLanguage.English:

                            url = "https://eu.finalfantasyxiv.com/lodestone/news/detail/";
                            break;

                        case ClientLanguage.German:

                            url = "https://de.finalfantasyxiv.com/lodestone/news/detail/";
                            break;

                        case ClientLanguage.French:

                            url = "https://fr.finalfantasyxiv.com/lodestone/news/detail/";
                            break;

                        default:

                            url = "https://eu.finalfantasyxiv.com/lodestone/news/detail/";
                            break;
                    }

                    Process.Start(url + item.Id);
                }
            }
        }

        private void WorldStatusButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("http://is.xivup.com/");
        }

        private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
        {
            new SettingsWindow().ShowDialog();
        }

        private void QueueButton_OnClick(object sender, RoutedEventArgs e)
        {
            _maintenanceQueueTimer = new System.Timers.Timer
            {
                Interval = 5000
            };

            _maintenanceQueueTimer.Elapsed += (o, args) =>
            {
                var gateStatus = false;
                try
                {
                    gateStatus = _game.GetGateStatus();
                }
                catch
                {
                    // ignored
                }

                if (gateStatus)
                {
                    Console.Beep(529, 130);
                    System.Threading.Thread.Sleep(200);
                    Console.Beep(529, 100);
                    System.Threading.Thread.Sleep(30);
                    Console.Beep(529, 100);
                    System.Threading.Thread.Sleep(300);
                    Console.Beep(420, 140);
                    System.Threading.Thread.Sleep(300);
                    Console.Beep(466, 100);
                    System.Threading.Thread.Sleep(300);
                    Console.Beep(529, 160);
                    System.Threading.Thread.Sleep(200);
                    Console.Beep(466, 100);
                    System.Threading.Thread.Sleep(30);
                    Console.Beep(529, 900);

                    this.Dispatcher.BeginInvoke(new Action(() => LoginButton_Click(null, null)));
                    _maintenanceQueueTimer.Stop();
                    return;
                }

                _maintenanceQueueTimer.Start();
            };

            DialogHost.OpenDialogCommand.Execute(null, MaintenanceQueueDialogHost);
            _maintenanceQueueTimer.Start();
        }

        private void QuitMaintenanceQueueButton_OnClick(object sender, RoutedEventArgs e)
        {
            _maintenanceQueueTimer.Stop();
            DialogHost.CloseDialogCommand.Execute(null, MaintenanceQueueDialogHost);
        }

        private void OtpTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                DialogHost.CloseDialogCommand.Execute(null, OtpDialogHost);
                StartGame();
            }
        }

        private void OtpTextBox_Click(object sender, RoutedEventArgs e)
        {
            DialogHost.CloseDialogCommand.Execute(null, OtpDialogHost);
            StartGame();
        }

        private void Card_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return || _isLoggingIn)
                return;

            HandleLogin(false);
            _isLoggingIn = true;
        }

        private void OtpDialogHost_OnDialogClosing(object sender, DialogClosingEventArgs eventargs)
        {
            _isLoggingIn = false;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            StopServiceAddons();
        }
    }
}
